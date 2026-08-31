using System.Diagnostics;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Navigation;

internal sealed class RouterRequestResolver(
    RouteTable routes,
    IReadOnlyList<INavigationRequestTransformer> requestTransformers,
    IReadOnlyList<INavigationRequestPolicy> requestPolicies,
    Func<NavigationFallbackContext, AppRoute?>? fallbackRouteFactory,
    int maxRedirects,
    RouterNavigationDiagnostics diagnostics)
{
    private readonly RouteTable _routes = routes ?? throw new ArgumentNullException(nameof(routes));

    private readonly IReadOnlyList<INavigationRequestTransformer> _requestTransformers =
        requestTransformers ?? throw new ArgumentNullException(nameof(requestTransformers));

    private readonly IReadOnlyList<INavigationRequestPolicy> _requestPolicies =
        requestPolicies ?? throw new ArgumentNullException(nameof(requestPolicies));

    private readonly RouterNavigationDiagnostics _diagnostics =
        diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

    public async ValueTask<ResolvedNavigationRequest> ResolveAsync(
        RouterNavigationRequest initialRequest,
        NavigationState currentState,
        string operationId,
        CancellationToken cancellationToken)
    {
        ValidateTarget(initialRequest);

        RouterNavigationRequest effectiveRequest = initialRequest;
        var seenTargets = new HashSet<RequestTargetKey> { RequestTargetKey.From(effectiveRequest) };
        var redirects = new List<RouterNavigationRequest>();

        while (true)
        {
            RequestStageResult transformResult = await ApplyTransformersAsync(
                initialRequest,
                effectiveRequest,
                currentState,
                operationId,
                redirects,
                seenTargets,
                cancellationToken).ConfigureAwait(false);
            effectiveRequest = transformResult.Request;
            if (transformResult.Restarted)
                continue;

            ResolvedRoute resolvedRoute = ResolveRoute(effectiveRequest, currentState, operationId);
            AppRoute route = resolvedRoute.Route;

            Activity.Current?.SetTag("navigation.route_type", route.GetType().FullName);
            Activity.Current?.SetTag("navigation.route_template", resolvedRoute.Definition?.Template.Value);

            RequestStageResult policyResult = await ApplyPoliciesAsync(
                initialRequest,
                effectiveRequest,
                resolvedRoute,
                currentState,
                operationId,
                redirects,
                seenTargets,
                cancellationToken).ConfigureAwait(false);
            effectiveRequest = policyResult.Request;
            if (!policyResult.Restarted)
            {
                RouterNavigationRequest finalizedRequest = effectiveRequest with
                {
                    Metadata = MergeMetadata(resolvedRoute.Metadata, effectiveRequest.Metadata)
                };
                return new ResolvedNavigationRequest(finalizedRequest, route, resolvedRoute.Definition);
            }
        }
    }

    private async ValueTask<RequestStageResult> ApplyTransformersAsync(
        RouterNavigationRequest initialRequest,
        RouterNavigationRequest effectiveRequest,
        NavigationState currentState,
        string operationId,
        List<RouterNavigationRequest> redirects,
        HashSet<RequestTargetKey> seenTargets,
        CancellationToken cancellationToken)
    {
        foreach (INavigationRequestTransformer transformer in _requestTransformers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string transformerName = transformer.GetType().Name;
            string? transformerType = transformer.GetType().FullName;
            var timer = Stopwatch.StartNew();
            _diagnostics.Write(
                NavigationDiagnosticEventKind.RequestTransformStarted,
                operationId,
                transformerName,
                RouterNavigationDiagnostics.Data(
                    (NavigationDiagnosticDataKeys.RequestTransformerType, transformerType)));

            try
            {
                RouterNavigationRequest previousRequest = effectiveRequest;
                RequestTargetKey previousTarget = RequestTargetKey.From(previousRequest);
                RouterNavigationRequest? candidateRequest = await transformer.TransformAsync(
                    new NavigationRequestTransformContext(effectiveRequest, currentState, operationId),
                    cancellationToken).ConfigureAwait(false);

                if (candidateRequest is null)
                    throw new InvalidOperationException(
                        $"Request transformer '{transformer.GetType().FullName}' returned a null navigation request.");

                ValidateTarget(candidateRequest);
                RequestTargetKey candidateTarget = RequestTargetKey.From(candidateRequest);
                effectiveRequest = candidateRequest;

                _diagnostics.Write(
                    NavigationDiagnosticEventKind.RequestTransformCompleted,
                    operationId,
                    transformerName,
                    RouterNavigationDiagnostics.Duration(
                        timer,
                        (NavigationDiagnosticDataKeys.RequestTransformerType, transformerType)));

                if (candidateTarget == previousTarget)
                    continue;

                RecordRedirect(
                    operationId,
                    initialRequest,
                    previousRequest,
                    candidateRequest,
                    redirects,
                    seenTargets,
                    transformerName,
                    NavigationDiagnosticDataKeys.RequestTransformerType,
                    transformerType,
                    NavigationDiagnosticPhase.RequestTransformation);
                return new RequestStageResult(effectiveRequest, true);
            }
            catch (Exception ex)
            {
                _diagnostics.WriteFailure(
                    NavigationDiagnosticEventKind.RequestTransformFailed,
                    operationId,
                    transformerName,
                    ex,
                    timer,
                    (NavigationDiagnosticDataKeys.RequestTransformerType, transformerType));
                throw;
            }
        }

        return new RequestStageResult(effectiveRequest, false);
    }

    private async ValueTask<RequestStageResult> ApplyPoliciesAsync(
        RouterNavigationRequest initialRequest,
        RouterNavigationRequest effectiveRequest,
        ResolvedRoute resolvedRoute,
        NavigationState currentState,
        string operationId,
        List<RouterNavigationRequest> redirects,
        HashSet<RequestTargetKey> seenTargets,
        CancellationToken cancellationToken)
    {
        foreach (INavigationRequestPolicy policy in _requestPolicies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string policyName = policy.GetType().Name;
            string? policyType = policy.GetType().FullName;
            var timer = Stopwatch.StartNew();
            _diagnostics.Write(
                NavigationDiagnosticEventKind.RequestPolicyStarted,
                operationId,
                policyName,
                RouterNavigationDiagnostics.Data((NavigationDiagnosticDataKeys.PolicyType, policyType)));

            try
            {
                RouterNavigationRequest previousRequest = effectiveRequest;
                RequestTargetKey previousTarget = RequestTargetKey.From(previousRequest);
                RouterNavigationRequest? candidateRequest = await policy.ApplyAsync(
                    new NavigationRequestPolicyContext(
                        effectiveRequest,
                        resolvedRoute.Route,
                        resolvedRoute.Metadata,
                        currentState,
                        operationId),
                    cancellationToken).ConfigureAwait(false);

                if (candidateRequest is null)
                    throw new InvalidOperationException(
                        $"Request policy '{policy.GetType().FullName}' returned a null navigation request.");

                ValidateTarget(candidateRequest);
                RequestTargetKey candidateTarget = RequestTargetKey.From(candidateRequest);
                effectiveRequest = candidateRequest;

                _diagnostics.Write(
                    NavigationDiagnosticEventKind.RequestPolicyCompleted,
                    operationId,
                    policyName,
                    RouterNavigationDiagnostics.Duration(timer,
                        (NavigationDiagnosticDataKeys.PolicyType, policyType)));

                if (candidateTarget == previousTarget)
                    continue;

                RecordRedirect(
                    operationId,
                    initialRequest,
                    previousRequest,
                    candidateRequest,
                    redirects,
                    seenTargets,
                    policyName,
                    NavigationDiagnosticDataKeys.PolicyType,
                    policyType,
                    NavigationDiagnosticPhase.RequestPolicy);
                return new RequestStageResult(effectiveRequest, true);
            }
            catch (Exception ex)
            {
                _diagnostics.WriteFailure(
                    NavigationDiagnosticEventKind.RequestPolicyFailed,
                    operationId,
                    policyName,
                    ex,
                    timer,
                    (NavigationDiagnosticDataKeys.PolicyType, policyType));
                throw;
            }
        }

        return new RequestStageResult(effectiveRequest, false);
    }

    private void RecordRedirect(
        string operationId,
        RouterNavigationRequest initialRequest,
        RouterNavigationRequest previousRequest,
        RouterNavigationRequest candidateRequest,
        List<RouterNavigationRequest> redirects,
        HashSet<RequestTargetKey> seenTargets,
        string componentName,
        string componentTypeKey,
        string? componentType,
        NavigationDiagnosticPhase phase)
    {
        redirects.Add(candidateRequest);
        int redirectCount = redirects.Count;
        string redirectTrace = RouterNavigationDiagnostics.BuildRedirectTrace(initialRequest, redirects);

        if (redirectCount > maxRedirects)
        {
            string message = maxRedirects == 0
                ? "Request redirects are disabled because MaxRedirects is 0."
                : $"Request redirect limit of {maxRedirects} was exceeded.";
            _diagnostics.WriteRedirectLoopDetected(
                operationId,
                componentTypeKey,
                componentType,
                previousRequest,
                candidateRequest,
                redirectCount,
                redirectTrace,
                message,
                phase);
            throw new RouteRedirectLoopException(initialRequest, candidateRequest, redirects, message);
        }

        if (!seenTargets.Add(RequestTargetKey.From(candidateRequest)))
        {
            var message = $"Request redirect loop detected after {redirectCount} redirects.";
            _diagnostics.WriteRedirectLoopDetected(
                operationId,
                componentTypeKey,
                componentType,
                previousRequest,
                candidateRequest,
                redirectCount,
                redirectTrace,
                message,
                phase);
            throw new RouteRedirectLoopException(initialRequest, candidateRequest, redirects, message);
        }

        Activity.Current?.SetTag("navigation.redirect_count", redirectCount);
        _diagnostics.Write(
            NavigationDiagnosticEventKind.RequestRedirected,
            operationId,
            componentName,
            RouterNavigationDiagnostics.RequestData(
                candidateRequest,
                (componentTypeKey, componentType),
                (NavigationDiagnosticDataKeys.RedirectCount, redirectCount),
                (NavigationDiagnosticDataKeys.RedirectFrom,
                    RouterNavigationDiagnostics.DescribeRedirectTarget(previousRequest)),
                (NavigationDiagnosticDataKeys.RedirectTo,
                    RouterNavigationDiagnostics.DescribeRedirectTarget(candidateRequest)),
                (NavigationDiagnosticDataKeys.RedirectTrace, redirectTrace)),
            phase);
    }

    private ResolvedRoute ResolveRoute(
        RouterNavigationRequest request,
        NavigationState currentState,
        string operationId)
    {
        if (request.Route is not null)
            return new ResolvedRoute(request.Route, null);

        if (request.Uri is null)
            throw new InvalidOperationException("RouterNavigationRequest must contain either a Uri or an AppRoute.");

        var timer = Stopwatch.StartNew();
        _diagnostics.Write(
            NavigationDiagnosticEventKind.RouteMatchingStarted,
            operationId,
            request.Uri.ToString(),
            RouterNavigationDiagnostics.RequestData(
                request,
                (NavigationDiagnosticDataKeys.Uri, request.Uri.ToString()),
                (NavigationDiagnosticDataKeys.RequestSource, request.Source.ToString())));

        try
        {
            RouteMatchResult match = _routes.Match(request.Uri);
            if (!match.IsSuccess || match.Route is null)
            {
                RouteDiagnostic? diagnostic = match.Diagnostics.Count > 0 ? match.Diagnostics[0] : null;
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.RouteNotMatched,
                    operationId,
                    request.Uri.ToString(),
                    RouterNavigationDiagnostics.RouteFailureData(timer, request, diagnostic));

                if (diagnostic?.Code != "route.not_matched" || fallbackRouteFactory is null)
                    throw new RouteNotMatchedException(request.Uri, match.Diagnostics);

                var fallbackTimer = Stopwatch.StartNew();
                AppRoute? fallbackRoute = fallbackRouteFactory(new NavigationFallbackContext(
                    request,
                    match.Diagnostics,
                    currentState,
                    operationId));

                if (fallbackRoute is null)
                    throw new RouteNotMatchedException(request.Uri, match.Diagnostics);

                Activity.Current?.SetTag("navigation.route_type", fallbackRoute.GetType().FullName);
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.RouteFallbackSelected,
                    operationId,
                    fallbackRoute.GetType().Name,
                    RouterNavigationDiagnostics.Duration(
                        fallbackTimer,
                        request,
                        (NavigationDiagnosticDataKeys.Uri, request.Uri.ToString()),
                        (NavigationDiagnosticDataKeys.RouteDiagnosticCode, diagnostic.Code),
                        (NavigationDiagnosticDataKeys.RouteDiagnosticMessage, diagnostic.Message),
                        (NavigationDiagnosticDataKeys.RouteType, fallbackRoute.GetType().FullName)));

                return new ResolvedRoute(fallbackRoute, null);
            }

            Activity.Current?.SetTag("navigation.route_type", match.Route.GetType().FullName);
            string? routeTemplate = match.Definition?.Template.Value;
            Activity.Current?.SetTag("navigation.route_template", routeTemplate);
            _diagnostics.Write(
                NavigationDiagnosticEventKind.RouteMatched,
                operationId,
                match.Route.GetType().Name,
                RouterNavigationDiagnostics.Duration(
                    timer,
                    request,
                    (NavigationDiagnosticDataKeys.Uri, request.Uri.ToString()),
                    (NavigationDiagnosticDataKeys.RouteType, match.Route.GetType().FullName),
                    (NavigationDiagnosticDataKeys.RouteTemplate, routeTemplate)));
            return new ResolvedRoute(match.Route, match.Definition, match.Metadata);
        }
        catch (Exception ex) when (ex is not RouteNotMatchedException)
        {
            _diagnostics.WriteFailure(
                NavigationDiagnosticEventKind.RouteMatchingFailed,
                operationId,
                request.Uri.ToString(),
                ex,
                timer,
                (NavigationDiagnosticDataKeys.Uri, request.Uri.ToString()));
            throw;
        }
    }

    private static Dictionary<string, object?> MergeMetadata(
        IReadOnlyDictionary<string, object?>? lowerPriority,
        IReadOnlyDictionary<string, object?>? higherPriority)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (lowerPriority is not null)
            foreach (KeyValuePair<string, object?> pair in lowerPriority)
                merged[pair.Key] = pair.Value;

        if (higherPriority is null)
            return merged;

        foreach (KeyValuePair<string, object?> pair in higherPriority)
            merged[pair.Key] = pair.Value;

        return merged;
    }

    private static void ValidateTarget(RouterNavigationRequest request)
    {
        bool hasUri = request.Uri is not null;
        bool hasRoute = request.Route is not null;
        if (hasUri == hasRoute)
            throw new InvalidOperationException(
                "RouterNavigationRequest must contain exactly one URI or application-route target.");
    }

    private readonly record struct RequestStageResult(
        RouterNavigationRequest Request,
        bool Restarted);
}

internal sealed record ResolvedRoute(
    AppRoute Route,
    RouteDefinition? Definition,
    IReadOnlyDictionary<string, object?>? Metadata = null);

internal sealed record ResolvedNavigationRequest(
    RouterNavigationRequest Request,
    AppRoute Route,
    RouteDefinition? Definition);

internal readonly record struct RequestTargetKey(
    string? Uri,
    AppRoute? Route)
{
    public static RequestTargetKey From(RouterNavigationRequest request)
    {
        return new RequestTargetKey(
            request.Uri?.ToString(),
            request.Route);
    }
}

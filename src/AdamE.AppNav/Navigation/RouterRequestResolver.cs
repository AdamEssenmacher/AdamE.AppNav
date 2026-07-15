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
            var restarted = false;

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

                    candidateRequest = PreserveProvenance(previousRequest, candidateRequest);
                    candidateRequest = InheritDisposition(previousRequest, candidateRequest);
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
                    restarted = true;
                    break;
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

            if (restarted)
                continue;

            ResolvedRoute resolvedRoute = ResolveRoute(effectiveRequest, currentState, operationId);
            AppRoute route = resolvedRoute.Route;
            effectiveRequest = effectiveRequest with
            {
                Metadata = MergeMetadata(resolvedRoute.Metadata, effectiveRequest.Metadata)
            };

            Activity.Current?.SetTag("navigation.route_type", route.GetType().FullName);
            Activity.Current?.SetTag("navigation.route_template", resolvedRoute.Definition?.Template.Value);

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
                        new NavigationRequestPolicyContext(effectiveRequest, route, currentState, operationId),
                        cancellationToken).ConfigureAwait(false);

                    if (candidateRequest is null)
                        throw new InvalidOperationException(
                            $"Request policy '{policy.GetType().FullName}' returned a null navigation request.");

                    candidateRequest = PreserveProvenance(previousRequest, candidateRequest);
                    candidateRequest = InheritDisposition(previousRequest, candidateRequest);
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

                    restarted = true;
                    break;
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

            if (!restarted)
                return new ResolvedNavigationRequest(effectiveRequest, route, resolvedRoute.Definition);
        }
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
        Activity.Current?.SetTag("navigation.redirect_from",
            RouterNavigationDiagnostics.DescribeRedirectTarget(previousRequest));
        Activity.Current?.SetTag("navigation.redirect_to",
            RouterNavigationDiagnostics.DescribeRedirectTarget(candidateRequest));
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

    private static RouterNavigationRequest PreserveProvenance(
        RouterNavigationRequest originalRequest,
        RouterNavigationRequest candidateRequest)
    {
        return candidateRequest.Provenance is null && originalRequest.Provenance is not null
            ? candidateRequest with { Provenance = originalRequest.Provenance }
            : candidateRequest;
    }

    private static RouterNavigationRequest InheritDisposition(
        RouterNavigationRequest originalRequest,
        RouterNavigationRequest candidateRequest)
    {
        return candidateRequest.Disposition == RouterNavigationDisposition.Auto &&
               originalRequest.Disposition != RouterNavigationDisposition.Auto
            ? candidateRequest with { Disposition = originalRequest.Disposition }
            : candidateRequest;
    }

    private static void ValidateTarget(RouterNavigationRequest request)
    {
        bool hasUri = request.Uri is not null;
        bool hasRoute = request.Route is not null;
        if (hasUri == hasRoute)
            throw new InvalidOperationException(
                "RouterNavigationRequest must contain exactly one URI or application-route target.");
    }
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

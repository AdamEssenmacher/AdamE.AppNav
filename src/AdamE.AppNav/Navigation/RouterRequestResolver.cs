using System.Diagnostics;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Navigation;

internal sealed class RouterRequestResolver(
    RouteTable routes,
    IReadOnlyList<INavigationRequestPolicy> requestPolicies,
    Func<NavigationFallbackContext, AppRoute?>? fallbackRouteFactory,
    int maxRedirects,
    RouterNavigationDiagnostics diagnostics)
{
    private readonly RouteTable _routes = routes ?? throw new ArgumentNullException(nameof(routes));

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
        ResolvedRoute resolvedRoute = ResolveRoute(initialRequest, currentState, operationId);
        AppRoute route = resolvedRoute.Route;
        RouterNavigationRequest effectiveRequest = initialRequest with
        {
            Route = route,
            Metadata = MergeMetadata(resolvedRoute.Metadata, initialRequest.Metadata)
        };
        RouterNavigationRequest initialEffectiveRequest = effectiveRequest;
        var seenTargets = new HashSet<RedirectTargetKey> { RedirectTargetKey.From(effectiveRequest) };
        var redirects = new List<RouterNavigationRequest>();

        Activity.Current?.SetTag("navigation.route_type", route.GetType().FullName);
        Activity.Current?.SetTag("navigation.route_template", resolvedRoute.Definition?.Template.Value);

        while (true)
        {
            var restarted = false;

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
                    RedirectTargetKey previousTarget = RedirectTargetKey.From(previousRequest);
                    RouterNavigationRequest? candidateRequest = await policy.ApplyAsync(
                        new NavigationRequestPolicyContext(effectiveRequest, route, currentState, operationId),
                        cancellationToken).ConfigureAwait(false);

                    if (candidateRequest is null)
                        throw new InvalidOperationException(
                            $"Request policy '{policy.GetType().FullName}' returned a null navigation request.");

                    candidateRequest = PreserveProvenance(previousRequest, candidateRequest);
                    ResolvedRoute candidateResolvedRoute = ResolveRoute(candidateRequest, currentState, operationId);
                    RouterNavigationRequest candidateEffectiveRequest = candidateRequest with
                    {
                        Route = candidateResolvedRoute.Route,
                        Disposition = candidateRequest.Disposition == RouterNavigationDisposition.Auto
                            ? effectiveRequest.Disposition
                            : candidateRequest.Disposition,
                        Metadata = MergeMetadata(candidateResolvedRoute.Metadata, candidateRequest.Metadata)
                    };
                    RedirectTargetKey candidateTarget = RedirectTargetKey.From(candidateEffectiveRequest);
                    route = candidateResolvedRoute.Route;
                    resolvedRoute = candidateResolvedRoute;
                    effectiveRequest = candidateEffectiveRequest;

                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.RequestPolicyCompleted,
                        operationId,
                        policyName,
                        RouterNavigationDiagnostics.Duration(timer,
                            (NavigationDiagnosticDataKeys.PolicyType, policyType)));

                    if (candidateTarget == previousTarget) continue;

                    redirects.Add(candidateEffectiveRequest);
                    int redirectCount = redirects.Count;
                    string redirectTrace =
                        RouterNavigationDiagnostics.BuildRedirectTrace(initialEffectiveRequest, redirects);

                    if (redirectCount > maxRedirects)
                    {
                        string message = maxRedirects == 0
                            ? "Request policy redirects are disabled because MaxRedirects is 0."
                            : $"Request policy redirect limit of {maxRedirects} was exceeded.";
                        _diagnostics.WriteRedirectLoopDetected(
                            operationId,
                            policyType,
                            previousRequest,
                            candidateEffectiveRequest,
                            redirectCount,
                            redirectTrace,
                            message);
                        throw new RouteRedirectLoopException(
                            initialRequest,
                            candidateEffectiveRequest,
                            redirects,
                            message);
                    }

                    if (!seenTargets.Add(candidateTarget))
                    {
                        var message = $"Request policy redirect loop detected after {redirectCount} redirects.";
                        _diagnostics.WriteRedirectLoopDetected(
                            operationId,
                            policyType,
                            previousRequest,
                            candidateEffectiveRequest,
                            redirectCount,
                            redirectTrace,
                            message);
                        throw new RouteRedirectLoopException(
                            initialRequest,
                            candidateEffectiveRequest,
                            redirects,
                            message);
                    }

                    Activity.Current?.SetTag("navigation.redirect_count", redirectCount);
                    Activity.Current?.SetTag("navigation.redirect_from",
                        RouterNavigationDiagnostics.DescribeRedirectTarget(previousRequest));
                    Activity.Current?.SetTag("navigation.redirect_to",
                        RouterNavigationDiagnostics.DescribeRedirectTarget(candidateEffectiveRequest));
                    Activity.Current?.SetTag("navigation.route_type", route.GetType().FullName);
                    Activity.Current?.SetTag("navigation.route_template", resolvedRoute.Definition?.Template.Value);
                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.RequestRedirected,
                        operationId,
                        policyName,
                        RouterNavigationDiagnostics.RequestData(
                            candidateEffectiveRequest,
                            (NavigationDiagnosticDataKeys.PolicyType, policyType),
                            (NavigationDiagnosticDataKeys.RedirectCount, redirectCount),
                            (NavigationDiagnosticDataKeys.RedirectFrom,
                                RouterNavigationDiagnostics.DescribeRedirectTarget(previousRequest)),
                            (NavigationDiagnosticDataKeys.RedirectTo,
                                RouterNavigationDiagnostics.DescribeRedirectTarget(candidateEffectiveRequest)),
                            (NavigationDiagnosticDataKeys.RedirectTrace, redirectTrace)));

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
}

internal sealed record ResolvedRoute(
    AppRoute Route,
    RouteDefinition? Definition,
    IReadOnlyDictionary<string, object?>? Metadata = null);

internal sealed record ResolvedNavigationRequest(
    RouterNavigationRequest Request,
    AppRoute Route,
    RouteDefinition? Definition);

internal readonly record struct RedirectTargetKey(
    string? Uri,
    AppRoute? Route,
    NavigationRequestSource Source,
    RouterNavigationDisposition Disposition,
    string? WindowId)
{
    public static RedirectTargetKey From(RouterNavigationRequest request)
    {
        return new RedirectTargetKey(
            request.Uri?.ToString(),
            request.Route,
            request.Source,
            request.Disposition,
            request.WindowId);
    }
}

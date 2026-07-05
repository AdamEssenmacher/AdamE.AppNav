using System.Diagnostics;
using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;
using Microsoft.Extensions.Logging;

namespace AdamE.MauiRouter.Navigation;

internal sealed class RouterNavigator : IRouterNavigator, IDisposable
{
    private readonly RouteTable _routes;
    private readonly IAppNavigationPlanner _planner;
    private readonly INavigationPresenter _presenter;
    private readonly IBackNavigator _backNavigator;
    private readonly IReadOnlyList<INavigationRequestPolicy> _requestPolicies;
    private readonly IReadOnlyList<INavigationPlanPolicy> _planPolicies;
    private readonly Func<NavigationFallbackContext, AppRoute?>? _fallbackRouteFactory;
    private readonly NavigationDiagnostics _diagnostics;
    private readonly ActivitySource _activitySource;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _reconciliationGate = new();
    private readonly object _navigationCommittedGate = new();
    private readonly Queue<NavigationCommittedEventArgs> _pendingNavigationCommittedEvents = new();
    private readonly int _maxRedirects;
    private readonly int _maxHistoryEntries;
    private readonly NavigationPersistenceOptions? _persistence;
    private readonly NavigationSnapshotSerializer? _snapshotSerializer;
    private Task _reconciliationQueue = Task.CompletedTask;
    private bool _publishingNavigationCommitted;
    private bool _disposed;

    public RouterNavigator(
        RouteTable routes,
        IAppNavigationPlanner planner,
        INavigationPresenter presenter,
        RouterNavigatorOptions? options = null)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        options ??= new RouterNavigatorOptions();

        if (options.MaxHistoryEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxHistoryEntries cannot be negative.");
        }

        if (options.MaxRedirects < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRedirects cannot be negative.");
        }

        CurrentState = options.InitialState ?? NavigationState.Empty;
        History = options.InitialHistory ?? NavigationHistory.Empty;
        _requestPolicies = options.RequestPolicies.ToArray();
        _planPolicies = options.PlanPolicies.ToArray();
        _fallbackRouteFactory = options.FallbackRouteFactory;
        var logger = options.Logger ?? options.LoggerFactory?.CreateLogger<RouterNavigator>();
        _diagnostics = options.Diagnostics ?? new NavigationDiagnostics(logger);
        _backNavigator = options.BackNavigator ?? new DefaultBackNavigator(diagnostics: _diagnostics);
        _maxRedirects = options.MaxRedirects;
        _maxHistoryEntries = options.MaxHistoryEntries;
        _persistence = options.Persistence;
        _snapshotSerializer = _persistence is null ? null : new NavigationSnapshotSerializer(_routes, _persistence);
        _activitySource = NavigationActivitySources.Default;

        _presenter.ReconciliationRequested += OnPresenterReconciliationRequested;
    }

    public NavigationState CurrentState { get; private set; }

    public NavigationHistory History { get; private set; }

    public event EventHandler<NavigationCommittedEventArgs>? NavigationCommitted;

    public ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default)
    {
        return NavigateAsync(RouterNavigationRequest.FromUri(uri, source), cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return NavigateAsync(RouterNavigationRequest.FromUri(uri, source, disposition: disposition), cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return NavigateAsync(uri, NavigationRequestSource.InAppCommand, disposition, cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default)
    {
        return NavigateAsync(RouterNavigationRequest.FromRoute(route, source), cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return NavigateAsync(RouterNavigationRequest.FromRoute(route, source, disposition: disposition), cancellationToken);
    }

    public ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return NavigateAsync(route, NavigationRequestSource.InAppCommand, disposition, cancellationToken);
    }

    /// <summary>
    /// Navigates using an app-facing route request and an explicit request source.
    /// </summary>
    public ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default)
    {
        return NavigateAsync(RouterNavigationRequest.FromRouteRequest(routeRequest, source), cancellationToken);
    }

    /// <summary>
    /// Navigates using an app-facing route request, explicit request source, and explicit disposition.
    /// </summary>
    public ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return NavigateAsync(
            RouterNavigationRequest.FromRouteRequest(routeRequest, source, disposition: disposition),
            cancellationToken);
    }

    /// <summary>
    /// Navigates using an app-facing route request and an explicit disposition.
    /// </summary>
    public ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        return NavigateAsync(routeRequest, NavigationRequestSource.InAppCommand, disposition, cancellationToken);
    }

    public async ValueTask<NavigationResult> NavigateAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        NavigationOperationResult<NavigationResult> operation;
        try
        {
            operation = await NavigateCoreAsync(request, cancellationToken).ConfigureAwait(false);
            EnqueueNavigationCommitted(operation.Committed);
        }
        finally
        {
            _operationLock.Release();
        }

        DrainNavigationCommitted();
        return operation.Result;
    }

    public async ValueTask<BackNavigationResult> BackAsync(
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        NavigationOperationResult<BackNavigationResult> operation;
        try
        {
            operation = await BackCoreAsync(windowId, cancellationToken).ConfigureAwait(false);
            EnqueueNavigationCommitted(operation.Committed);
        }
        finally
        {
            _operationLock.Release();
        }

        DrainNavigationCommitted();
        return operation.Result;
    }

    public async ValueTask<NavigationResult> ReconcileAsync(
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        NavigationOperationResult<NavigationResult> operation;
        try
        {
            var request = RouterNavigationRequest.FromRoute(
                reconciliation.Route ?? new ReconciledRoute(reconciliation.Source.ToString()),
                NavigationRequestSource.NativeReconciliation);

            operation = await ReconcileCoreAsync(request, reconciliation, cancellationToken).ConfigureAwait(false);
            EnqueueNavigationCommitted(operation.Committed);
        }
        finally
        {
            _operationLock.Release();
        }

        DrainNavigationCommitted();
        return operation.Result;
    }

    public async ValueTask<NavigationRestoreResult> RestoreAsync(
        NavigationSnapshot snapshot,
        NavigationRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        NavigationOperationResult<NavigationRestoreResult> operation;
        try
        {
            operation = await RestoreCoreAsync(snapshot, options ?? new NavigationRestoreOptions(), cancellationToken)
                .ConfigureAwait(false);
            EnqueueNavigationCommitted(operation.Committed);
        }
        finally
        {
            _operationLock.Release();
        }

        DrainNavigationCommitted();
        return operation.Result;
    }

    public async ValueTask<NavigationRestoreResult> RestoreFromStoreAsync(
        NavigationRestoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        NavigationOperationResult<NavigationRestoreResult> operation;
        try
        {
            operation = await RestoreFromStoreCoreAsync(options ?? new NavigationRestoreOptions(), cancellationToken)
                .ConfigureAwait(false);
            EnqueueNavigationCommitted(operation.Committed);
        }
        finally
        {
            _operationLock.Release();
        }

        DrainNavigationCommitted();
        return operation.Result;
    }

    internal Task WhenReconciliationIdleAsync()
    {
        lock (_reconciliationGate)
        {
            return _reconciliationQueue;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _presenter.ReconciliationRequested -= OnPresenterReconciliationRequested;
        _operationLock.Dispose();
        _disposed = true;
    }

    private async ValueTask<NavigationOperationResult<NavigationResult>> NavigateCoreAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using var activity = StartActivity("Navigation.Navigate", operationId, request);
        var operationTimer = Stopwatch.StartNew();
        AppRoute? route = null;

        try
        {
            var resolvedRequest = await ResolveRequestWithPoliciesAsync(
                request,
                operationId,
                cancellationToken).ConfigureAwait(false);
            route = resolvedRequest.Route;
            var effectiveRequest = resolvedRequest.Request;
            Activity.Current?.SetTag("navigation.route_type", route.GetType().FullName);
            Activity.Current?.SetTag("navigation.route_template", resolvedRequest.Definition?.Template.Value);
            Activity.Current?.SetTag("navigation.disposition", effectiveRequest.Disposition.ToString());

            var planningTimer = Stopwatch.StartNew();
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PlanningStarted,
                operationId,
                route.GetType().Name,
                RequestData(
                    effectiveRequest,
                    (NavigationDiagnosticDataKeys.RouteType, route.GetType().FullName),
                    (NavigationDiagnosticDataKeys.RequestDisposition, effectiveRequest.Disposition.ToString())));
            NavigationPlan plan;
            try
            {
                plan = await _planner.CreatePlanAsync(
                    new NavigationPlanningContext(effectiveRequest, route, CurrentState, operationId),
                    cancellationToken).ConfigureAwait(false);
                Activity.Current?.SetTag("navigation.plan_kind", plan.Kind.ToString());
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.PlanningCompleted,
                    operationId,
                    plan.Kind.ToString(),
                    Duration(planningTimer, (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            }
            catch (Exception ex)
            {
                WriteFailure(
                    NavigationDiagnosticEventKind.PlanningFailed,
                    operationId,
                    route.GetType().Name,
                    ex,
                    planningTimer,
                    (NavigationDiagnosticDataKeys.RouteType, route.GetType().FullName));
                throw;
            }

            foreach (var policy in _planPolicies)
            {
                var policyName = policy.GetType().Name;
                var timer = Stopwatch.StartNew();
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.PlanPolicyStarted,
                    operationId,
                    policyName,
                    Data((NavigationDiagnosticDataKeys.PolicyType, policy.GetType().FullName)));

                try
                {
                    plan = await policy.ApplyAsync(
                        new NavigationPlanPolicyContext(effectiveRequest, route, CurrentState, operationId),
                        plan,
                        cancellationToken).ConfigureAwait(false);
                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.PlanPolicyCompleted,
                        operationId,
                        policyName,
                        Duration(timer, (NavigationDiagnosticDataKeys.PolicyType, policy.GetType().FullName)));
                }
                catch (Exception ex)
                {
                    WriteFailure(
                        NavigationDiagnosticEventKind.PlanPolicyFailed,
                        operationId,
                        policyName,
                        ex,
                        timer,
                        (NavigationDiagnosticDataKeys.PolicyType, policy.GetType().FullName));
                    throw;
                }
            }

            var finalRoute = ResolvePresentedRoute(plan.TargetState, effectiveRequest.WindowId, route);
            var finalizedRequest = effectiveRequest with { Route = finalRoute };
            var presentationTimer = Stopwatch.StartNew();
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationStarted,
                operationId,
                plan.Kind.ToString(),
                Data((NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            try
            {
                await _presenter.ApplyAsync(
                    plan,
                    new NavigationPresentationContext(finalizedRequest, finalRoute, CurrentState, operationId),
                    cancellationToken).ConfigureAwait(false);
                _diagnostics.Write(
                        NavigationDiagnosticEventKind.PresentationCompleted,
                        operationId,
                        plan.Kind.ToString(),
                        Duration(presentationTimer, (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            }
            catch (Exception ex)
            {
                WriteFailure(
                    NavigationDiagnosticEventKind.PresentationFailed,
                    operationId,
                    plan.Kind.ToString(),
                    ex,
                    presentationTimer,
                    (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString()));
                throw;
            }

            var previousState = CurrentState;
            CurrentState = plan.TargetState;
            History = History.Push(
                CreateHistoryEntry(operationId, finalizedRequest, finalRoute, CurrentState, plan.Reason),
                _maxHistoryEntries);
            await SaveSnapshotIfConfiguredAsync(operationId, cancellationToken).ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);

            var result = new NavigationResult(finalRoute, plan, CurrentState, Presented: true);
            return new NavigationOperationResult<NavigationResult>(
                result,
                CreateNavigationCommittedEventArgs(
                    operationId,
                    NavigationCommitKind.Navigate,
                    finalizedRequest,
                    finalRoute,
                    plan,
                    previousState,
                    CurrentState,
                    History,
                    presented: true));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            WriteFailure(
                NavigationDiagnosticEventKind.NavigationFailed,
                operationId,
                route?.GetType().Name ?? request.Uri?.ToString() ?? request.Source.ToString(),
                ex,
                operationTimer);
            throw;
        }
    }

    private async ValueTask<ResolvedNavigationRequest> ResolveRequestWithPoliciesAsync(
        RouterNavigationRequest initialRequest,
        string operationId,
        CancellationToken cancellationToken)
    {
        var resolvedRoute = ResolveRoute(initialRequest, operationId);
        var route = resolvedRoute.Route;
        var effectiveRequest = initialRequest with
        {
            Route = route,
            Metadata = MergeMetadata(resolvedRoute.Metadata, initialRequest.Metadata)
        };
        var initialEffectiveRequest = effectiveRequest;
        var seenTargets = new HashSet<RedirectTargetKey> { RedirectTargetKey.From(effectiveRequest) };
        var redirects = new List<RouterNavigationRequest>();

        Activity.Current?.SetTag("navigation.route_type", route.GetType().FullName);
        Activity.Current?.SetTag("navigation.route_template", resolvedRoute.Definition?.Template.Value);

        while (true)
        {
            var restarted = false;

            foreach (var policy in _requestPolicies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var policyName = policy.GetType().Name;
                var policyType = policy.GetType().FullName;
                var timer = Stopwatch.StartNew();
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.RequestPolicyStarted,
                    operationId,
                    policyName,
                    Data((NavigationDiagnosticDataKeys.PolicyType, policyType)));

                try
                {
                    var previousRequest = effectiveRequest;
                    var previousTarget = RedirectTargetKey.From(previousRequest);
                    var candidateRequest = await policy.ApplyAsync(
                        new NavigationRequestPolicyContext(effectiveRequest, route, CurrentState, operationId),
                        effectiveRequest,
                        cancellationToken).ConfigureAwait(false);

                    if (candidateRequest is null)
                    {
                        throw new InvalidOperationException(
                            $"Request policy '{policy.GetType().FullName}' returned a null navigation request.");
                    }

                    candidateRequest = PreserveProvenance(previousRequest, candidateRequest);
                    var candidateResolvedRoute = ResolveRoute(candidateRequest, operationId);
                    var candidateEffectiveRequest = candidateRequest with
                    {
                        Route = candidateResolvedRoute.Route,
                        Disposition = candidateRequest.Disposition == RouterNavigationDisposition.Auto
                            ? effectiveRequest.Disposition
                            : candidateRequest.Disposition,
                        Metadata = MergeMetadata(candidateResolvedRoute.Metadata, candidateRequest.Metadata)
                    };
                    var candidateTarget = RedirectTargetKey.From(candidateEffectiveRequest);
                    route = candidateResolvedRoute.Route;
                    resolvedRoute = candidateResolvedRoute;
                    effectiveRequest = candidateEffectiveRequest;

                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.RequestPolicyCompleted,
                        operationId,
                        policyName,
                        Duration(timer, (NavigationDiagnosticDataKeys.PolicyType, policyType)));

                    if (candidateTarget == previousTarget)
                    {
                        continue;
                    }

                    redirects.Add(candidateEffectiveRequest);
                    var redirectCount = redirects.Count;
                    var redirectTrace = BuildRedirectTrace(initialEffectiveRequest, redirects);

                    if (redirectCount > _maxRedirects)
                    {
                        var message = _maxRedirects == 0
                            ? "Request policy redirects are disabled because MaxRedirects is 0."
                            : $"Request policy redirect limit of {_maxRedirects} was exceeded.";
                        WriteRedirectLoopDetected(
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
                        WriteRedirectLoopDetected(
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
                    Activity.Current?.SetTag("navigation.redirect_from", DescribeRedirectTarget(previousRequest));
                    Activity.Current?.SetTag("navigation.redirect_to", DescribeRedirectTarget(candidateEffectiveRequest));
                    Activity.Current?.SetTag("navigation.route_type", route.GetType().FullName);
                    Activity.Current?.SetTag("navigation.route_template", resolvedRoute.Definition?.Template.Value);
                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.RequestRedirected,
                        operationId,
                        policyName,
                        RequestData(
                            candidateEffectiveRequest,
                            (NavigationDiagnosticDataKeys.PolicyType, policyType),
                            (NavigationDiagnosticDataKeys.RedirectCount, redirectCount),
                            (NavigationDiagnosticDataKeys.RedirectFrom, DescribeRedirectTarget(previousRequest)),
                            (NavigationDiagnosticDataKeys.RedirectTo, DescribeRedirectTarget(candidateEffectiveRequest)),
                            (NavigationDiagnosticDataKeys.RedirectTrace, redirectTrace)));

                    restarted = true;
                    break;
                }
                catch (Exception ex)
                {
                    WriteFailure(
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
            {
                return new ResolvedNavigationRequest(effectiveRequest, route, resolvedRoute.Definition);
            }
        }
    }

    private async ValueTask<NavigationOperationResult<BackNavigationResult>> BackCoreAsync(
        string? windowId,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using var activity = StartActivity("Navigation.Back", operationId, NavigationRequestSource.InAppCommand.ToString());
        var timer = Stopwatch.StartNew();

        var backContext = new BackNavigationContext(CurrentState, windowId, operationId);
        var diagnosticWindowId = backContext.ResolvedWindowId ?? backContext.RequestedWindowId ?? CurrentState.ActiveWindowId;
        var diagnosticWindowName = diagnosticWindowId ?? "active";

        _diagnostics.Write(
            NavigationDiagnosticEventKind.BackStarted,
            operationId,
            diagnosticWindowName,
            Data((NavigationDiagnosticDataKeys.WindowId, diagnosticWindowId)));

        try
        {
            var plan = _backNavigator.CreateBackPlan(backContext);
            if (plan is null)
            {
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.BackUnhandled,
                    operationId,
                    "No host accepted back navigation.",
                    Duration(timer, (NavigationDiagnosticDataKeys.WindowId, diagnosticWindowId)));
                activity?.SetStatus(ActivityStatusCode.Ok);
                return new NavigationOperationResult<BackNavigationResult>(
                    BackNavigationResult.Unhandled,
                    Committed: null);
            }

            var resolvedWindowId = backContext.ResolvedWindowId ?? backContext.RequestedWindowId;
            var route = ResolvePresentedRoute(plan.TargetState, resolvedWindowId, new BackRoute());
            var request = RouterNavigationRequest.FromRoute(route, NavigationRequestSource.InAppCommand, resolvedWindowId);

            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationStarted,
                operationId,
                plan.Kind.ToString(),
                Data((NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            var presentationTimer = Stopwatch.StartNew();
            try
            {
                await _presenter.ApplyAsync(
                    plan,
                    new NavigationPresentationContext(request, route, CurrentState, operationId),
                    cancellationToken).ConfigureAwait(false);
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.PresentationCompleted,
                    operationId,
                    plan.Kind.ToString(),
                    Duration(presentationTimer, (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            }
            catch (Exception ex)
            {
                WriteFailure(
                    NavigationDiagnosticEventKind.PresentationFailed,
                    operationId,
                    plan.Kind.ToString(),
                    ex,
                    presentationTimer,
                    (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString()));
                throw;
            }

            var previousState = CurrentState;
            CurrentState = plan.TargetState;
            History = History.Push(CreateHistoryEntry(operationId, request, route, CurrentState, plan.Reason), _maxHistoryEntries);
            await SaveSnapshotIfConfiguredAsync(operationId, cancellationToken).ConfigureAwait(false);

            _diagnostics.Write(
                NavigationDiagnosticEventKind.BackCompleted,
                operationId,
                plan.Reason ?? "Back handled.",
                Duration(timer, (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            activity?.SetStatus(ActivityStatusCode.Ok);
            var result = BackNavigationResult.HandledBy(new NavigationResult(route, plan, CurrentState, Presented: true));
            return new NavigationOperationResult<BackNavigationResult>(
                result,
                CreateNavigationCommittedEventArgs(
                    operationId,
                    NavigationCommitKind.Back,
                    request,
                    route,
                    plan,
                    previousState,
                    CurrentState,
                    History,
                    presented: true));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            WriteFailure(NavigationDiagnosticEventKind.BackFailed, operationId, windowId ?? "active", ex, timer);
            throw;
        }
    }

    private async ValueTask<NavigationOperationResult<NavigationResult>> ReconcileCoreAsync(
        RouterNavigationRequest request,
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using var activity = StartActivity("Navigation.Reconcile", operationId, reconciliation.Source.ToString());
        var timer = Stopwatch.StartNew();
        var route = request.Route ?? new ReconciledRoute(reconciliation.Source.ToString());
        var plan = new NavigationPlan(reconciliation.TargetState, NavigationPlanKind.Reconcile, reconciliation.Reason);

        _diagnostics.Write(
            NavigationDiagnosticEventKind.ReconciliationStarted,
            operationId,
            reconciliation.Source.ToString(),
            Data((NavigationDiagnosticDataKeys.ReconciliationSource, reconciliation.Source.ToString())));

        try
        {
            foreach (var policy in _planPolicies)
            {
                var policyName = policy.GetType().Name;
                var policyTimer = Stopwatch.StartNew();
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.PlanPolicyStarted,
                    operationId,
                    policyName,
                    Data((NavigationDiagnosticDataKeys.PolicyType, policy.GetType().FullName)));

                try
                {
                    plan = await policy.ApplyAsync(
                        new NavigationPlanPolicyContext(request, route, CurrentState, operationId),
                        plan,
                        cancellationToken).ConfigureAwait(false);
                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.PlanPolicyCompleted,
                        operationId,
                        policyName,
                        Duration(policyTimer, (NavigationDiagnosticDataKeys.PolicyType, policy.GetType().FullName)));
                }
                catch (Exception ex)
                {
                    WriteFailure(
                        NavigationDiagnosticEventKind.PlanPolicyFailed,
                        operationId,
                        policyName,
                        ex,
                        policyTimer,
                        (NavigationDiagnosticDataKeys.PolicyType, policy.GetType().FullName));
                    throw;
                }
            }

            var finalRoute = ResolvePresentedRoute(plan.TargetState, request.WindowId, route);
            var finalizedRequest = request with { Route = finalRoute };
            var presentationTimer = Stopwatch.StartNew();
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationStarted,
                operationId,
                plan.Kind.ToString(),
                Data((NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            try
            {
                await _presenter.ApplyAsync(
                    plan,
                    new NavigationPresentationContext(finalizedRequest, finalRoute, CurrentState, operationId),
                    cancellationToken).ConfigureAwait(false);
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.PresentationCompleted,
                    operationId,
                    plan.Kind.ToString(),
                    Duration(presentationTimer, (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            }
            catch (Exception ex)
            {
                WriteFailure(
                    NavigationDiagnosticEventKind.PresentationFailed,
                    operationId,
                    plan.Kind.ToString(),
                    ex,
                    presentationTimer,
                    (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString()));
                throw;
            }

            var previousState = CurrentState;
            CurrentState = plan.TargetState;
            History = History.Push(
                CreateHistoryEntry(operationId, finalizedRequest, finalRoute, CurrentState, plan.Reason),
                _maxHistoryEntries);
            await SaveSnapshotIfConfiguredAsync(operationId, cancellationToken).ConfigureAwait(false);

            _diagnostics.Write(
                NavigationDiagnosticEventKind.ReconciliationCompleted,
                operationId,
                reconciliation.Source.ToString(),
                Duration(timer, (NavigationDiagnosticDataKeys.ReconciliationSource, reconciliation.Source.ToString())));
            activity?.SetStatus(ActivityStatusCode.Ok);
            var result = new NavigationResult(finalRoute, plan, CurrentState, Presented: false);
            return new NavigationOperationResult<NavigationResult>(
                result,
                CreateNavigationCommittedEventArgs(
                    operationId,
                    NavigationCommitKind.Reconcile,
                    finalizedRequest,
                    finalRoute,
                    plan,
                    previousState,
                    CurrentState,
                    History,
                    presented: false));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            WriteFailure(NavigationDiagnosticEventKind.ReconciliationFailed, operationId, reconciliation.Source.ToString(), ex, timer);
            throw;
        }
    }

    private async ValueTask<NavigationOperationResult<NavigationRestoreResult>> RestoreFromStoreCoreAsync(
        NavigationRestoreOptions options,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using var activity = StartActivity("Navigation.RestoreFromStore", operationId, NavigationRequestSource.Restore.ToString());
        var store = _persistence?.Store;
        if (store is null)
        {
            var result = NavigationRestoreResult.Rejected("No navigation state store is configured.");
            WriteRestoreRejected(operationId, result);
            activity?.SetTag("navigation.restore_outcome", "rejected");
            return new NavigationOperationResult<NavigationRestoreResult>(result, Committed: null);
        }

        var loadTimer = Stopwatch.StartNew();
        _diagnostics.Write(
            NavigationDiagnosticEventKind.SnapshotLoadStarted,
            operationId,
            store.GetType().Name,
            Data((NavigationDiagnosticDataKeys.SnapshotStoreType, store.GetType().FullName)));

        NavigationSnapshot? snapshot;
        try
        {
            snapshot = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            _diagnostics.Write(
                NavigationDiagnosticEventKind.SnapshotLoaded,
                operationId,
                snapshot is null ? "No navigation snapshot was available." : "Navigation snapshot loaded.",
                SnapshotData(snapshot, store, loadTimer));
        }
        catch (Exception ex)
        {
            WriteFailure(
                NavigationDiagnosticEventKind.SnapshotLoadFailed,
                operationId,
                store.GetType().Name,
                ex,
                loadTimer,
                (NavigationDiagnosticDataKeys.SnapshotStoreType, store.GetType().FullName));
            activity?.SetTag("navigation.restore_outcome", "rejected");
            return new NavigationOperationResult<NavigationRestoreResult>(
                NavigationRestoreResult.Rejected($"Navigation snapshot load failed: {ex.Message}"),
                Committed: null);
        }

        if (snapshot is null)
        {
            var result = NavigationRestoreResult.Rejected("No navigation snapshot was available.");
            WriteRestoreRejected(operationId, result);
            activity?.SetTag("navigation.restore_outcome", "rejected");
            return new NavigationOperationResult<NavigationRestoreResult>(result, Committed: null);
        }

        return await RestoreSnapshotCoreAsync(snapshot, options, operationId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NavigationOperationResult<NavigationRestoreResult>> RestoreCoreAsync(
        NavigationSnapshot snapshot,
        NavigationRestoreOptions options,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using var activity = StartActivity("Navigation.Restore", operationId, NavigationRequestSource.Restore.ToString());
        return await RestoreSnapshotCoreAsync(snapshot, options, operationId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NavigationOperationResult<NavigationRestoreResult>> RestoreSnapshotCoreAsync(
        NavigationSnapshot snapshot,
        NavigationRestoreOptions options,
        string operationId,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        Activity.Current?.SetTag("navigation.snapshot_version", snapshot.SchemaVersion);
        _diagnostics.Write(
            NavigationDiagnosticEventKind.RestoreStarted,
            operationId,
            options.Reason ?? "Navigation restore started.",
            SnapshotData(snapshot, _persistence?.Store, timer, includeDuration: false));

        try
        {
            var serializer = _snapshotSerializer ?? new NavigationSnapshotSerializer(_routes);
            var restored = serializer.Restore(snapshot);
            if (!restored.Accepted || restored.State is null || restored.History is null)
            {
                WriteRestoreRejected(operationId, restored, timer);
                Activity.Current?.SetTag("navigation.restore_outcome", "rejected");
                return new NavigationOperationResult<NavigationRestoreResult>(restored, Committed: null);
            }

            foreach (var policy in _persistence?.RestorePolicies ?? Array.Empty<INavigationRestorePolicy>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var decision = await policy.EvaluateAsync(
                    new NavigationRestoreContext(snapshot, restored.State, restored.History, CurrentState, operationId),
                    cancellationToken).ConfigureAwait(false);
                if (!decision.Accepted)
                {
                    var rejected = NavigationRestoreResult.Rejected(decision.Reason ?? "Navigation restore was rejected by policy.");
                    WriteRestoreRejected(operationId, rejected, timer);
                    Activity.Current?.SetTag("navigation.restore_outcome", "rejected");
                    return new NavigationOperationResult<NavigationRestoreResult>(rejected, Committed: null);
                }
            }

            var route = ResolvePresentedRoute(restored.State, restored.State.ActiveWindowId, new RestoredRoute());
            var request = RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Restore, restored.State.ActiveWindowId);
            var plan = new NavigationPlan(
                restored.State,
                NavigationPlanKind.Restore,
                options.Reason ?? "Navigation state restored.");

            foreach (var policy in _planPolicies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var policyName = policy.GetType().Name;
                var policyTimer = Stopwatch.StartNew();
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.PlanPolicyStarted,
                    operationId,
                    policyName,
                    Data((NavigationDiagnosticDataKeys.PolicyType, policy.GetType().FullName)));

                try
                {
                    plan = await policy.ApplyAsync(
                        new NavigationPlanPolicyContext(request, route, CurrentState, operationId),
                        plan,
                        cancellationToken).ConfigureAwait(false);
                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.PlanPolicyCompleted,
                        operationId,
                        policyName,
                        Duration(policyTimer, (NavigationDiagnosticDataKeys.PolicyType, policy.GetType().FullName)));
                }
                catch (Exception ex)
                {
                    WriteFailure(
                        NavigationDiagnosticEventKind.PlanPolicyFailed,
                        operationId,
                        policyName,
                        ex,
                        policyTimer,
                        (NavigationDiagnosticDataKeys.PolicyType, policy.GetType().FullName));
                    throw;
                }
            }

            var finalRoute = ResolvePresentedRoute(plan.TargetState, request.WindowId, route);
            var finalizedRequest = request with { Route = finalRoute };
            var presentationTimer = Stopwatch.StartNew();
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationStarted,
                operationId,
                plan.Kind.ToString(),
                Data((NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            try
            {
                await _presenter.ApplyAsync(
                    plan,
                    new NavigationPresentationContext(finalizedRequest, finalRoute, CurrentState, operationId),
                    cancellationToken).ConfigureAwait(false);
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.PresentationCompleted,
                    operationId,
                    plan.Kind.ToString(),
                    Duration(presentationTimer, (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            }
            catch (Exception ex)
            {
                WriteFailure(
                    NavigationDiagnosticEventKind.PresentationFailed,
                    operationId,
                    plan.Kind.ToString(),
                    ex,
                    presentationTimer,
                    (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString()));
                throw;
            }

            var previousState = CurrentState;
            CurrentState = plan.TargetState;
            History = NormalizeRestoredHistory(restored.History, CurrentState, finalRoute);

            if (options.SaveAfterRestore)
            {
                await SaveSnapshotIfConfiguredAsync(operationId, cancellationToken).ConfigureAwait(false);
            }

            _diagnostics.Write(
                NavigationDiagnosticEventKind.RestoreCompleted,
                operationId,
                options.Reason ?? "Navigation restore completed.",
                SnapshotData(snapshot, _persistence?.Store, timer));
            Activity.Current?.SetTag("navigation.restore_outcome", "accepted");
            Activity.Current?.SetStatus(ActivityStatusCode.Ok);
            var result = NavigationRestoreResult.AcceptedResult(
                CurrentState,
                History,
                presented: true,
                restored.Diagnostics);
            return new NavigationOperationResult<NavigationRestoreResult>(
                result,
                CreateNavigationCommittedEventArgs(
                    operationId,
                    NavigationCommitKind.Restore,
                    finalizedRequest,
                    finalRoute,
                    plan,
                    previousState,
                    CurrentState,
                    History,
                    presented: true));
        }
        catch (Exception ex)
        {
            Activity.Current?.SetTag("navigation.restore_outcome", "failed");
            Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);
            WriteFailure(
                NavigationDiagnosticEventKind.RestoreFailed,
                operationId,
                options.Reason ?? "Navigation restore failed.",
                ex,
                timer,
                (NavigationDiagnosticDataKeys.SnapshotVersion, snapshot.SchemaVersion));
            throw;
        }
    }

    private ResolvedRoute ResolveRoute(RouterNavigationRequest request, string operationId)
    {
        if (request.Route is not null)
        {
            return new ResolvedRoute(request.Route, null);
        }

        if (request.Uri is null)
        {
            throw new InvalidOperationException("RouterNavigationRequest must contain either a Uri or an AppRoute.");
        }

        var timer = Stopwatch.StartNew();
        _diagnostics.Write(
            NavigationDiagnosticEventKind.RouteMatchingStarted,
            operationId,
            request.Uri.ToString(),
            RequestData(
                request,
                (NavigationDiagnosticDataKeys.Uri, request.Uri.ToString()),
                (NavigationDiagnosticDataKeys.RequestSource, request.Source.ToString())));

        try
        {
            var match = _routes.Match(request.Uri);
            if (!match.IsSuccess || match.Route is null)
            {
                var diagnostic = match.Diagnostics.FirstOrDefault();
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.RouteNotMatched,
                    operationId,
                    request.Uri.ToString(),
                    RouteFailureData(timer, request, diagnostic));

                if (diagnostic?.Code == "route.not_matched" && _fallbackRouteFactory is not null)
                {
                    var fallbackTimer = Stopwatch.StartNew();
                    var fallbackRoute = _fallbackRouteFactory(new NavigationFallbackContext(
                        request,
                        match.Diagnostics,
                        CurrentState,
                        operationId));

                    if (fallbackRoute is not null)
                    {
                        Activity.Current?.SetTag("navigation.route_type", fallbackRoute.GetType().FullName);
                        _diagnostics.Write(
                            NavigationDiagnosticEventKind.RouteFallbackSelected,
                            operationId,
                            fallbackRoute.GetType().Name,
                            Duration(
                                fallbackTimer,
                                request,
                                (NavigationDiagnosticDataKeys.Uri, request.Uri.ToString()),
                                (NavigationDiagnosticDataKeys.RouteDiagnosticCode, diagnostic.Code),
                                (NavigationDiagnosticDataKeys.RouteDiagnosticMessage, diagnostic.Message),
                                (NavigationDiagnosticDataKeys.RouteType, fallbackRoute.GetType().FullName)));
                        return new ResolvedRoute(fallbackRoute, null);
                    }
                }

                throw new RouteNotMatchedException(request.Uri, match.Diagnostics);
            }

            Activity.Current?.SetTag("navigation.route_type", match.Route.GetType().FullName);
            var routeTemplate = match.Definition?.Template.Value;
            Activity.Current?.SetTag("navigation.route_template", routeTemplate);
            _diagnostics.Write(
                NavigationDiagnosticEventKind.RouteMatched,
                operationId,
                match.Route.GetType().Name,
                Duration(
                    timer,
                    request,
                    (NavigationDiagnosticDataKeys.Uri, request.Uri.ToString()),
                    (NavigationDiagnosticDataKeys.RouteType, match.Route.GetType().FullName),
                    (NavigationDiagnosticDataKeys.RouteTemplate, routeTemplate)));
            return new ResolvedRoute(match.Route, match.Definition, match.Metadata);
        }
        catch (Exception ex) when (ex is not RouteNotMatchedException)
        {
            WriteFailure(
                NavigationDiagnosticEventKind.RouteMatchingFailed,
                operationId,
                request.Uri.ToString(),
                ex,
                timer,
                (NavigationDiagnosticDataKeys.Uri, request.Uri.ToString()));
            throw;
        }
    }

    private void OnPresenterReconciliationRequested(object? sender, NavigationReconciliationRequestedEventArgs e)
    {
        Task task;
        lock (_reconciliationGate)
        {
            var reconciliation = e.Reconciliation;
            _reconciliationQueue = _reconciliationQueue
                .ContinueWith(
                    static async (previous, state) =>
                    {
                        var (navigator, nextReconciliation) = ((RouterNavigator, NavigationReconciliation))state!;
                        await navigator.ReconcileAsync(nextReconciliation).ConfigureAwait(false);
                    },
                    (this, reconciliation),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
            task = _reconciliationQueue;
        }

        _ = ObserveQueuedReconciliationAsync(task);
    }

    private async Task ObserveQueuedReconciliationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var operationId = Guid.NewGuid().ToString("N");
            WriteFailure(
                NavigationDiagnosticEventKind.ReconciliationFailed,
                operationId,
                "Queued presenter reconciliation failed.",
                ex,
                Stopwatch.StartNew());
        }
    }

    private async ValueTask SaveSnapshotIfConfiguredAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        if (_persistence?.Store is not { } store || _snapshotSerializer is null)
        {
            return;
        }

        var timer = Stopwatch.StartNew();
        _diagnostics.Write(
            NavigationDiagnosticEventKind.SnapshotSaveStarted,
            operationId,
            store.GetType().Name,
            Data((NavigationDiagnosticDataKeys.SnapshotStoreType, store.GetType().FullName)));

        try
        {
            var snapshot = _snapshotSerializer.CreateSnapshot(CurrentState, History);
            await store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            _diagnostics.Write(
                NavigationDiagnosticEventKind.SnapshotSaved,
                operationId,
                store.GetType().Name,
                SnapshotData(snapshot, store, timer));
        }
        catch (Exception ex)
        {
            WriteFailure(
                NavigationDiagnosticEventKind.SnapshotSaveFailed,
                operationId,
                store.GetType().Name,
                ex,
                timer,
                (NavigationDiagnosticDataKeys.SnapshotStoreType, store.GetType().FullName));
        }
    }

    private void WriteRestoreRejected(
        string operationId,
        NavigationRestoreResult result,
        Stopwatch? timer = null)
    {
        var data = timer is null
            ? Data((NavigationDiagnosticDataKeys.RestoreReason, result.RejectionReason))
            : Duration(timer, (NavigationDiagnosticDataKeys.RestoreReason, result.RejectionReason));

        _diagnostics.Write(
            NavigationDiagnosticEventKind.RestoreRejected,
            operationId,
            result.RejectionReason ?? "Navigation restore was rejected.",
            data);
    }

    private Activity? StartActivity(string name, string operationId, RouterNavigationRequest request)
    {
        var activity = StartActivity(name, operationId, request.Source.ToString(), request.Disposition);
        AddProvenanceActivityTags(activity, request.Provenance);
        return activity;
    }

    private Activity? StartActivity(
        string name,
        string operationId,
        string source,
        RouterNavigationDisposition disposition = RouterNavigationDisposition.Auto)
    {
        var activity = _activitySource.StartActivity(name);
        activity?.SetTag("navigation.operation_id", operationId);
        activity?.SetTag("navigation.source", source);
        activity?.SetTag("navigation.disposition", disposition.ToString());
        return activity;
    }

    private NavigationHistoryEntry CreateHistoryEntry(
        string operationId,
        RouterNavigationRequest request,
        AppRoute route,
        NavigationState state,
        string? reason)
    {
        return new NavigationHistoryEntry(
            operationId,
            request,
            route,
            state,
            reason,
            DateTimeOffset.UtcNow);
    }

    private static NavigationCommittedEventArgs CreateNavigationCommittedEventArgs(
        string operationId,
        NavigationCommitKind kind,
        RouterNavigationRequest request,
        AppRoute route,
        NavigationPlan plan,
        NavigationState previousState,
        NavigationState currentState,
        NavigationHistory currentHistory,
        bool presented)
    {
        return new NavigationCommittedEventArgs(
            operationId,
            kind,
            request,
            route,
            plan,
            previousState,
            currentState,
            currentHistory,
            presented);
    }

    private void EnqueueNavigationCommitted(NavigationCommittedEventArgs? eventArgs)
    {
        if (eventArgs is null)
        {
            return;
        }

        lock (_navigationCommittedGate)
        {
            _pendingNavigationCommittedEvents.Enqueue(eventArgs);
        }
    }

    private void DrainNavigationCommitted()
    {
        lock (_navigationCommittedGate)
        {
            if (_publishingNavigationCommitted)
            {
                return;
            }

            if (_pendingNavigationCommittedEvents.Count == 0)
            {
                return;
            }

            _publishingNavigationCommitted = true;
        }

        while (true)
        {
            NavigationCommittedEventArgs nextEventArgs;
            lock (_navigationCommittedGate)
            {
                if (_pendingNavigationCommittedEvents.Count == 0)
                {
                    _publishingNavigationCommitted = false;
                    return;
                }

                nextEventArgs = _pendingNavigationCommittedEvents.Dequeue();
            }

            PublishNavigationCommittedNow(nextEventArgs);
        }
    }

    private void PublishNavigationCommittedNow(NavigationCommittedEventArgs eventArgs)
    {
        var handlers = NavigationCommitted;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            if (handler is not EventHandler<NavigationCommittedEventArgs> eventHandler)
            {
                continue;
            }

            try
            {
                eventHandler(this, eventArgs);
            }
            catch (Exception ex)
            {
                try
                {
                    WriteNavigationCommittedHandlerFailure(eventArgs, ex);
                }
                catch
                {
                    // Subscriber failures must never become navigation control flow.
                }
            }
        }
    }

    private void WriteNavigationCommittedHandlerFailure(
        NavigationCommittedEventArgs eventArgs,
        Exception exception)
    {
        _diagnostics.Write(
            NavigationDiagnosticEventKind.NavigationCommittedHandlerFailed,
            eventArgs.OperationId,
            "A navigation committed event handler failed.",
            Data(
                (NavigationDiagnosticDataKeys.OriginalKind, nameof(NavigationCommitted)),
                (NavigationDiagnosticDataKeys.ExceptionType, exception.GetType().FullName),
                (NavigationDiagnosticDataKeys.ExceptionMessage, exception.Message),
                (NavigationDiagnosticDataKeys.PlanKind, eventArgs.Plan.Kind.ToString()),
                (NavigationDiagnosticDataKeys.RouteType, eventArgs.Route.GetType().FullName)),
            LogLevel.Error,
            NavigationDiagnosticPhase.Navigation);
    }

    private void WriteFailure(
        NavigationDiagnosticEventKind kind,
        string operationId,
        string message,
        Exception exception,
        Stopwatch timer,
        params (string Key, object? Value)[] data)
    {
        Activity.Current?.SetTag("navigation.failure.type", exception.GetType().FullName);
        Activity.Current?.SetTag("navigation.failure.message", exception.Message);
        _diagnostics.Write(
            kind,
            operationId,
            message,
            FailureData(exception, timer, data));
    }

    private static IReadOnlyDictionary<string, object?> Duration(
        Stopwatch timer,
        params (string Key, object? Value)[] data)
    {
        timer.Stop();
        var result = Data(data);
        result[NavigationDiagnosticDataKeys.DurationMs] = timer.Elapsed.TotalMilliseconds;
        return result;
    }

    private static IReadOnlyDictionary<string, object?> Duration(
        Stopwatch timer,
        RouterNavigationRequest request,
        params (string Key, object? Value)[] data)
    {
        timer.Stop();
        var result = RequestData(request, data);
        result[NavigationDiagnosticDataKeys.DurationMs] = timer.Elapsed.TotalMilliseconds;
        return result;
    }

    private static IReadOnlyDictionary<string, object?> FailureData(
        Exception exception,
        Stopwatch timer,
        params (string Key, object? Value)[] data)
    {
        timer.Stop();
        var result = Data(data);
        result[NavigationDiagnosticDataKeys.DurationMs] = timer.Elapsed.TotalMilliseconds;
        result[NavigationDiagnosticDataKeys.ExceptionType] = exception.GetType().FullName;
        result[NavigationDiagnosticDataKeys.ExceptionMessage] = exception.Message;
        return result;
    }

    private static IReadOnlyDictionary<string, object?> RouteFailureData(
        Stopwatch timer,
        RouterNavigationRequest request,
        RouteDiagnostic? diagnostic)
    {
        var data = new List<(string Key, object? Value)>
        {
            (NavigationDiagnosticDataKeys.Uri, request.Uri?.ToString())
        };

        if (diagnostic is not null)
        {
            data.Add((NavigationDiagnosticDataKeys.RouteDiagnosticCode, diagnostic.Code));
            data.Add((NavigationDiagnosticDataKeys.RouteDiagnosticMessage, diagnostic.Message));

            foreach (var (key, value) in diagnostic.Data)
            {
                var normalizedKey = key switch
                {
                    "path" => NavigationDiagnosticDataKeys.Path,
                    "template" => NavigationDiagnosticDataKeys.RouteTemplate,
                    "routeType" => NavigationDiagnosticDataKeys.RouteType,
                    "candidateCount" => NavigationDiagnosticDataKeys.CandidateCount,
                    _ => key
                };

                data.Add((normalizedKey, value));
            }
        }

        return Duration(timer, request, data.ToArray());
    }

    private static IReadOnlyDictionary<string, object?> SnapshotData(
        NavigationSnapshot? snapshot,
        INavigationStateStore? store,
        Stopwatch timer,
        bool includeDuration = true)
    {
        var values = new List<(string Key, object? Value)>
        {
            (NavigationDiagnosticDataKeys.SnapshotStoreType, store?.GetType().FullName)
        };

        if (snapshot is not null)
        {
            values.Add((NavigationDiagnosticDataKeys.SnapshotVersion, snapshot.SchemaVersion));
            values.Add((NavigationDiagnosticDataKeys.SnapshotAgeMs, (DateTimeOffset.UtcNow - snapshot.CreatedAt).TotalMilliseconds));
            values.Add((NavigationDiagnosticDataKeys.SnapshotWindowCount, snapshot.State.Windows.Count));
            values.Add((NavigationDiagnosticDataKeys.SnapshotHistoryCount, snapshot.History?.Entries.Count ?? 0));
        }

        return includeDuration
            ? Duration(timer, values.ToArray())
            : Data(values.ToArray());
    }

    private void WriteRedirectLoopDetected(
        string operationId,
        string? policyType,
        RouterNavigationRequest redirectFrom,
        RouterNavigationRequest redirectTo,
        int redirectCount,
        string redirectTrace,
        string message)
    {
        Activity.Current?.SetTag("navigation.redirect_count", redirectCount);
        Activity.Current?.SetTag("navigation.redirect_from", DescribeRedirectTarget(redirectFrom));
        Activity.Current?.SetTag("navigation.redirect_to", DescribeRedirectTarget(redirectTo));
        Activity.Current?.SetTag("navigation.redirect_trace", redirectTrace);
        _diagnostics.Write(
            NavigationDiagnosticEventKind.RequestRedirectLoopDetected,
            operationId,
            message,
            Data(
                (NavigationDiagnosticDataKeys.PolicyType, policyType),
                (NavigationDiagnosticDataKeys.RedirectCount, redirectCount),
                (NavigationDiagnosticDataKeys.RedirectFrom, DescribeRedirectTarget(redirectFrom)),
                (NavigationDiagnosticDataKeys.RedirectTo, DescribeRedirectTarget(redirectTo)),
                (NavigationDiagnosticDataKeys.RedirectTrace, redirectTrace)));
    }

    private static string BuildRedirectTrace(
        RouterNavigationRequest initialRequest,
        IReadOnlyList<RouterNavigationRequest> redirects)
    {
        return string.Join(
            " -> ",
            new[] { DescribeRedirectTarget(initialRequest) }
                .Concat(redirects.Select(DescribeRedirectTarget)));
    }

    private static string DescribeRedirectTarget(RouterNavigationRequest request)
    {
        var parts = new List<string>();
        if (request.Uri is not null)
        {
            parts.Add($"uri={request.Uri}");
        }

        if (request.Route is not null)
        {
            parts.Add($"route={request.Route.GetType().Name}:{request.Route}");
        }

        var target = parts.Count == 0
            ? "<none>"
            : string.Join(", ", parts);

        return request.WindowId is null
            ? $"{target} [{request.Source}, disposition={request.Disposition}]"
            : $"{target} [{request.Source}, disposition={request.Disposition}, window={request.WindowId}]";
    }

    private static Dictionary<string, object?> RequestData(
        RouterNavigationRequest request,
        params (string Key, object? Value)[] values)
    {
        var result = Data(values);
        AddProvenanceData(result, request.Provenance);
        return result;
    }

    private static Dictionary<string, object?> Data(params (string Key, object? Value)[] values)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            result[key] = value;
        }

        return result;
    }

    private static void AddProvenanceData(
        IDictionary<string, object?> data,
        NavigationRequestProvenance? provenance)
    {
        if (provenance is null)
        {
            return;
        }

        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceProvider, provenance.Provider);
        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceOriginalUri, provenance.OriginalUri?.ToString());
        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceReferrerUri, provenance.ReferrerUri?.ToString());
        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceCorrelationId, provenance.CorrelationId);
        if (provenance.IsColdStart.HasValue)
        {
            data[NavigationDiagnosticDataKeys.ProvenanceIsColdStart] = provenance.IsColdStart.Value;
        }

        if (provenance.Attributes.Count > 0)
        {
            data[NavigationDiagnosticDataKeys.ProvenanceAttributes] =
                new Dictionary<string, string?>(provenance.Attributes, StringComparer.Ordinal);
        }
    }

    private static void AddProvenanceActivityTags(
        Activity? activity,
        NavigationRequestProvenance? provenance)
    {
        if (activity is null || provenance is null)
        {
            return;
        }

        activity.SetTag("navigation.provenance.provider", provenance.Provider);
        activity.SetTag("navigation.provenance.original_uri", provenance.OriginalUri?.ToString());
        activity.SetTag("navigation.provenance.referrer_uri", provenance.ReferrerUri?.ToString());
        activity.SetTag("navigation.provenance.correlation_id", provenance.CorrelationId);
        if (provenance.IsColdStart.HasValue)
        {
            activity.SetTag("navigation.provenance.is_cold_start", provenance.IsColdStart.Value);
        }

        foreach (var pair in provenance.Attributes.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            {
                activity.SetTag($"navigation.provenance.attribute.{pair.Key}", pair.Value);
            }
        }
    }

    private static void AddIfPresent(
        IDictionary<string, object?> data,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            data[key] = value;
        }
    }

    private static AppRoute ResolvePresentedRoute(
        NavigationState state,
        string? preferredWindowId,
        AppRoute fallbackRoute)
    {
        var window = string.IsNullOrWhiteSpace(preferredWindowId)
            ? state.ActiveWindow
            : state.FindWindow(preferredWindowId) ?? state.ActiveWindow;

        return PresentedRouteResolver.FindPresentedRoute(window) ?? fallbackRoute;
    }

    private static NavigationHistory NormalizeRestoredHistory(
        NavigationHistory history,
        NavigationState currentState,
        AppRoute finalRoute)
    {
        var current = history.Current;
        if (current is null)
        {
            return history;
        }

        var entries = history.Entries.ToArray();
        entries[history.CurrentIndex] = current with
        {
            Request = current.Request with { Route = finalRoute },
            Route = finalRoute,
            State = currentState
        };

        return new NavigationHistory(entries, history.CurrentIndex);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static IReadOnlyDictionary<string, object?> MergeMetadata(
        IReadOnlyDictionary<string, object?>? lowerPriority,
        IReadOnlyDictionary<string, object?>? higherPriority)
    {
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (lowerPriority is not null)
        {
            foreach (var pair in lowerPriority)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        if (higherPriority is not null)
        {
            foreach (var pair in higherPriority)
            {
                merged[pair.Key] = pair.Value;
            }
        }

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

    private sealed record ReconciledRoute(string Source) : AppRoute;

    private sealed record BackRoute : AppRoute;

    private sealed record RestoredRoute : AppRoute;

    private readonly record struct NavigationOperationResult<T>(
        T Result,
        NavigationCommittedEventArgs? Committed);

    private sealed record ResolvedRoute(
        AppRoute Route,
        RouteDefinition? Definition,
        IReadOnlyDictionary<string, object?>? Metadata = null);

    private sealed record ResolvedNavigationRequest(
        RouterNavigationRequest Request,
        AppRoute Route,
        RouteDefinition? Definition);

    private readonly record struct RedirectTargetKey(
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
}

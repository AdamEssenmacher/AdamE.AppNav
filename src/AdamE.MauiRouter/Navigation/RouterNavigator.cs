using System.Diagnostics;
using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Internal;
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
    private readonly IAppNavigationPlanner _planner;
    private readonly INavigationPresenter _presenter;
    private readonly IBackNavigator _backNavigator;
    private readonly RouterRequestResolver _requestResolver;
    private readonly RouterNavigationDiagnostics _diagnostics;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Lock _reconciliationGate = new();
    private readonly int _maxHistoryEntries;
    private Task _reconciliationQueue = Task.CompletedTask;
    private bool _disposed;

    public RouterNavigator(
        RouteTable routes,
        IAppNavigationPlanner planner,
        INavigationPresenter presenter,
        RouterNavigatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        options ??= new RouterNavigatorOptions();

        if (options.MaxHistoryEntries < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxHistoryEntries cannot be negative.");

        if (options.MaxRedirects < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRedirects cannot be negative.");

        CurrentState = options.InitialState ?? NavigationState.Empty;
        NavigationStateValidator.ValidateState(CurrentState, "Router initial state");
        History = options.InitialHistory ?? NavigationHistory.Empty;
        ILogger? logger = options.Logger ?? options.LoggerFactory?.CreateLogger<RouterNavigator>();
        NavigationDiagnostics diagnostics = options.Diagnostics ?? new NavigationDiagnostics(logger);
        _diagnostics = new RouterNavigationDiagnostics(diagnostics, NavigationActivitySources.Default);
        _requestResolver = new RouterRequestResolver(
            routes,
            options.RequestPolicies.ToArray(),
            options.FallbackRouteFactory,
            options.MaxRedirects,
            _diagnostics);
        _backNavigator = options.BackNavigator ?? new DefaultBackNavigator(diagnostics: diagnostics);
        _maxHistoryEntries = options.MaxHistoryEntries;

        _presenter.ReconciliationRequested += OnPresenterReconciliationRequested;
    }

    public NavigationState CurrentState { get; private set; }

    public NavigationHistory History { get; private set; }

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
        return NavigateAsync(RouterNavigationRequest.FromRoute(route, source, disposition: disposition),
            cancellationToken);
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
        try
        {
            return await NavigateCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async ValueTask<BackNavigationResult> BackAsync(
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await BackCoreAsync(windowId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async ValueTask<NavigationResult> ReconcileAsync(
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RouterNavigationRequest request = RouterNavigationRequest.FromRoute(
                reconciliation.Route ?? new ReconciledRoute(),
                NavigationRequestSource.NativeReconciliation);

            return await ReconcileCoreAsync(request, reconciliation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
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
        if (_disposed) return;

        _presenter.ReconciliationRequested -= OnPresenterReconciliationRequested;
        _operationLock.Dispose();
        _disposed = true;
    }

    private async ValueTask<NavigationResult> NavigateCoreAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using Activity? activity = _diagnostics.StartActivity("Navigation.Navigate", operationId, request);
        var operationTimer = Stopwatch.StartNew();
        AppRoute? route = null;

        try
        {
            (RouterNavigationRequest effectiveRequest, AppRoute appRoute, RouteDefinition? routeDefinition) =
                await _requestResolver.ResolveAsync(
                    request,
                    CurrentState,
                    operationId,
                    cancellationToken).ConfigureAwait(false);
            route = appRoute;
            Activity.Current?.SetTag("navigation.route_type", route.GetType().FullName);
            Activity.Current?.SetTag("navigation.route_template", routeDefinition?.Template.Value);
            Activity.Current?.SetTag("navigation.disposition", effectiveRequest.Disposition.ToString());

            var planningTimer = Stopwatch.StartNew();
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PlanningStarted,
                operationId,
                route.GetType().Name,
                RouterNavigationDiagnostics.RequestData(
                    effectiveRequest,
                    (NavigationDiagnosticDataKeys.RouteType, route.GetType().FullName),
                    (NavigationDiagnosticDataKeys.RequestDisposition, effectiveRequest.Disposition.ToString())));
            NavigationPlan plan;
            try
            {
                plan = await _planner.CreatePlanAsync(
                    new NavigationPlanningContext(effectiveRequest, route, CurrentState, operationId),
                    cancellationToken).ConfigureAwait(false);
                NavigationStateValidator.ValidatePlan(plan, "App navigation planner");
                Activity.Current?.SetTag("navigation.plan_kind", plan.Kind.ToString());
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.PlanningCompleted,
                    operationId,
                    plan.Kind.ToString(),
                    RouterNavigationDiagnostics.Duration(planningTimer,
                        (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            }
            catch (Exception ex)
            {
                _diagnostics.WriteFailure(
                    NavigationDiagnosticEventKind.PlanningFailed,
                    operationId,
                    route.GetType().Name,
                    ex,
                    planningTimer,
                    (NavigationDiagnosticDataKeys.RouteType, route.GetType().FullName));
                throw;
            }

            AppRoute finalRoute = ResolvePresentedRoute(plan.TargetState, effectiveRequest.WindowId, route);
            RouterNavigationRequest finalizedRequest = effectiveRequest with { Route = finalRoute };
            var presentationTimer = Stopwatch.StartNew();
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationStarted,
                operationId,
                plan.Kind.ToString(),
                RouterNavigationDiagnostics.Data((NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
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
                    RouterNavigationDiagnostics.Duration(presentationTimer,
                        (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            }
            catch (Exception ex)
            {
                _diagnostics.WriteFailure(
                    NavigationDiagnosticEventKind.PresentationFailed,
                    operationId,
                    plan.Kind.ToString(),
                    ex,
                    presentationTimer,
                    (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString()));
                throw;
            }

            CurrentState = plan.TargetState;
            History = History.Push(
                CreateHistoryEntry(finalizedRequest, finalRoute, CurrentState),
                _maxHistoryEntries);
            activity?.SetStatus(ActivityStatusCode.Ok);

            return new NavigationResult(finalRoute, plan, CurrentState, true);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _diagnostics.WriteFailure(
                NavigationDiagnosticEventKind.NavigationFailed,
                operationId,
                route?.GetType().Name ?? request.Uri?.ToString() ?? request.Source.ToString(),
                ex,
                operationTimer);
            throw;
        }
    }

    private async ValueTask<BackNavigationResult> BackCoreAsync(
        string? windowId,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using Activity? activity =
            _diagnostics.StartActivity("Navigation.Back", operationId, nameof(NavigationRequestSource.InAppCommand));
        var timer = Stopwatch.StartNew();

        var backContext = new BackNavigationContext(CurrentState, windowId, operationId);
        string? diagnosticWindowId =
            backContext.ResolvedWindowId ?? backContext.RequestedWindowId ?? CurrentState.ActiveWindowId;
        string diagnosticWindowName = diagnosticWindowId ?? "active";

        _diagnostics.Write(
            NavigationDiagnosticEventKind.BackStarted,
            operationId,
            diagnosticWindowName,
            RouterNavigationDiagnostics.Data((NavigationDiagnosticDataKeys.WindowId, diagnosticWindowId)));

        try
        {
            NavigationPlan? plan = _backNavigator.CreateBackPlan(backContext);
            if (plan is null)
            {
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.BackUnhandled,
                    operationId,
                    "No host accepted back navigation.",
                    RouterNavigationDiagnostics.Duration(timer,
                        (NavigationDiagnosticDataKeys.WindowId, diagnosticWindowId)));
                activity?.SetStatus(ActivityStatusCode.Ok);
                return BackNavigationResult.Unhandled;
            }

            NavigationStateValidator.ValidatePlan(plan, $"Back navigator '{_backNavigator.GetType().Name}'");
            string? resolvedWindowId = backContext.ResolvedWindowId ?? backContext.RequestedWindowId;
            AppRoute route = ResolvePresentedRoute(plan.TargetState, resolvedWindowId, new BackRoute());
            RouterNavigationRequest request =
                RouterNavigationRequest.FromRoute(route, NavigationRequestSource.InAppCommand, resolvedWindowId);

            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationStarted,
                operationId,
                plan.Kind.ToString(),
                RouterNavigationDiagnostics.Data((NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
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
                    RouterNavigationDiagnostics.Duration(presentationTimer,
                        (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            }
            catch (Exception ex)
            {
                _diagnostics.WriteFailure(
                    NavigationDiagnosticEventKind.PresentationFailed,
                    operationId,
                    plan.Kind.ToString(),
                    ex,
                    presentationTimer,
                    (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString()));
                throw;
            }

            CurrentState = plan.TargetState;
            History = History.Push(CreateHistoryEntry(request, route, CurrentState),
                _maxHistoryEntries);

            _diagnostics.Write(
                NavigationDiagnosticEventKind.BackCompleted,
                operationId,
                plan.Reason ?? "Back handled.",
                RouterNavigationDiagnostics.Duration(timer,
                    (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            activity?.SetStatus(ActivityStatusCode.Ok);
            return BackNavigationResult.HandledBy(new NavigationResult(route, plan, CurrentState, true));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _diagnostics.WriteFailure(
                NavigationDiagnosticEventKind.BackFailed,
                operationId,
                windowId ?? "active",
                ex,
                timer);
            throw;
        }
    }

    private async ValueTask<NavigationResult> ReconcileCoreAsync(
        RouterNavigationRequest request,
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using Activity? activity =
            _diagnostics.StartActivity("Navigation.Reconcile", operationId, reconciliation.Source.ToString());
        var timer = Stopwatch.StartNew();
        AppRoute route = request.Route ?? new ReconciledRoute();
        var plan = new NavigationPlan(reconciliation.TargetState, NavigationPlanKind.Reconcile, reconciliation.Reason);

        _diagnostics.Write(
            NavigationDiagnosticEventKind.ReconciliationStarted,
            operationId,
            reconciliation.Source.ToString(),
            RouterNavigationDiagnostics.Data((NavigationDiagnosticDataKeys.ReconciliationSource,
                reconciliation.Source.ToString())));

        try
        {
            NavigationStateValidator.ValidatePlan(plan, "Navigation reconciliation");

            AppRoute finalRoute = ResolvePresentedRoute(plan.TargetState, request.WindowId, route);
            RouterNavigationRequest finalizedRequest = request with { Route = finalRoute };
            var presentationTimer = Stopwatch.StartNew();
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationStarted,
                operationId,
                plan.Kind.ToString(),
                RouterNavigationDiagnostics.Data((NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
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
                    RouterNavigationDiagnostics.Duration(presentationTimer,
                        (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
            }
            catch (Exception ex)
            {
                _diagnostics.WriteFailure(
                    NavigationDiagnosticEventKind.PresentationFailed,
                    operationId,
                    plan.Kind.ToString(),
                    ex,
                    presentationTimer,
                    (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString()));
                throw;
            }

            CurrentState = plan.TargetState;
            History = History.Push(
                CreateHistoryEntry(finalizedRequest, finalRoute, CurrentState),
                _maxHistoryEntries);

            _diagnostics.Write(
                NavigationDiagnosticEventKind.ReconciliationCompleted,
                operationId,
                reconciliation.Source.ToString(),
                RouterNavigationDiagnostics.Duration(timer,
                    (NavigationDiagnosticDataKeys.ReconciliationSource, reconciliation.Source.ToString())));
            activity?.SetStatus(ActivityStatusCode.Ok);
            return new NavigationResult(finalRoute, plan, CurrentState, false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _diagnostics.WriteFailure(NavigationDiagnosticEventKind.ReconciliationFailed, operationId,
                reconciliation.Source.ToString(), ex, timer);
            throw;
        }
    }

    private void OnPresenterReconciliationRequested(object? sender, NavigationReconciliationRequestedEventArgs e)
    {
        Task task;
        lock (_reconciliationGate)
        {
            NavigationReconciliation reconciliation = e.Reconciliation;
            _reconciliationQueue = _reconciliationQueue
                .ContinueWith(
                    static async (_, state) =>
                    {
                        (RouterNavigator navigator, NavigationReconciliation nextReconciliation) =
                            ((RouterNavigator, NavigationReconciliation))state!;
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
            _diagnostics.WriteFailure(
                NavigationDiagnosticEventKind.ReconciliationFailed,
                operationId,
                "Queued presenter reconciliation failed.",
                ex,
                Stopwatch.StartNew());
        }
    }

    private static NavigationHistoryEntry CreateHistoryEntry(
        RouterNavigationRequest request,
        AppRoute route,
        NavigationState state)
    {
        return new NavigationHistoryEntry(request, route, state);
    }

    private static AppRoute ResolvePresentedRoute(
        NavigationState state,
        string? preferredWindowId,
        AppRoute fallbackRoute)
    {
        WindowNode? window = string.IsNullOrWhiteSpace(preferredWindowId)
            ? state.ActiveWindow
            : state.FindWindow(preferredWindowId) ?? state.ActiveWindow;

        return PresentedRouteResolver.FindPresentedRoute(window) ?? fallbackRoute;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record ReconciledRoute : AppRoute;

    private sealed record BackRoute : AppRoute;
}

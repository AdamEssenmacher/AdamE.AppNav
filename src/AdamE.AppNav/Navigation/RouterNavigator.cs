using System.Diagnostics;
using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.History;
using AdamE.AppNav.Internal;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using Microsoft.Extensions.Logging;

namespace AdamE.AppNav.Navigation;

internal sealed class RouterNavigator : IRouterNavigator
{
    private static readonly AsyncLocal<RouterOperationContext?> CurrentOperationContext = new();

    private readonly IAppNavigationPlanner _planner;
    private readonly INavigationPresenter _presenter;
    private readonly IBackNavigator _backNavigator;
    private readonly IReadOnlyList<IBackNavigationPolicy> _backNavigationPolicies;
    private readonly RouterRequestResolver _requestResolver;
    private readonly RouterNavigationDiagnostics _diagnostics;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly Lock _lifetimeGate = new();
    private readonly Lock _reconciliationGate = new();
    private readonly TaskCompletionSource<bool> _shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly int _maxHistoryEntries;
    private Task _reconciliationQueue = Task.CompletedTask;
    private int _activeOperations;
    private bool _shutdownStarted;
    private bool _shutdownSignalIssued;
    private bool _shutdownCancellationCompleted;
    private bool _operationLockDisposed;

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
            options.RequestTransformers.ToArray(),
            options.RequestPolicies.ToArray(),
            options.FallbackRouteFactory,
            options.MaxRedirects,
            _diagnostics);
        _backNavigator = options.BackNavigator ?? new DefaultBackNavigator(diagnostics: diagnostics);
        _backNavigationPolicies = options.BackNavigationPolicies.ToArray();
        _maxHistoryEntries = options.MaxHistoryEntries;

        _presenter.ReconciliationRequested += OnPresenterReconciliationRequested;
    }

    public NavigationState CurrentState { get; private set; }

    public NavigationHistory History { get; private set; }

    public async ValueTask<NavigationResult> NavigateAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        RouterOperationContext? operationContext = null;
        var operationAdmitted = false;
        var lockTaken = false;
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            operationContext = EnterOperationContext();
            BeginOperation();
            operationAdmitted = true;
            CancellationToken operationCancellation = CreateOperationCancellation(
                cancellationToken,
                out linkedCancellation);
            await _operationLock.WaitAsync(operationCancellation).ConfigureAwait(false);
            lockTaken = true;
            operationCancellation.ThrowIfCancellationRequested();
            return await NavigateCoreAsync(request, operationCancellation).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation(operationContext, lockTaken, linkedCancellation, operationAdmitted);
        }
    }

    public async ValueTask<BackNavigationResult> BackAsync(
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        return await BackAsync(
            new BackNavigationRequest(windowId, BackNavigationSource.ApplicationCommand),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<BackNavigationResult> BackAsync(
        BackNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        RouterOperationContext? operationContext = null;
        var operationAdmitted = false;
        var lockTaken = false;
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            operationContext = EnterOperationContext();
            BeginOperation();
            operationAdmitted = true;
            CancellationToken operationCancellation = CreateOperationCancellation(
                cancellationToken,
                out linkedCancellation);
            await _operationLock.WaitAsync(operationCancellation).ConfigureAwait(false);
            lockTaken = true;
            operationCancellation.ThrowIfCancellationRequested();
            return await BackCoreAsync(request, operationCancellation).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation(operationContext, lockTaken, linkedCancellation, operationAdmitted);
        }
    }

    public async ValueTask<NavigationResult> ReconcileAsync(
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);

        RouterOperationContext? operationContext = null;
        var operationAdmitted = false;
        var lockTaken = false;
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            operationContext = EnterOperationContext();
            BeginOperation();
            operationAdmitted = true;
            CancellationToken operationCancellation = CreateOperationCancellation(
                cancellationToken,
                out linkedCancellation);
            await _operationLock.WaitAsync(operationCancellation).ConfigureAwait(false);
            lockTaken = true;
            operationCancellation.ThrowIfCancellationRequested();
            return await ReconcileAdmittedAsync(reconciliation, operationCancellation).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation(operationContext, lockTaken, linkedCancellation, operationAdmitted);
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
        _ = StartShutdown();
    }

    public ValueTask DisposeAsync()
    {
        Task shutdown = StartShutdown();
        return shutdown.IsCompletedSuccessfully ? ValueTask.CompletedTask : new ValueTask(shutdown);
    }

    private ValueTask<NavigationResult> ReconcileAdmittedAsync(
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken)
    {
        RouterNavigationRequest request = RouterNavigationRequest.FromRoute(
            reconciliation.Route ?? new ReconciledRoute(),
            NavigationRequestSource.HostReconciliation);

        return ReconcileCoreAsync(request, reconciliation, cancellationToken);
    }

    private async ValueTask<NavigationResult> NavigateCoreAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using RouterNavigationActivityScope activity =
            _diagnostics.StartActivity("Navigation.Navigate", operationId, request);
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
            activity.SetTag("navigation.route_type", route.GetType().FullName);
            activity.SetTag("navigation.route_template", routeDefinition?.Template.Value);
            activity.SetTag("navigation.disposition", effectiveRequest.Disposition);

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
                activity.SetTag("navigation.plan_kind", plan.Kind);
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.PlanningCompleted,
                    operationId,
                    plan.Kind.ToString(),
                    RouterNavigationDiagnostics.Duration(planningTimer,
                        (NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString()),
                        (NavigationDiagnosticDataKeys.ContextualFallback, plan.ContextualFallback)));
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
            RouterNavigationRequest finalizedRequest = effectiveRequest;
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
            activity.SetStatus(ActivityStatusCode.Ok);

            return new NavigationResult(finalRoute, plan, CurrentState, true);
        }
        catch (Exception ex)
        {
            activity.SetStatus(ActivityStatusCode.Error);
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
        BackNavigationRequest backRequest,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        using RouterNavigationActivityScope activity =
            _diagnostics.StartActivity("Navigation.Back", operationId, backRequest.Source.ToString());
        var timer = Stopwatch.StartNew();

        var backContext = new BackNavigationContext(CurrentState, backRequest.WindowId, operationId);
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
                activity.SetStatus(ActivityStatusCode.Ok);
                return BackNavigationResult.Unhandled;
            }

            NavigationStateValidator.ValidatePlan(plan, $"Back navigator '{_backNavigator.GetType().Name}'");
            var policyContext = new BackNavigationPolicyContext(backRequest, backContext, plan);
            foreach (IBackNavigationPolicy policy in _backNavigationPolicies)
            {
                var policyTimer = Stopwatch.StartNew();
                string policyName = policy.GetType().FullName ?? policy.GetType().Name;
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.BackPolicyStarted,
                    operationId,
                    policyName,
                    RouterNavigationDiagnostics.Data(
                        (NavigationDiagnosticDataKeys.PolicyType, policyName),
                        (NavigationDiagnosticDataKeys.BackSource, backRequest.Source.ToString())));

                BackNavigationPolicyDecision decision;
                try
                {
                    decision = await policy.EvaluateAsync(policyContext, cancellationToken).ConfigureAwait(false);
                    if (!Enum.IsDefined(decision))
                    {
                        throw new InvalidOperationException(
                            $"Back policy '{policyName}' returned unknown decision value '{(int)decision}'.");
                    }

                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.BackPolicyCompleted,
                        operationId,
                        decision.ToString(),
                        RouterNavigationDiagnostics.Duration(
                            policyTimer,
                            (NavigationDiagnosticDataKeys.PolicyType, policyName),
                            (NavigationDiagnosticDataKeys.Decision, decision.ToString())));
                }
                catch (Exception ex)
                {
                    _diagnostics.WriteFailure(
                        NavigationDiagnosticEventKind.BackPolicyFailed,
                        operationId,
                        policyName,
                        ex,
                        policyTimer,
                        (NavigationDiagnosticDataKeys.PolicyType, policyName));
                    throw;
                }

                if (decision == BackNavigationPolicyDecision.Cancel)
                {
                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.BackCanceled,
                        operationId,
                        policyName,
                        RouterNavigationDiagnostics.Duration(
                            timer,
                            (NavigationDiagnosticDataKeys.PolicyType, policyName),
                            (NavigationDiagnosticDataKeys.WindowId, diagnosticWindowId)));
                    activity.SetStatus(ActivityStatusCode.Ok);
                    return BackNavigationResult.Canceled;
                }
            }

            string? resolvedWindowId = backContext.ResolvedWindowId ?? backContext.RequestedWindowId;
            AppRoute route = ResolvePresentedRoute(plan.TargetState, resolvedWindowId, new BackRoute());
            RouterNavigationRequest request =
                RouterNavigationRequest.FromRoute(route, ResolveBackRequestSource(backRequest.Source), resolvedWindowId);

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
            activity.SetStatus(ActivityStatusCode.Ok);
            return BackNavigationResult.CompletedBy(new NavigationResult(route, plan, CurrentState, true));
        }
        catch (Exception ex)
        {
            activity.SetStatus(ActivityStatusCode.Error);
            _diagnostics.WriteFailure(
                NavigationDiagnosticEventKind.BackFailed,
                operationId,
                backRequest.WindowId ?? "active",
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
        using RouterNavigationActivityScope activity =
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
            RouterNavigationRequest finalizedRequest = request;
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
            activity.SetStatus(ActivityStatusCode.Ok);
            return new NavigationResult(finalRoute, plan, CurrentState, false);
        }
        catch (Exception ex)
        {
            activity.SetStatus(ActivityStatusCode.Error);
            _diagnostics.WriteFailure(NavigationDiagnosticEventKind.ReconciliationFailed, operationId,
                reconciliation.Source.ToString(), ex, timer);
            throw;
        }
    }

    private void OnPresenterReconciliationRequested(object? sender, NavigationReconciliationRequestedEventArgs e)
    {
        if (!TryBeginOperation())
            return;

        Task task;
        try
        {
            lock (_reconciliationGate)
            {
                NavigationReconciliation reconciliation = e.Reconciliation;
                _reconciliationQueue = _reconciliationQueue
                    .ContinueWith(
                        static async (_, state) =>
                        {
                            (RouterNavigator navigator, NavigationReconciliation nextReconciliation) =
                                ((RouterNavigator, NavigationReconciliation))state!;
                            var lockTaken = false;
                            RouterOperationContext? operationContext = null;
                            try
                            {
                                await navigator._operationLock.WaitAsync(
                                    navigator._shutdownCancellation.Token).ConfigureAwait(false);
                                lockTaken = true;
                                navigator._shutdownCancellation.Token.ThrowIfCancellationRequested();
                                operationContext = navigator.EnterOperationContext();
                                await navigator.ReconcileAdmittedAsync(
                                    nextReconciliation,
                                    navigator._shutdownCancellation.Token).ConfigureAwait(false);
                            }
                            finally
                            {
                                navigator.ExitOperation(
                                    operationContext,
                                    lockTaken,
                                    linkedCancellation: null,
                                    operationAdmitted: true);
                            }
                        },
                        (this, reconciliation),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default)
                    .Unwrap();
                task = _reconciliationQueue;
            }
        }
        catch
        {
            EndOperation();
            throw;
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

    private void BeginOperation()
    {
        if (!TryBeginOperation())
            throw new ObjectDisposedException(nameof(RouterNavigator));
    }

    private RouterOperationContext EnterOperationContext()
    {
        if (ExecutionContext.IsFlowSuppressed())
        {
            throw new InvalidOperationException(
                "Router operations cannot be started while execution-context flow is suppressed because " +
                "reentrancy detection must flow across asynchronous callbacks.");
        }

        RouterOperationContext? current = CurrentOperationContext.Value;
        for (RouterOperationContext? candidate = current; candidate is not null; candidate = candidate.Parent)
        {
            if (ReferenceEquals(candidate.ActiveNavigator, this))
            {
                throw new InvalidOperationException(
                    "Reentrant router operations are not supported. NavigateAsync, BackAsync, and ReconcileAsync " +
                    "cannot be called on the same RouterNavigator until its current operation has completed.");
            }
        }

        var context = new RouterOperationContext(this, current);
        CurrentOperationContext.Value = context;
        return context;
    }

    private void ExitOperation(
        RouterOperationContext? operationContext,
        bool lockTaken,
        CancellationTokenSource? linkedCancellation,
        bool operationAdmitted)
    {
        // A waiter may have captured this execution context. Mark its token inactive before waking the waiter so the
        // completed operation is not mistaken for reentrancy in that continuation.
        operationContext?.Deactivate();
        try
        {
            if (lockTaken)
                _operationLock.Release();
        }
        finally
        {
            try
            {
                linkedCancellation?.Dispose();
            }
            finally
            {
                if (operationAdmitted)
                    EndOperation();
            }
        }
    }

    private bool TryBeginOperation()
    {
        lock (_lifetimeGate)
        {
            if (_shutdownStarted)
                return false;

            _activeOperations++;
            return true;
        }
    }

    private void EndOperation()
    {
        var completeShutdown = false;
        lock (_lifetimeGate)
        {
            _activeOperations--;
            if (_activeOperations < 0)
                throw new InvalidOperationException("Router operation admission count became negative.");

            if (_shutdownStarted && _shutdownSignalIssued && _shutdownCancellationCompleted &&
                _activeOperations == 0 && !_operationLockDisposed)
            {
                _operationLockDisposed = true;
                completeShutdown = true;
            }
        }

        if (completeShutdown)
            CompleteShutdown();
    }

    private Task StartShutdown()
    {
        var unsubscribe = false;
        var signalShutdown = false;
        lock (_lifetimeGate)
        {
            if (!_shutdownStarted)
            {
                _shutdownStarted = true;
                unsubscribe = true;
                signalShutdown = true;
            }
        }

        try
        {
            if (unsubscribe)
                _presenter.ReconciliationRequested -= OnPresenterReconciliationRequested;

            if (signalShutdown)
            {
                try
                {
                    _ = ObserveShutdownCancellationAsync(_shutdownCancellation.CancelAsync());
                }
                catch (Exception)
                {
                    _ = ObserveShutdownCancellationAsync(Task.CompletedTask);
                }
            }
        }
        finally
        {
            var completeShutdown = false;
            lock (_lifetimeGate)
            {
                if (signalShutdown)
                    _shutdownSignalIssued = true;

                if (_shutdownStarted && _shutdownSignalIssued && _shutdownCancellationCompleted &&
                    _activeOperations == 0 && !_operationLockDisposed)
                {
                    _operationLockDisposed = true;
                    completeShutdown = true;
                }
            }

            if (completeShutdown)
                CompleteShutdown();
        }

        return _shutdownCompletion.Task;
    }

    private async Task ObserveShutdownCancellationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancellation callback ownership remains with the callback registrant.
        }

        var completeShutdown = false;
        lock (_lifetimeGate)
        {
            _shutdownCancellationCompleted = true;
            if (_shutdownStarted && _shutdownSignalIssued && _activeOperations == 0 && !_operationLockDisposed)
            {
                _operationLockDisposed = true;
                completeShutdown = true;
            }
        }

        if (completeShutdown)
            CompleteShutdown();
    }

    private void CompleteShutdown()
    {
        try
        {
            _operationLock.Dispose();
            _shutdownCancellation.Dispose();
        }
        finally
        {
            _shutdownCompletion.TrySetResult(true);
        }
    }

    private CancellationToken CreateOperationCancellation(
        CancellationToken callerCancellation,
        out CancellationTokenSource? linkedCancellation)
    {
        if (!callerCancellation.CanBeCanceled)
        {
            linkedCancellation = null;
            return _shutdownCancellation.Token;
        }

        linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation,
            _shutdownCancellation.Token);
        return linkedCancellation.Token;
    }

    private sealed class RouterOperationContext(
        RouterNavigator navigator,
        RouterOperationContext? parent)
    {
        private RouterNavigator? _activeNavigator = navigator;

        public RouterNavigator? ActiveNavigator => Volatile.Read(ref _activeNavigator);

        public RouterOperationContext? Parent { get; } = parent;

        public void Deactivate()
        {
            Interlocked.Exchange(ref _activeNavigator, null);
            if (ReferenceEquals(CurrentOperationContext.Value, this))
                CurrentOperationContext.Value = Parent;
        }
    }

    private sealed record ReconciledRoute : AppRoute;

    private static NavigationRequestSource ResolveBackRequestSource(BackNavigationSource source)
    {
        return source switch
        {
            BackNavigationSource.Host => NavigationRequestSource.HostBack,
            _ => NavigationRequestSource.InAppCommand
        };
    }

    private sealed record BackRoute : AppRoute;
}

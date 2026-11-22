using System.Diagnostics;
using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.History;
using AdamE.AppNav.Leases;
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
    private readonly RouteEntryLeaseRegistry? _entryLeases;
    private readonly TimeSpan _entryLeaseShutdownWarningDelay;
    // Admits waiting reconciliations ahead of queued navigation, Back and refresh operations.
    private readonly RouterOperationGate _operationLock = new();
    private readonly Lock _lifetimeGate = new();
    private readonly Lock _reconciliationGate = new();
    private readonly TaskCompletionSource<bool> _shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly int _maxHistoryEntries;
    private Task _reconciliationQueue = Task.CompletedTask;
    private Task _entryLeaseDrain = Task.CompletedTask;
    private Task _settlementDrain = Task.CompletedTask;
    private int _activeOperations;
    private bool _shutdownStarted;
    private bool _shutdownSignalIssued;
    private bool _shutdownCancellationCompleted;
    private bool _shutdownCompleted;

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
        if (!IsValidWarningDelay(options.EntryLeaseShutdownWarningDelay))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.EntryLeaseShutdownWarningDelay,
                "EntryLeaseShutdownWarningDelay must be positive and at most about 49 days, or " +
                "Timeout.InfiniteTimeSpan to turn the warning off.");
        }

        _entryLeaseShutdownWarningDelay = options.EntryLeaseShutdownWarningDelay;
        _entryLeases = options.EntryLeases;
        _entryLeases?.Bind(CurrentState);

        _presenter.ReconciliationRequested += OnPresenterReconciliationRequested;
    }

    public NavigationState CurrentState { get; private set; }

    public NavigationHistory History { get; private set; }

    public async ValueTask<NavigationResult> NavigateAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (RouteEntryLeaseNavigation.TryGetLease(request, out RouteEntryLease? lease) &&
            !ReferenceEquals(lease.Registry, _entryLeases))
        {
            throw new InvalidOperationException(
                "The request's route-entry lease belongs to another registry, or this router has no entry leases; " +
                "send a lease only through the router configured with the registry that opened it.");
        }

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
            await _operationLock.EnterOperationAsync(operationCancellation).ConfigureAwait(false);
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
            await _operationLock.EnterOperationAsync(operationCancellation).ConfigureAwait(false);
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
            await _operationLock.ReserveReconciliation().EnterAsync(operationCancellation).ConfigureAwait(false);
            lockTaken = true;
            operationCancellation.ThrowIfCancellationRequested();
            return await ReconcileAdmittedAsync(reconciliation, operationCancellation).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation(operationContext, lockTaken, linkedCancellation, operationAdmitted);
        }
    }

    public async ValueTask RefreshPresentationAsync(CancellationToken cancellationToken = default)
    {
        RouterOperationContext? operationContext = null;
        var operationAdmitted = false;
        var lockTaken = false;
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            operationContext = EnterOperationContext();
            BeginOperation();
            operationAdmitted = true;
            CancellationToken operationCancellation = CreateOperationCancellation(cancellationToken, out linkedCancellation);
            await _operationLock.EnterOperationAsync(operationCancellation).ConfigureAwait(false);
            lockTaken = true;
            operationCancellation.ThrowIfCancellationRequested();
            // Read only AFTER admission: preceding navigation may still be committing while this request queues.
            NavigationState state = CurrentState;
            AppRoute route = History.Current?.Route ?? new RefreshedRoute();
            RouterNavigationRequest request = History.Current?.Request
                ?? RouterNavigationRequest.FromRoute(route, NavigationRequestSource.InAppCommand);
            var operationId = Guid.NewGuid().ToString("N");
            var plan = new NavigationPlan(state, NavigationPlanKind.Refresh, "Native host recreation");
            using RouterNavigationActivityScope activity =
                _diagnostics.StartActivity("Navigation.Refresh", operationId, plan.Kind.ToString());
            var timer = Stopwatch.StartNew();
            _diagnostics.Write(NavigationDiagnosticEventKind.PresentationStarted, operationId, plan.Kind.ToString());
            try
            {
                _entryLeases?.BeginPresentation();
                RecordPresentationSettlement(state, plan.Kind, operationId);
                await _presenter.ApplyAsync(plan,
                    new NavigationPresentationContext(request, route, state, operationId), operationCancellation)
                    .ConfigureAwait(false);
                _diagnostics.Write(NavigationDiagnosticEventKind.PresentationCompleted, operationId, plan.Kind.ToString(),
                    RouterNavigationDiagnostics.Duration(timer));
                activity.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception exception)
            {
                activity.SetStatus(ActivityStatusCode.Error);
                _diagnostics.WriteFailure(NavigationDiagnosticEventKind.PresentationFailed,
                    operationId, plan.Kind.ToString(), exception, timer);
                throw;
            }
            finally
            {
                // A refresh commits nothing, so leases its presenter opened outside the state end with it.
                _entryLeases?.EndPresentation(state);
            }
            // Refresh is neither navigation nor an observed host-state replacement. State/history stay identical.
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
        // A failed lease release faults shutdown; only DisposeAsync can surface it, so observe it here.
        _ = StartShutdown().ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
                if (_entryLeases is not null)
                    RouteEntryLeaseRegistry.ValidateUniqueEntryIds(plan.TargetState, "App navigation planner");
                if (RouteEntryLeaseNavigation.TryGetLease(request, out RouteEntryLease? sentLease) &&
                    sentLease.EntryId is { } sentEntryId &&
                    !RouteEntryLeaseRegistry.EntryIds(plan.TargetState).Contains(sentEntryId))
                {
                    // The lease will be abandoned when the request ends; say why instead of leaving it silent.
                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.EntryLeaseNotInPlan,
                        operationId,
                        "The plan does not contain the entry of the route-entry lease sent with this request.",
                        RouterNavigationDiagnostics.Data((NavigationDiagnosticDataKeys.PlanKind, plan.Kind.ToString())));
                }
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
                _entryLeases?.BeginPresentation();
                RecordPresentationSettlement(CurrentState, plan.Kind, operationId);
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
                _entryLeases?.Abandon(CurrentState);
                throw;
            }

            CurrentState = plan.TargetState;
            _entryLeases?.Commit(CurrentState);
            History = History.Push(
                CreateHistoryEntry(finalizedRequest, finalRoute, CurrentState),
                _maxHistoryEntries);
            RecordPresentationSettlement(CurrentState, plan.Kind, operationId);
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

        var backContext = new BackNavigationContext(CurrentState, backRequest.WindowId, operationId)
        {
            PresentsSplitColumnsSideBySide = backRequest.PresentsSplitColumnsSideBySide
        };
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
            if (backRequest.ExpectedModalEntryId is { } expectedModal
                && backContext.Window?.Modals.LastOrDefault()?.RouteEntry.Id != expectedModal)
            {
                _diagnostics.Write(NavigationDiagnosticEventKind.BackCanceled, operationId,
                    "The modal that requested Back is no longer current.");
                activity.SetStatus(ActivityStatusCode.Ok);
                return BackNavigationResult.Canceled;
            }

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

            string backPlanSource = $"Back navigator '{_backNavigator.GetType().Name}'";
            NavigationStateValidator.ValidatePlan(plan, backPlanSource);
            if (_entryLeases is not null)
                RouteEntryLeaseRegistry.ValidateUniqueEntryIds(plan.TargetState, backPlanSource);
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
                _entryLeases?.BeginPresentation();
                RecordPresentationSettlement(CurrentState, plan.Kind, operationId);
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
                _entryLeases?.Abandon(CurrentState);
                throw;
            }

            CurrentState = plan.TargetState;
            _entryLeases?.Commit(CurrentState);
            History = History.Push(CreateHistoryEntry(request, route, CurrentState),
                _maxHistoryEntries);
            RecordPresentationSettlement(CurrentState, plan.Kind, operationId);

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

        if (!ReferenceEquals(reconciliation.BaseState, CurrentState))
        {
            _diagnostics.Write(
                NavigationDiagnosticEventKind.ReconciliationRejected,
                operationId,
                reconciliation.Source.ToString(),
                RouterNavigationDiagnostics.Duration(timer,
                    (NavigationDiagnosticDataKeys.ReconciliationSource, reconciliation.Source.ToString())));
            activity.SetStatus(ActivityStatusCode.Error);
            throw new StaleNavigationReconciliationException(
                reconciliation,
                CurrentState,
                "The navigation reconciliation was rejected because the router state changed after the host observed it.");
        }

        try
        {
            NavigationStateValidator.ValidatePlan(plan, "Navigation reconciliation");
            if (_entryLeases is not null)
                RouteEntryLeaseRegistry.ValidateUniqueEntryIds(plan.TargetState, "Navigation reconciliation");

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
                _entryLeases?.BeginPresentation();
                RecordPresentationSettlement(CurrentState, plan.Kind, operationId);
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
                _entryLeases?.Abandon(CurrentState);
                throw;
            }

            CurrentState = plan.TargetState;
            _entryLeases?.Commit(CurrentState);
            History = History.Push(
                CreateHistoryEntry(finalizedRequest, finalRoute, CurrentState),
                _maxHistoryEntries);
            RecordPresentationSettlement(CurrentState, plan.Kind, operationId);

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

    private void RecordPresentationSettlement(NavigationState state, NavigationPlanKind kind, string operationId)
    {
        if (_presenter is INavigationPresentationSettlement settlement)
        {
            RouterOperationContext context = CurrentOperationContext.Value!;
            context.Settlement = new(settlement, state, kind, operationId);
        }
    }

    private async Task NotifyPresentationSettledAsync(INavigationPresentationSettlement settlement,
        NavigationState state, NavigationPlanKind kind, string operationId)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await settlement.OnPresentationSettledAsync(state, operationId).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _diagnostics.WriteFailure(NavigationDiagnosticEventKind.PresentationSettlementFailed,
                operationId, kind.ToString(), exception, timer);
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
                // Take its place in line now, in report order, so a navigation queued behind the current operation
                // gives way to it.
                RouterOperationGate.Reservation reservation = _operationLock.ReserveReconciliation();
                _reconciliationQueue = _reconciliationQueue
                    .ContinueWith(
                        static async (_, state) =>
                        {
                            (RouterNavigator navigator, NavigationReconciliation nextReconciliation,
                                RouterOperationGate.Reservation nextReservation) =
                                ((RouterNavigator, NavigationReconciliation, RouterOperationGate.Reservation))state!;
                            var lockTaken = false;
                            RouterOperationContext? operationContext = null;
                            try
                            {
                                await nextReservation.EnterAsync(
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
                        (this, reconciliation, reservation),
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
        catch (StaleNavigationReconciliationException)
        {
            // The rejection was already reported as ReconciliationRejected; it is not a failure.
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
                _operationLock.Exit();
        }
        finally
        {
            try
            {
                linkedCancellation?.Dispose();
            }
            finally
            {
                // Register settlement while this operation still holds admission. Once the count reaches
                // zero a pending shutdown completes, and a settlement started after that would escape the
                // drain disposal waits on.
                StartPresentationSettlement(operationContext);
                if (operationAdmitted)
                    EndOperation();
            }
        }

        if (operationContext?.Settlement is { } settlement)
        {
            operationContext.Settlement = null;
            // Cleanup runs independently of router admission. Do not flow an active operation
            // context into arbitrary presenter code.
            if (ExecutionContext.IsFlowSuppressed())
                _ = Task.Run(SettleAsync);
            else
                using (ExecutionContext.SuppressFlow())
                    _ = Task.Run(SettleAsync);

            Task SettleAsync() => NotifyPresentationSettledAsync(
                settlement.Presenter, settlement.State, settlement.Kind, settlement.OperationId);
        }
    }

    private void StartPresentationSettlement(RouterOperationContext? operationContext)
    {
        if (operationContext?.Settlement is not { } settlement)
            return;

        operationContext.Settlement = null;
        // Cleanup runs independently of router admission and never blocks the operation, but disposal
        // waits for it. Do not flow an active operation context into arbitrary presenter code.
        Task settling;
        if (ExecutionContext.IsFlowSuppressed())
        {
            settling = Task.Run(SettleAsync);
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
                settling = Task.Run(SettleAsync);
        }

        lock (_lifetimeGate)
        {
            // A faulted drain is dropped rather than chained: its failure was already reported.
            _settlementDrain = _settlementDrain.IsCompleted
                ? settling
                : Task.WhenAll(_settlementDrain, settling);
        }

        Task SettleAsync() => NotifyPresentationSettledAsync(
            settlement.Presenter, settlement.State, settlement.Kind, settlement.OperationId);
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
                _activeOperations == 0 && !_shutdownCompleted)
            {
                _shutdownCompleted = true;
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
                // Retire every lease first: their release may need presenter teardown that runs alongside this.
                if (_entryLeases is not null)
                {
                    _entryLeaseDrain = _entryLeases.ShutdownAsync();
                    // Timed from the start of disposal, so a presenter whose ApplyAsync never returns is reported too.
                    _ = WarnIfEntryLeasesHoldShutdownAsync();
                }

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
                    _activeOperations == 0 && !_shutdownCompleted)
                {
                    _shutdownCompleted = true;
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
            if (_shutdownStarted && _shutdownSignalIssued && _activeOperations == 0 && !_shutdownCompleted)
            {
                _shutdownCompleted = true;
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
            _shutdownCancellation.Dispose();
        }
        finally
        {
            Task entryLeaseDrain = _entryLeaseDrain;
            Task settlementDrain;
            lock (_lifetimeGate)
                settlementDrain = _settlementDrain;

            if (entryLeaseDrain.IsCompletedSuccessfully && settlementDrain.IsCompleted)
                _shutdownCompletion.TrySetResult(true);
            else
                _ = CompleteShutdownAfterDrainsAsync(entryLeaseDrain, settlementDrain);
        }
    }

    private async Task CompleteShutdownAfterDrainsAsync(Task entryLeaseDrain, Task settlementDrain)
    {
        // Settlement releases what an entry lease may still be waiting on, so it is awaited first. Its
        // failures are reported through diagnostics and never change an operation's outcome, so they must
        // not fault disposal either: disposal only waits for settlement to finish.
        try
        {
            await settlementDrain.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reported when it happened; nothing further to surface here.
        }

        try
        {
            await entryLeaseDrain.ConfigureAwait(false);
            _shutdownCompletion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            // A release failed: its resources stay retained, and disposal throws so the owner disposes nothing after.
            _shutdownCompletion.TrySetException(ex);
        }
    }

    // Task.Delay accepts at most uint.MaxValue - 1 milliseconds.
    internal static bool IsValidWarningDelay(TimeSpan delay)
    {
        return delay == Timeout.InfiniteTimeSpan ||
               (delay > TimeSpan.Zero && delay.TotalMilliseconds <= uint.MaxValue - 1);
    }

    // Shutdown has no timeout; when it is still waiting after the delay, say which leases hold it and why.
    private async Task WarnIfEntryLeasesHoldShutdownAsync()
    {
        if (_entryLeases is null || _entryLeaseShutdownWarningDelay == Timeout.InfiniteTimeSpan)
            return;

        using var delay = new CancellationTokenSource();
        Task finished = await Task.WhenAny(
            _entryLeaseDrain,
            Task.Delay(_entryLeaseShutdownWarningDelay, delay.Token)).ConfigureAwait(false);
        if (finished == _entryLeaseDrain)
        {
            await delay.CancelAsync().ConfigureAwait(false);
            return;
        }

        (IReadOnlyList<string> held, string summary) = _entryLeases.DescribeUnsettled();
        if (held.Count == 0)
            return;

        int operations;
        lock (_lifetimeGate)
        {
            operations = _activeOperations;
        }

        if (operations > 0)
            summary += $"; navigation operations still running: {operations}";

        // The message names entries by ID, which only full-mode diagnostics keep; Count and Reason survive safe mode.
        _diagnostics.Write(
            NavigationDiagnosticEventKind.EntryLeaseShutdownWaiting,
            Guid.NewGuid().ToString("N"),
            $"Router disposal is still waiting for {held.Count} route-entry lease(s) after " +
            $"{_entryLeaseShutdownWarningDelay.TotalSeconds:0.###} s: " + string.Join("; ", held) +
            (operations > 0 ? $"; {operations} navigation operation(s), such as a presenter's ApplyAsync, still running" : "") +
            ".",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.Count] = held.Count,
                [NavigationDiagnosticDataKeys.Reason] = summary
            });
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

    private readonly record struct PresentationSettlement(INavigationPresentationSettlement Presenter,
        NavigationState State, NavigationPlanKind Kind, string OperationId);

    private sealed class RouterOperationContext(
        RouterNavigator navigator,
        RouterOperationContext? parent)
    {
        private RouterNavigator? _activeNavigator = navigator;

        public RouterNavigator? ActiveNavigator => Volatile.Read(ref _activeNavigator);

        public RouterOperationContext? Parent { get; } = parent;
        public PresentationSettlement? Settlement { get; set; }

        public void Deactivate()
        {
            Interlocked.Exchange(ref _activeNavigator, null);
            if (ReferenceEquals(CurrentOperationContext.Value, this))
                CurrentOperationContext.Value = Parent;
        }
    }

    private sealed record ReconciledRoute : AppRoute;
    private sealed record RefreshedRoute : AppRoute;

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

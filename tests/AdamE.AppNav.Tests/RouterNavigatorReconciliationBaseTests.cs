using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;
using Microsoft.Extensions.Logging;

namespace AdamE.AppNav.Tests;

public sealed class RouterNavigatorReconciliationBaseTests
{
    private static readonly NavigationState InitialState = StackState("home", new TestRoutes.StoreRoute("northwind"));

    private static readonly NavigationState ReconciledState =
        StackState("catalog", new TestRoutes.CatalogRoute("northwind"));

    [Fact]
    public async Task ReconcileAsyncRejectsStaleBaseStateBeforePresentation()
    {
        var presenter = new RecordingNavigationPresenter();
        var events = new List<NavigationDiagnosticEventKind>();
        var levels = new List<LogLevel>();
        RouterNavigator navigator = CreateNavigator(presenter, events, levels);
        NavigationState staleState = StackState("old", new TestRoutes.StoreRoute("contoso"));
        var reconciliation = new NavigationReconciliation(
            staleState,
            ReconciledState,
            NavigationReconciliationSource.HostBack,
            new TestRoutes.CatalogRoute("northwind"));

        StaleNavigationReconciliationException exception =
            await Assert.ThrowsAsync<StaleNavigationReconciliationException>(() =>
                navigator.ReconcileAsync(reconciliation).AsTask());

        Assert.Same(reconciliation, exception.Reconciliation);
        Assert.Same(InitialState, exception.CurrentState);
        Assert.Equal(0, presenter.ApplyCount);
        Assert.Same(InitialState, navigator.CurrentState);
        Assert.Empty(navigator.History.Entries);
        Assert.Equal(
            new[]
            {
                NavigationDiagnosticEventKind.ReconciliationStarted,
                NavigationDiagnosticEventKind.ReconciliationRejected
            },
            events);
        Assert.Equal(LogLevel.Warning, levels[^1]);
    }

    [Fact]
    public async Task ReconcileAsyncTreatsAnEquivalentRebuiltBaseStateAsStale()
    {
        var presenter = new RecordingNavigationPresenter();
        var events = new List<NavigationDiagnosticEventKind>();
        RouterNavigator navigator = CreateNavigator(presenter, events);
        NavigationState rebuiltState = StackState("home", new TestRoutes.StoreRoute("northwind"));

        await Assert.ThrowsAsync<StaleNavigationReconciliationException>(() => navigator
            .ReconcileAsync(new NavigationReconciliation(
                rebuiltState,
                ReconciledState,
                NavigationReconciliationSource.HostBack))
            .AsTask());

        Assert.Equal(0, presenter.ApplyCount);
        Assert.Same(InitialState, navigator.CurrentState);
    }

    [Fact]
    public async Task ReconcileAsyncCommitsWhenBaseStateIsCurrent()
    {
        var presenter = new RecordingNavigationPresenter();
        var events = new List<NavigationDiagnosticEventKind>();
        RouterNavigator navigator = CreateNavigator(presenter, events);

        NavigationResult result = await navigator.ReconcileAsync(new NavigationReconciliation(
            navigator.CurrentState,
            ReconciledState,
            NavigationReconciliationSource.HostBack,
            new TestRoutes.CatalogRoute("northwind")));

        Assert.Same(ReconciledState, result.State);
        Assert.Equal(1, presenter.ApplyCount);
        Assert.Same(ReconciledState, navigator.CurrentState);
        Assert.Single(navigator.History.Entries);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationCompleted, events);
        Assert.DoesNotContain(NavigationDiagnosticEventKind.ReconciliationRejected, events);
    }

    [Fact]
    public async Task QueuedPresenterReconciliationIsRejectedWhenNavigationCommitsFirst()
    {
        var presenter = new BlockingPresenter();
        var events = new List<NavigationDiagnosticEventKind>();
        RouterNavigator navigator = CreateNavigator(presenter, events);
        Task<NavigationResult> navigation = navigator
            .NavigateAsync(new TestRoutes.StoreRoute("contoso"))
            .AsTask();
        await presenter.BlockedApplyStarted;

        presenter.RequestReconciliation(new NavigationReconciliation(
            InitialState,
            ReconciledState,
            NavigationReconciliationSource.HostBack,
            new TestRoutes.CatalogRoute("northwind")));
        presenter.ReleaseBlockedApply();
        NavigationResult navigated = await navigation;
        await Assert.ThrowsAsync<StaleNavigationReconciliationException>(navigator.WhenReconciliationIdleAsync);

        Assert.Same(navigated.State, navigator.CurrentState);
        Assert.Single(navigator.History.Entries);
        Assert.DoesNotContain(presenter.AppliedPlans, plan => plan.Kind == NavigationPlanKind.Reconcile);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationRejected, events);
        Assert.DoesNotContain(NavigationDiagnosticEventKind.ReconciliationFailed, events);
        Assert.DoesNotContain(NavigationDiagnosticEventKind.ReconciliationCompleted, events);
    }

    [Fact]
    public async Task QueuedPresenterReconciliationCommitsWhenBasedOnTheNavigationItWaitedFor()
    {
        var presenter = new BlockingPresenter();
        var events = new List<NavigationDiagnosticEventKind>();
        RouterNavigator navigator = CreateNavigator(presenter, events);
        Task<NavigationResult> navigation = navigator
            .NavigateAsync(new TestRoutes.StoreRoute("contoso"))
            .AsTask();
        await presenter.BlockedApplyStarted;

        presenter.RequestReconciliation(new NavigationReconciliation(
            presenter.AppliedPlans[^1].TargetState,
            ReconciledState,
            NavigationReconciliationSource.HostBack,
            new TestRoutes.CatalogRoute("northwind")));
        presenter.ReleaseBlockedApply();
        await navigation;
        await navigator.WhenReconciliationIdleAsync();

        Assert.Same(ReconciledState, navigator.CurrentState);
        Assert.Equal(2, navigator.History.Entries.Count);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationCompleted, events);
        Assert.DoesNotContain(NavigationDiagnosticEventKind.ReconciliationRejected, events);
    }

    [Fact]
    public async Task ChainedPresenterReconciliationsCommitInOrder()
    {
        var presenter = new BlockingPresenter();
        var events = new List<NavigationDiagnosticEventKind>();
        RouterNavigator navigator = CreateNavigator(presenter, events);
        NavigationState finalState = StackState("store", new TestRoutes.StoreRoute("contoso"));

        presenter.RequestReconciliation(new NavigationReconciliation(
            InitialState,
            ReconciledState,
            NavigationReconciliationSource.HostBack));
        presenter.RequestReconciliation(new NavigationReconciliation(
            ReconciledState,
            finalState,
            NavigationReconciliationSource.HostBack));
        await navigator.WhenReconciliationIdleAsync();

        Assert.Same(finalState, navigator.CurrentState);
        Assert.Equal(2, navigator.History.Entries.Count);
        Assert.DoesNotContain(NavigationDiagnosticEventKind.ReconciliationRejected, events);
    }

    [Fact]
    public async Task ReconciliationQueuedBehindRefreshCommitsBecauseRefreshKeepsTheBaseState()
    {
        var presenter = new BlockingPresenter(NavigationPlanKind.Refresh);
        var events = new List<NavigationDiagnosticEventKind>();
        RouterNavigator navigator = CreateNavigator(presenter, events);
        Task refresh = navigator.RefreshPresentationAsync().AsTask();
        await presenter.BlockedApplyStarted;

        presenter.RequestReconciliation(new NavigationReconciliation(
            InitialState,
            ReconciledState,
            NavigationReconciliationSource.HostBack,
            new TestRoutes.CatalogRoute("northwind")));
        presenter.ReleaseBlockedApply();
        await refresh;
        await navigator.WhenReconciliationIdleAsync();

        Assert.Same(InitialState, presenter.AppliedPlans[0].TargetState);
        Assert.Equal(
            new[] { NavigationPlanKind.Refresh, NavigationPlanKind.Reconcile },
            presenter.AppliedPlans.Select(plan => plan.Kind));
        Assert.Same(ReconciledState, navigator.CurrentState);
        Assert.Single(navigator.History.Entries);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationCompleted, events);
        Assert.DoesNotContain(NavigationDiagnosticEventKind.ReconciliationRejected, events);
    }

    private static RouterNavigator CreateNavigator(
        INavigationPresenter presenter,
        List<NavigationDiagnosticEventKind> events,
        List<LogLevel>? levels = null)
    {
        var diagnostics = new NavigationDiagnostics();
        diagnostics.EventWritten += (_, diagnosticEvent) =>
        {
            if (diagnosticEvent.Phase == NavigationDiagnosticPhase.Reconciliation)
            {
                lock (events)
                {
                    events.Add(diagnosticEvent.Kind);
                    levels?.Add(diagnosticEvent.Severity);
                }
            }
        };

        return new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter,
            new RouterNavigatorOptions
            {
                InitialState = InitialState,
                Diagnostics = diagnostics
            });
    }

    private static NavigationState StackState(string entryId, AppRoute route)
    {
        return TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry(entryId, route))));
    }

    private sealed class BlockingPresenter : INavigationPresenter
    {
        private readonly RecordingNavigationPresenter _inner = new();
        private readonly TaskCompletionSource<bool> _blockedApplyStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseBlockedApply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingPresenter(NavigationPlanKind blockedKind = NavigationPlanKind.Navigate)
        {
            _inner.OnApplyAsync = async (plan, _, _) =>
            {
                if (plan.Kind != blockedKind)
                    return;

                _blockedApplyStarted.TrySetResult(true);
                await _releaseBlockedApply.Task;
            };
        }

        public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
        {
            add => _inner.ReconciliationRequested += value;
            remove => _inner.ReconciliationRequested -= value;
        }

        public Task BlockedApplyStarted => _blockedApplyStarted.Task;

        public IReadOnlyList<NavigationPlan> AppliedPlans => _inner.AppliedPlans;

        public ValueTask ApplyAsync(
            NavigationPlan plan,
            NavigationPresentationContext context,
            CancellationToken cancellationToken = default)
        {
            return _inner.ApplyAsync(plan, context, cancellationToken);
        }

        public void RequestReconciliation(NavigationReconciliation reconciliation)
        {
            _inner.RequestReconciliation(reconciliation);
        }

        public void ReleaseBlockedApply()
        {
            _releaseBlockedApply.TrySetResult(true);
        }
    }
}

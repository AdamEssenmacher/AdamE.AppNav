using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class RouterNavigatorPresentationTests
{
    [Fact]
    public async Task SettlementRunsAfterCommitAndFailureLeavesNavigationSuccessful()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);

        var presenter = new SettlingPresenter();
        await using var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter,
            new RouterNavigatorOptions { Diagnostics = diagnostics });
        var snapshots = new List<(NavigationState Settled, NavigationState Current, int HistoryCount)>();
        presenter.Settle = state =>
        {
            snapshots.Add((state, navigator.CurrentState, navigator.History.Entries.Count));
            throw new InvalidOperationException("Post-commit cleanup failed.");
        };

        var result = await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test));
        await navigator.RefreshPresentationAsync();

        Assert.True(result.Presented);
        Assert.Same(result.State, navigator.CurrentState);
        Assert.Equal(2, snapshots.Count);
        Assert.All(snapshots, snapshot =>
        {
            Assert.Same(result.State, snapshot.Settled);
            Assert.Same(snapshot.Settled, snapshot.Current);
            Assert.Equal(1, snapshot.HistoryCount);
        });
        var failures = events.Where(entry => entry.Kind == NavigationDiagnosticEventKind.PresentationSettlementFailed).ToArray();
        Assert.Equal(2, failures.Length);
        Assert.All(failures, failure => Assert.Equal(NavigationDiagnosticPhase.Presentation, failure.Phase));
        Assert.DoesNotContain(events, entry => entry.Kind == NavigationDiagnosticEventKind.PresentationFailed);
    }

    private sealed class SettlingPresenter : INavigationPresenter, INavigationPresentationSettlement
    {
        public Action<NavigationState>? Settle { get; set; }

        public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
        {
            add { }
            remove { }
        }

        public ValueTask ApplyAsync(NavigationPlan plan, NavigationPresentationContext context,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OnPresentationSettledAsync(NavigationState state)
        {
            Settle?.Invoke(state);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task NavigationFinalizesPresentedRouteForPresenterResultAndHistory()
    {
        var requestedRoute = new TestRoutes.StoreRoute("requested");
        var rewrittenRoute = new TestRoutes.CatalogRoute("northwind");
        var rewrittenState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("rewritten", rewrittenRoute))));
        var presenter = new RecordingNavigationPresenter();
        var planner = TestNavigationPlanner.ForState(rewrittenState);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            presenter);

        var result = await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            requestedRoute, NavigationRequestSource.Test));

        Assert.Equal(requestedRoute, planner.LastRoute);
        Assert.Equal(rewrittenState, presenter.LastPlan?.TargetState);
        Assert.Equal(rewrittenRoute, presenter.LastContext!.Route);
        Assert.Equal(rewrittenRoute, result.Route);
        Assert.Equal(rewrittenRoute, navigator.History.Current!.Route);
    }

    [Fact]
    public async Task PresenterRequestedReconciliationCommitsStateAndHistory()
    {
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);
        var reconciledRoute = new TestRoutes.CatalogRoute("northwind");
        var reconciledState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("catalog", reconciledRoute))));

        presenter.RequestReconciliation(new NavigationReconciliation(
            navigator.CurrentState,
            reconciledState,
            NavigationReconciliationSource.HostBack,
            reconciledRoute,
            "test reconciliation"));
        await navigator.WhenReconciliationIdleAsync();

        Assert.Equal(reconciledState, navigator.CurrentState);
        Assert.Equal(NavigationRequestSource.HostReconciliation, navigator.History.Current!.Request.Source);
        Assert.Equal(reconciledRoute, navigator.History.Current.Route);
        Assert.Equal(1, presenter.ApplyCount);
        Assert.Equal(NavigationPlanKind.Reconcile, presenter.LastPlan?.Kind);
    }

    [Fact]
    public async Task ReconciliationFinalizesPresentedRouteBeforeCommittingState()
    {
        var rewrittenRoute = new TestRoutes.CatalogRoute("northwind");
        var rewrittenState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("rewritten", rewrittenRoute))));
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);
        var requestedRoute = new TestRoutes.StoreRoute("requested");

        var result = await navigator.ReconcileAsync(new NavigationReconciliation(
            navigator.CurrentState,
            rewrittenState,
            NavigationReconciliationSource.HostBack,
            requestedRoute,
            "test reconciliation"));

        Assert.Equal(1, presenter.ApplyCount);
        Assert.Equal(NavigationPlanKind.Reconcile, presenter.LastPlan?.Kind);
        Assert.Equal(rewrittenState, presenter.LastPlan?.TargetState);
        Assert.Equal(rewrittenRoute, presenter.LastContext!.Route);
        Assert.Equal(rewrittenRoute, result.Route);
        Assert.Equal(rewrittenState, result.State);
        Assert.False(result.Presented);
        Assert.Equal(rewrittenState, navigator.CurrentState);
        Assert.Equal(rewrittenRoute, navigator.History.Current!.Route);
        Assert.Equal(rewrittenState, navigator.History.Current.State);
    }

    [Fact]
    public async Task ReconciliationPresenterFailureDoesNotCommitStateOrHistory()
    {
        var initialState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("home", new TestRoutes.StoreRoute("northwind")))));
        var presenter = new RecordingNavigationPresenter
        {
            ThrowOnApply = new InvalidOperationException("Presentation failed.")
        };
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter,
            new RouterNavigatorOptions
            {
                InitialState = initialState,
                Diagnostics = diagnostics
            });
        var reconciledState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("catalog", new TestRoutes.CatalogRoute("northwind")))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator.ReconcileAsync(new NavigationReconciliation(
            navigator.CurrentState,
            reconciledState,
            NavigationReconciliationSource.HostBack,
            new TestRoutes.CatalogRoute("northwind"),
            "test reconciliation")).AsTask());

        Assert.Equal(1, presenter.ApplyCount);
        Assert.Equal(initialState, navigator.CurrentState);
        Assert.Empty(navigator.History.Entries);
        Assert.Contains(NavigationDiagnosticEventKind.PresentationFailed, events);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationFailed, events);
        Assert.DoesNotContain(NavigationDiagnosticEventKind.PresentationCompleted, events);
        Assert.DoesNotContain(NavigationDiagnosticEventKind.ReconciliationCompleted, events);
    }
}

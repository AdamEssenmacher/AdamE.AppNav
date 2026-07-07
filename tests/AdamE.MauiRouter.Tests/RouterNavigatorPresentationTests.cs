using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Tests;

public sealed class RouterNavigatorPresentationTests
{
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

        var result = await navigator.NavigateAsync(requestedRoute, NavigationRequestSource.Test);

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
            reconciledState,
            NavigationReconciliationSource.NativeBackGesture,
            reconciledRoute,
            "test reconciliation"));
        await navigator.WhenReconciliationIdleAsync();

        Assert.Equal(reconciledState, navigator.CurrentState);
        Assert.Equal(NavigationRequestSource.NativeReconciliation, navigator.History.Current!.Request.Source);
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
            rewrittenState,
            NavigationReconciliationSource.NativeBackGesture,
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
            reconciledState,
            NavigationReconciliationSource.NativeBackGesture,
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

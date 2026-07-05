using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.Testing;

namespace AdamE.MauiRouter.Tests;

public sealed class TestingHarnessTests
{
    [Fact]
    public void RouteTableSubjectMatchesFormatsAndReportsFailures()
    {
        var subject = RouteTableSubject.Create(routes => routes.Map(
            "/stores/{storeId}",
            match => new TestRoutes.StoreRoute(match.Path("storeId")),
            format => format.PathParam("storeId", route => route.StoreId)));

        var route = subject.MatchRoute<TestRoutes.StoreRoute>("/stores/northwind");

        Assert.Equal("northwind", route.StoreId);
        Assert.Equal("/stores/northwind", subject.Format(route));
        subject.RoundTrips(route, "/stores/northwind");
        subject.ShouldNotMatch("/missing", "route.not_matched");
        Assert.Throws<NavigationAssertionException>(() => subject.MatchRoute<TestRoutes.CatalogRoute>("/stores/northwind"));
        Assert.Throws<NavigationAssertionException>(() => subject.ShouldNotMatch("/stores/northwind"));
    }

    [Fact]
    public async Task PlannerAndPresenterFakesRecordNavigation()
    {
        var planner = TestNavigationPlanner.EchoStack();
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(TestRoutes.CreateTable(), planner, presenter);

        var result = await navigator.NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test);

        Assert.Single(planner.Calls);
        Assert.Equal(new TestRoutes.StoreRoute("northwind"), planner.LastRoute);
        Assert.Equal(NavigationRequestSource.Test, planner.LastRequest!.Source);
        Assert.Equal(1, presenter.ApplyCount);
        Assert.Same(result.Plan, presenter.LastPlan);
        Assert.Equal(new TestRoutes.StoreRoute("northwind"), presenter.LastContext!.Route);

        var stack = NavigationStateAssert.Root<StackNode>(result.State);
        NavigationStateAssert.StackRouteTypes(stack, typeof(TestRoutes.StoreRoute));
        Assert.Equal(new TestRoutes.StoreRoute("northwind"), NavigationStateAssert.StackTop<TestRoutes.StoreRoute>(stack));
    }

    [Fact]
    public async Task RecordingPresenterSupportsFailureAndReconciliationTests()
    {
        var presenter = new RecordingNavigationPresenter
        {
            ThrowOnApply = new InvalidOperationException("Presentation failed.")
        };
        var failingNavigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => failingNavigator
            .NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test)
            .AsTask());
        Assert.Equal(1, presenter.ApplyCount);

        var reconciliationPresenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            reconciliationPresenter);
        var reconciledState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("catalog", new TestRoutes.CatalogRoute("northwind")))));

        reconciliationPresenter.RequestReconciliation(new NavigationReconciliation(
            reconciledState,
            NavigationReconciliationSource.NativeBackGesture,
            new TestRoutes.CatalogRoute("northwind"),
            "test reconciliation"));
        await navigator.WhenReconciliationIdleAsync();

        Assert.Equal(reconciledState, navigator.CurrentState);
        Assert.Equal(NavigationRequestSource.NativeReconciliation, navigator.History.Current!.Request.Source);
        Assert.Equal(1, reconciliationPresenter.ApplyCount);
        Assert.Equal(NavigationPlanKind.Reconcile, reconciliationPresenter.LastPlan?.Kind);
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

        var result = await navigator.NavigateAsync(requestedRoute, NavigationRequestSource.Test);

        Assert.Equal(requestedRoute, planner.LastRoute);
        Assert.Equal(rewrittenState, presenter.LastPlan?.TargetState);
        Assert.Equal(rewrittenRoute, presenter.LastContext!.Route);
        Assert.Equal(rewrittenRoute, result.Route);
        Assert.Equal(rewrittenRoute, navigator.History.Current!.Route);
    }

    [Fact]
    public async Task ReconciliationFinalizesPresentedRouteBeforeCommittingState()
    {
        var rewrittenState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("rewritten", new TestRoutes.CatalogRoute("northwind")))));
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
        Assert.Equal(new TestRoutes.CatalogRoute("northwind"), presenter.LastContext!.Route);
        Assert.Equal(new TestRoutes.CatalogRoute("northwind"), result.Route);
        Assert.Equal(rewrittenState, result.State);
        Assert.False(result.Presented);
        Assert.Equal(rewrittenState, navigator.CurrentState);
        Assert.Equal(new TestRoutes.CatalogRoute("northwind"), navigator.History.Current!.Route);
        Assert.Equal(rewrittenState, navigator.History.Current!.State);
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

    [Fact]
    public void StateBuildersAndAssertionsCreateReadableNavigationFixtures()
    {
        var productRoute = new TestRoutes.ProductDetailRoute("northwind", 123);
        var modalRoute = new TestRoutes.StoreRoute("northwind");
        var catalogStack = TestNavigationState.Stack(
            "catalog-stack",
            TestNavigationState.Entry("catalog", new TestRoutes.CatalogRoute("northwind")),
            TestNavigationState.Entry("product", productRoute));
        var branchHost = TestNavigationState.BranchHost(
            "branchHost",
            "catalog",
            "home",
            TestNavigationState.Branch("home", "Home", TestNavigationState.Stack("home-stack")),
            TestNavigationState.Branch("catalog", "Catalog", catalogStack));
        var window = TestNavigationState.Window(
            "main",
            branchHost,
            new[] { TestNavigationState.Modal("store-modal", TestNavigationState.Entry("store", modalRoute)) });
        var state = TestNavigationState.State("main", window);

        Assert.Equal(window, NavigationStateAssert.ActiveWindow(state, "main"));
        var selectedBranchHost = NavigationStateAssert.SelectedBranchHost(state, "catalog");
        var selectedStack = NavigationStateAssert.SelectedBranch<StackNode>(selectedBranchHost, "catalog");
        NavigationStateAssert.StackRouteTypes(
            selectedStack,
            typeof(TestRoutes.CatalogRoute),
            typeof(TestRoutes.ProductDetailRoute));
        Assert.Equal(productRoute, NavigationStateAssert.StackTop<TestRoutes.ProductDetailRoute>(selectedStack));
        Assert.Equal(modalRoute, NavigationStateAssert.ModalRoute<TestRoutes.StoreRoute>(window));
        Assert.Throws<NavigationAssertionException>(() => NavigationStateAssert.SelectedBranchHost(state, "home"));
    }

    [Fact]
    public void RecordingNavigationDiagnosticObserverCapturesFiltersAndClearsEvents()
    {
        var observer = new RecordingNavigationDiagnosticObserver();
        var diagnostics = new NavigationDiagnostics();
        diagnostics.AddObserver(observer);

        diagnostics.Write(NavigationDiagnosticEventKind.RouteMatched, "operation", "matched");
        diagnostics.Write(NavigationDiagnosticEventKind.PlanningCompleted, "operation", "planned");

        Assert.True(observer.Contains(NavigationDiagnosticEventKind.RouteMatched));
        Assert.Single(observer.EventsOfKind(NavigationDiagnosticEventKind.RouteMatched));
        Assert.Equal(NavigationDiagnosticEventKind.RouteMatched, observer.Single(NavigationDiagnosticEventKind.RouteMatched).Kind);
        Assert.Throws<NavigationAssertionException>(() => observer.Single(NavigationDiagnosticEventKind.NavigationFailed));

        observer.Clear();
        Assert.Empty(observer.Events);
    }
}

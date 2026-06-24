using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
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
        var tabs = TestNavigationState.Tabs(
            "tabs",
            "catalog",
            "home",
            TestNavigationState.Branch("home", "Home", TestNavigationState.Stack("home-stack")),
            TestNavigationState.Branch("catalog", "Catalog", catalogStack));
        var window = TestNavigationState.Window(
            "main",
            tabs,
            new[] { TestNavigationState.Modal("store-modal", TestNavigationState.Entry("store", modalRoute)) });
        var state = TestNavigationState.State("main", window);

        Assert.Equal(window, NavigationStateAssert.ActiveWindow(state, "main"));
        var selectedTabs = NavigationStateAssert.SelectedTabs(state, "catalog");
        var selectedStack = NavigationStateAssert.SelectedBranch<StackNode>(selectedTabs, "catalog");
        NavigationStateAssert.StackRouteTypes(
            selectedStack,
            typeof(TestRoutes.CatalogRoute),
            typeof(TestRoutes.ProductDetailRoute));
        Assert.Equal(productRoute, NavigationStateAssert.StackTop<TestRoutes.ProductDetailRoute>(selectedStack));
        Assert.Equal(modalRoute, NavigationStateAssert.ModalRoute<TestRoutes.StoreRoute>(window));
        Assert.Throws<NavigationAssertionException>(() => NavigationStateAssert.SelectedTabs(state, "home"));
    }

    [Fact]
    public void RecordingNavigationObserverCapturesFiltersAndClearsEvents()
    {
        var observer = new RecordingNavigationObserver();
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

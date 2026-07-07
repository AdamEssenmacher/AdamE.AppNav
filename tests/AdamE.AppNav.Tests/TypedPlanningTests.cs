using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class TypedPlanningTests
{
    [Fact]
    public async Task TypedPlannerReceivesTypedRouteWithoutAppSideCast()
    {
        var routePlanner = new StoreRoutePlanner();
        var planner = new TypedAppNavigationPlanner(new IAppRoutePlannerRegistration[]
        {
            new AppRoutePlannerRegistration<TestRoutes.StoreRoute>(routePlanner)
        });

        var plan = await planner.CreatePlanAsync(
            new NavigationPlanningContext(
                RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test),
                new TestRoutes.StoreRoute("northwind"),
                NavigationState.Empty,
                "operation"));

        Assert.Equal("northwind", routePlanner.LastRoute!.StoreId);
        var stack = Assert.IsType<StackNode>(plan.TargetState.ActiveWindow!.Root);
        Assert.IsType<TestRoutes.StoreRoute>(stack.Top!.Route);
    }

    [Fact]
    public async Task TypedPlannerReceivesFullPlanningContext()
    {
        var routePlanner = new ContextCapturingStoreRoutePlanner();
        var planner = new TypedAppNavigationPlanner(new IAppRoutePlannerRegistration[]
        {
            new AppRoutePlannerRegistration<TestRoutes.StoreRoute>(routePlanner)
        });
        var route = new TestRoutes.StoreRoute("northwind");
        var request = RouterNavigationRequest.FromRoute(
            route,
            NavigationRequestSource.Test,
            "secondary",
            new Dictionary<string, object?> { ["origin"] = "typed-planner" },
            RouterNavigationDisposition.ReplaceCurrent);
        var currentState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "catalog-stack",
                    TestNavigationState.Entry("catalog", new TestRoutes.CatalogRoute("northwind")))));

        await planner.CreatePlanAsync(new NavigationPlanningContext(request, route, currentState, "operation"));

        var context = routePlanner.LastContext;
        Assert.NotNull(context);
        Assert.Same(request, context!.Request);
        Assert.Same(route, context.Route);
        Assert.Same(currentState, context.CurrentState);
        Assert.Equal("operation", context.OperationId);
    }

    [Fact]
    public async Task TypedPlannerWorksThroughRouterNavigator()
    {
        var planner = new TypedAppNavigationPlanner(new IAppRoutePlannerRegistration[]
        {
            new AppRoutePlannerRegistration<TestRoutes.StoreRoute>(new StoreRoutePlanner())
        });
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(TestRoutes.CreateTable(), planner, presenter);

        var result = await navigator.NavigateAsync(
            new Uri("https://example.com/stores/northwind"),
            NavigationRequestSource.Test);

        var stack = Assert.IsType<StackNode>(result.State.ActiveWindow!.Root);
        Assert.IsType<TestRoutes.StoreRoute>(stack.Top!.Route);
        Assert.Equal(1, presenter.ApplyCount);
    }

    [Fact]
    public async Task MissingTypedPlannerFailsWithRouteType()
    {
        var planner = new TypedAppNavigationPlanner(Array.Empty<IAppRoutePlannerRegistration>());

        var exception = await Assert.ThrowsAsync<RoutePlannerNotFoundException>(() =>
            planner.CreatePlanAsync(
                new NavigationPlanningContext(
                    RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test),
                    new TestRoutes.StoreRoute("northwind"),
                    NavigationState.Empty,
                    "operation")).AsTask());

        Assert.Equal(typeof(TestRoutes.StoreRoute), exception.RouteType);
    }

    [Fact]
    public void DuplicateTypedPlannerRegistrationsFailClearly()
    {
        var first = new AppRoutePlannerRegistration<TestRoutes.StoreRoute>(new StoreRoutePlanner());
        var second = new AppRoutePlannerRegistration<TestRoutes.StoreRoute>(new StoreRoutePlanner());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new TypedAppNavigationPlanner(new IAppRoutePlannerRegistration[] { first, second }));

        Assert.Contains(typeof(TestRoutes.StoreRoute).FullName!, exception.Message, StringComparison.Ordinal);
    }

    private sealed class StoreRoutePlanner : IAppRoutePlanner<TestRoutes.StoreRoute>
    {
        public TestRoutes.StoreRoute? LastRoute { get; private set; }

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext<TestRoutes.StoreRoute> context,
            CancellationToken cancellationToken = default)
        {
            LastRoute = context.Route;
            var state = TestNavigationState.State(
                "main",
                TestNavigationState.Window(
                    "main",
                    TestNavigationState.Stack(
                        "store-stack",
                        TestNavigationState.Entry("store-home", context.Route))));

            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }

    private sealed class ContextCapturingStoreRoutePlanner : IAppRoutePlanner<TestRoutes.StoreRoute>
    {
        public NavigationPlanningContext<TestRoutes.StoreRoute>? LastContext { get; private set; }

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext<TestRoutes.StoreRoute> context,
            CancellationToken cancellationToken = default)
        {
            LastContext = context;
            var state = TestNavigationState.State(
                "main",
                TestNavigationState.Window(
                    "main",
                    TestNavigationState.Stack(
                        "store-stack",
                        TestNavigationState.Entry("store-home", context.Route))));

            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }
}

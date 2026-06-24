using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Tests;

public sealed class BackNavigatorTests
{
    [Fact]
    public void BackDismissesModalBeforeHostContent()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                Stack("root", new TestRoute("home")),
                new[]
                {
                    new ModalNode("modal", Entry("modal", new TestRoute("modal")))
                })
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        Assert.NotNull(plan);
        Assert.Equal(NavigationPlanKind.Back, plan.Kind);
        Assert.Empty(plan.TargetState.ActiveWindow!.Modals);
    }

    [Fact]
    public void BackPopsTopModalContentBeforeDismissingModal()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                Stack("root", new TestRoute("home")),
                new[]
                {
                    new ModalNode(
                        "modal",
                        Entry("modal-shell", new TestRoute("modal-shell")),
                        Stack(
                            "modal-stack",
                            new TestRoute("catalog"),
                            new TestRoute("product"),
                            new TestRoute("review")))
                })
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        var modal = Assert.Single(plan!.TargetState.ActiveWindow!.Modals);
        var stack = Assert.IsType<StackNode>(modal.Content);
        Assert.Equal(new[] { "catalog", "product" }, stack.Entries.Select(entry => ((TestRoute)entry.Route).Value));
    }

    [Fact]
    public void BackPopsSelectedStack()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode("main", Stack("catalog", new TestRoute("catalog"), new TestRoute("product")))
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        var stack = Assert.IsType<StackNode>(plan!.TargetState.ActiveWindow!.Root);
        Assert.Single(stack.Entries);
        Assert.Equal("catalog", ((TestRoute)stack.Top!.Route).Value);
    }

    [Fact]
    public void BackReturnsToDefaultTabWhenSelectedStackCannotPop()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new TabsNode(
                    "tabs",
                    new[]
                    {
                        new NavigationBranch("overview", "Overview", Stack("overview-stack", new TestRoute("overview"))),
                        new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", new TestRoute("catalog")))
                    },
                    SelectedTabId: "catalog",
                    DefaultTabId: "overview"))
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        var tabs = Assert.IsType<TabsNode>(plan!.TargetState.ActiveWindow!.Root);
        Assert.Equal("overview", tabs.SelectedTabId);
    }

    private static StackNode Stack(string id, params TestRoute[] routes)
    {
        return new StackNode(id, routes.Select((route, index) => Entry($"{id}-{index}", route)).ToArray());
    }

    private static RouteEntry Entry(string id, AppRoute route)
    {
        return new RouteEntry(id, route);
    }

    private sealed record TestRoute(string Value) : AppRoute;
}

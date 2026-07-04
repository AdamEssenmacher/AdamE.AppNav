using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.Testing;

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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BackWithNullOrBlankWindowIdUsesActiveWindow(string? windowId)
    {
        var state = new NavigationState(new[]
        {
            new WindowNode("main", Stack("main-stack", new TestRoute("main"), new TestRoute("main-detail"))),
            new WindowNode("secondary", Stack("secondary-stack", new TestRoute("secondary"), new TestRoute("secondary-detail")))
        }, "secondary");

        var plan = new DefaultBackNavigator().CreateBackPlan(state, windowId);

        var mainStack = Assert.IsType<StackNode>(plan!.TargetState.FindWindow("main")!.Root);
        var secondaryStack = Assert.IsType<StackNode>(plan.TargetState.FindWindow("secondary")!.Root);
        Assert.Equal(2, mainStack.Entries.Count);
        Assert.Single(secondaryStack.Entries);
        Assert.Equal("secondary", ((TestRoute)secondaryStack.Top!.Route).Value);
    }

    [Fact]
    public void BackWithStaleActiveWindowIdFallsBackToFirstWindow()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode("main", Stack("main-stack", new TestRoute("main"), new TestRoute("main-detail")))
        }, "missing");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        var stack = Assert.IsType<StackNode>(plan!.TargetState.FindWindow("main")!.Root);
        Assert.Single(stack.Entries);
        Assert.Equal("main", ((TestRoute)stack.Top!.Route).Value);
    }

    [Fact]
    public void BackWithExplicitMissingWindowIdReturnsNull()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode("main", Stack("main-stack", new TestRoute("main"), new TestRoute("main-detail")))
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state, "missing");

        Assert.Null(plan);
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

    [Fact]
    public void BackPopsSelectedTabStackBeforeReturningToDefaultTab()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new TabsNode(
                    "tabs",
                    new[]
                    {
                        Branch("overview", Stack("overview-stack", new TestRoute("overview"))),
                        Branch("catalog", Stack("catalog-stack", new TestRoute("catalog"), new TestRoute("product")))
                    },
                    SelectedTabId: "catalog",
                    DefaultTabId: "overview"))
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        var tabs = Assert.IsType<TabsNode>(plan!.TargetState.ActiveWindow!.Root);
        var catalogStack = Assert.IsType<StackNode>(tabs.SelectedBranch!.Content);
        Assert.Equal("catalog", tabs.SelectedTabId);
        Assert.Single(catalogStack.Entries);
        Assert.Equal("catalog", ((TestRoute)catalogStack.Top!.Route).Value);
    }

    [Fact]
    public void BackDoesNotReturnToDefaultTabWhenTabFallbackIsDisabled()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new TabsNode(
                    "tabs",
                    new[]
                    {
                        Branch("overview", Stack("overview-stack", new TestRoute("overview"))),
                        Branch("catalog", Stack("catalog-stack", new TestRoute("catalog")))
                    },
                    SelectedTabId: "catalog",
                    DefaultTabId: "overview"))
        }, "main");

        var plan = new DefaultBackNavigator(new BackNavigationOptions
        {
            ReturnToDefaultTabBeforeLeaving = false
        }).CreateBackPlan(state);

        Assert.Null(plan);
    }

    [Fact]
    public void BackDoesNotReturnToMissingDefaultTab()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new TabsNode(
                    "tabs",
                    new[]
                    {
                        Branch("catalog", Stack("catalog-stack", new TestRoute("catalog")))
                    },
                    SelectedTabId: "catalog",
                    DefaultTabId: "missing"))
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        Assert.Null(plan);
    }

    [Fact]
    public void BackReturnsToDefaultFlyoutItemWhenSelectedStackCannotPop()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new FlyoutNode(
                    "flyout",
                    new[]
                    {
                        Branch("overview", Stack("overview-stack", new TestRoute("overview"))),
                        Branch("catalog", Stack("catalog-stack", new TestRoute("catalog")))
                    },
                    SelectedItemId: "catalog",
                    DefaultItemId: "overview"))
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        var flyout = Assert.IsType<FlyoutNode>(plan!.TargetState.ActiveWindow!.Root);
        Assert.Equal("overview", flyout.SelectedItemId);
    }

    [Fact]
    public void BackDoesNotReturnToDefaultFlyoutItemWhenFlyoutFallbackIsDisabled()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new FlyoutNode(
                    "flyout",
                    new[]
                    {
                        Branch("overview", Stack("overview-stack", new TestRoute("overview"))),
                        Branch("catalog", Stack("catalog-stack", new TestRoute("catalog")))
                    },
                    SelectedItemId: "catalog",
                    DefaultItemId: "overview"))
        }, "main");

        var plan = new DefaultBackNavigator(new BackNavigationOptions
        {
            ReturnToDefaultFlyoutItemBeforeLeaving = false
        }).CreateBackPlan(state);

        Assert.Null(plan);
    }

    [Fact]
    public void BackDoesNotReturnToMissingDefaultFlyoutItem()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new FlyoutNode(
                    "flyout",
                    new[]
                    {
                        Branch("catalog", Stack("catalog-stack", new TestRoute("catalog")))
                    },
                    SelectedItemId: "catalog",
                    DefaultItemId: "missing"))
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        Assert.Null(plan);
    }

    [Fact]
    public void BackDismissesTopModalWhenModalContentCannotGoBack()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                Stack("root", new TestRoute("home"), new TestRoute("detail")),
                new[]
                {
                    new ModalNode(
                        "modal",
                        Entry("modal-shell", new TestRoute("modal-shell")),
                        Stack("modal-stack", new TestRoute("modal-root")))
                })
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        Assert.Empty(plan!.TargetState.ActiveWindow!.Modals);
        var rootStack = Assert.IsType<StackNode>(plan.TargetState.ActiveWindow.Root);
        Assert.Equal(2, rootStack.Entries.Count);
    }

    [Fact]
    public void BackPopsNestedModalContentStack()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                Stack("root", new TestRoute("home")),
                new[]
                {
                    new ModalNode(
                        "outer",
                        Entry("outer-shell", new TestRoute("outer-shell")),
                        new ModalNode(
                            "inner",
                            Entry("inner-shell", new TestRoute("inner-shell")),
                            Stack("inner-stack", new TestRoute("inner-root"), new TestRoute("inner-detail"))))
                })
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        var outer = Assert.Single(plan!.TargetState.ActiveWindow!.Modals);
        var inner = Assert.IsType<ModalNode>(outer.Content);
        var innerStack = Assert.IsType<StackNode>(inner.Content);
        Assert.Single(innerStack.Entries);
        Assert.Equal("inner-root", ((TestRoute)innerStack.Top!.Route).Value);
    }

    [Fact]
    public async Task RouterBackAsyncUsesSameOperationIdForDefaultBackEvaluation()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var initialState = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("catalog-stack", new[]
                {
                    new RouteEntry("catalog", new TestRoutes.CatalogRoute("northwind")),
                    new RouteEntry("product", new TestRoutes.ProductDetailRoute("northwind", 123))
                }))
        }, "main");
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Diagnostics = diagnostics,
                InitialState = initialState
            });

        await navigator.BackAsync();

        var started = Assert.Single(events, diagnosticEvent => diagnosticEvent.Kind == NavigationDiagnosticEventKind.BackStarted);
        var evaluated = Assert.Single(events, diagnosticEvent => diagnosticEvent.Kind == NavigationDiagnosticEventKind.BackEvaluated);
        var completed = Assert.Single(events, diagnosticEvent => diagnosticEvent.Kind == NavigationDiagnosticEventKind.BackCompleted);
        Assert.Equal(started.OperationId, evaluated.OperationId);
        Assert.Equal(started.OperationId, completed.OperationId);
    }

    private static NavigationBranch Branch(string id, NavigationNode content)
    {
        return new NavigationBranch(id, id, content);
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

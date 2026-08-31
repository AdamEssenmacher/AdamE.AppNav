using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class BackNavigatorTests
{
    [Fact]
    public void DefaultBackNavigationResultRepresentsUnhandledBack()
    {
        BackNavigationResult result = default;

        Assert.Equal(BackNavigationResult.Unhandled, result);
        Assert.Equal(BackNavigationStatus.Unhandled, result.Status);
        Assert.Null(result.NavigationResult);
    }

    [Fact]
    public void HandledBackNavigationResultRequiresNavigationResult()
    {
        Assert.Throws<ArgumentNullException>(() => BackNavigationResult.CompletedBy(null!));
    }

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
    public void BackWithStaleActiveWindowIdIsNotHandled()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode("main", Stack("main-stack", new TestRoute("main"), new TestRoute("main-detail")))
        }, "missing");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        Assert.Null(plan);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BackNavigationContextWithNullOrBlankWindowIdResolvesActiveWindow(string? windowId)
    {
        var state = new NavigationState(new[]
        {
            new WindowNode("main", Stack("main-stack", new TestRoute("main"))),
            new WindowNode("secondary", Stack("secondary-stack", new TestRoute("secondary")))
        }, "secondary");

        var context = new BackNavigationContext(state, windowId, "operation");

        Assert.True(context.UsesActiveWindow);
        Assert.Same(state, context.State);
        Assert.Equal(windowId, context.RequestedWindowId);
        Assert.Equal("operation", context.OperationId);
        Assert.Equal("secondary", context.Window?.Id);
        Assert.Equal("secondary", context.ResolvedWindowId);
    }

    [Fact]
    public void BackNavigationContextWithStaleActiveWindowIdDoesNotResolveWindow()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode("main", Stack("main-stack", new TestRoute("main")))
        }, "missing");

        var context = new BackNavigationContext(state, null, "operation");

        Assert.True(context.UsesActiveWindow);
        Assert.Null(context.Window);
        Assert.Null(context.ResolvedWindowId);
    }

    [Fact]
    public void BackNavigationContextWithExplicitMissingWindowIdDoesNotResolveWindow()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode("main", Stack("main-stack", new TestRoute("main")))
        }, "main");

        var context = new BackNavigationContext(state, "missing", "operation");

        Assert.False(context.UsesActiveWindow);
        Assert.Equal("missing", context.RequestedWindowId);
        Assert.Null(context.Window);
        Assert.Null(context.ResolvedWindowId);
    }

    [Fact]
    public void BackWithExplicitSecondaryWindowPreservesActiveWindow()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode("main", Stack("main-stack", new TestRoute("main"))),
            new WindowNode("secondary", Stack("secondary-stack", new TestRoute("secondary"), new TestRoute("secondary-detail")))
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state, "secondary");

        Assert.Equal("main", plan!.TargetState.ActiveWindowId);
        var secondaryStack = Assert.IsType<StackNode>(plan.TargetState.FindWindow("secondary")!.Root);
        Assert.Single(secondaryStack.Entries);
        Assert.Equal("secondary", ((TestRoute)secondaryStack.Top!.Route).Value);
    }

    [Fact]
    public void BackWithExplicitSecondaryWindowPreservesImplicitActiveWindow()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode("main", Stack("main-stack", new TestRoute("main"))),
            new WindowNode("secondary", Stack("secondary-stack", new TestRoute("secondary"), new TestRoute("secondary-detail")))
        });

        var plan = new DefaultBackNavigator().CreateBackPlan(state, "secondary");

        Assert.Null(plan!.TargetState.ActiveWindowId);
        Assert.Equal("main", plan.TargetState.ActiveWindow!.Id);
        var secondaryStack = Assert.IsType<StackNode>(plan.TargetState.FindWindow("secondary")!.Root);
        Assert.Single(secondaryStack.Entries);
        Assert.Equal("secondary", ((TestRoute)secondaryStack.Top!.Route).Value);
    }

    [Fact]
    public void BackReturnsToDefaultBranchWhenSelectedStackCannotPop()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new BranchHostNode(
                    "branchHost",
                    new[]
                    {
                        new NavigationBranch("overview", "Overview", Stack("overview-stack", new TestRoute("overview"))),
                        new NavigationBranch("catalog", "Catalog", Stack("catalog-stack", new TestRoute("catalog")))
                    },
                    SelectedBranchId: "catalog",
                    DefaultBranchId: "overview"))
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        var branchHost = Assert.IsType<BranchHostNode>(plan!.TargetState.ActiveWindow!.Root);
        Assert.Equal("overview", branchHost.SelectedBranchId);
    }

    [Fact]
    public void BackPopsSelectedBranchStackBeforeReturningToDefaultBranch()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new BranchHostNode(
                    "branchHost",
                    new[]
                    {
                        Branch("overview", Stack("overview-stack", new TestRoute("overview"))),
                        Branch("catalog", Stack("catalog-stack", new TestRoute("catalog"), new TestRoute("product")))
                    },
                    SelectedBranchId: "catalog",
                    DefaultBranchId: "overview"))
        }, "main");

        var plan = new DefaultBackNavigator().CreateBackPlan(state);

        var branchHost = Assert.IsType<BranchHostNode>(plan!.TargetState.ActiveWindow!.Root);
        var catalogStack = Assert.IsType<StackNode>(branchHost.SelectedBranch!.Content);
        Assert.Equal("catalog", branchHost.SelectedBranchId);
        Assert.Single(catalogStack.Entries);
        Assert.Equal("catalog", ((TestRoute)catalogStack.Top!.Route).Value);
    }

    [Fact]
    public void BackDoesNotReturnToDefaultBranchWhenBranchFallbackIsDisabled()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new BranchHostNode(
                    "branchHost",
                    new[]
                    {
                        Branch("overview", Stack("overview-stack", new TestRoute("overview"))),
                        Branch("catalog", Stack("catalog-stack", new TestRoute("catalog")))
                    },
                    SelectedBranchId: "catalog",
                    DefaultBranchId: "overview"))
        }, "main");

        var plan = new DefaultBackNavigator(new BackNavigationOptions
        {
            ReturnToDefaultBranchBeforeLeaving = false
        }).CreateBackPlan(state);

        Assert.Null(plan);
    }

    [Fact]
    public void BackDoesNotReturnToAlreadySelectedDefaultBranch()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new BranchHostNode(
                    "branchHost",
                    new[]
                    {
                        Branch("catalog", Stack("catalog-stack", new TestRoute("catalog")))
                    },
                    SelectedBranchId: "catalog",
                    DefaultBranchId: "catalog"))
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

    [Fact]
    public async Task RouterBackAsyncPassesOperationContextToCustomBackNavigator()
    {
        var diagnostics = new NavigationDiagnostics(
            options: new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full });
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var backNavigator = new RecordingBackNavigator();
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
                BackNavigator = backNavigator,
                Diagnostics = diagnostics,
                InitialState = initialState
            });

        await navigator.BackAsync();

        var started = Assert.Single(events, diagnosticEvent => diagnosticEvent.Kind == NavigationDiagnosticEventKind.BackStarted);
        var unhandled = Assert.Single(events, diagnosticEvent => diagnosticEvent.Kind == NavigationDiagnosticEventKind.BackUnhandled);
        Assert.NotNull(backNavigator.Context);
        Assert.Equal(started.OperationId, backNavigator.Context.OperationId);
        Assert.True(backNavigator.Context.UsesActiveWindow);
        Assert.Equal("main", backNavigator.Context.ResolvedWindowId);
        Assert.Equal("main", backNavigator.Context.Window?.Id);
        Assert.Equal("main", started.Data[NavigationDiagnosticDataKeys.WindowId]);
        Assert.Equal("main", unhandled.Data[NavigationDiagnosticDataKeys.WindowId]);
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

    private sealed class RecordingBackNavigator : IBackNavigator
    {
        public BackNavigationContext? Context { get; private set; }

        public NavigationPlan? CreateBackPlan(BackNavigationContext context)
        {
            Context = context;
            return null;
        }
    }
}

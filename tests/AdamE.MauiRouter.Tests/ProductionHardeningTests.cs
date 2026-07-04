using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.Testing;

namespace AdamE.MauiRouter.Tests;

public sealed class ProductionHardeningTests
{
    [Fact]
    public async Task NavigationOperationsAreSerialized()
    {
        var presenter = new BlockingPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RouteEchoPlanner(),
            presenter);

        var first = navigator.NavigateAsync(new TestRoutes.StoreRoute("first"), NavigationRequestSource.Test).AsTask();
        await presenter.FirstPresentationStarted.Task;

        var second = navigator.NavigateAsync(new TestRoutes.StoreRoute("second"), NavigationRequestSource.Test).AsTask();
        await Task.Delay(100);

        Assert.False(second.IsCompleted);

        presenter.ReleaseFirstPresentation.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(new[] { "first", "second" }, presenter.PresentedStoreIds);
        Assert.Equal("second", ((TestRoutes.StoreRoute)navigator.History.Current!.Route).StoreId);
    }

    [Fact]
    public async Task PresentationFailureDoesNotMutateLogicalStateOrHistory()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);

        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RouteEchoPlanner(),
            new ThrowingPresenter(),
            new RouterNavigatorOptions { Diagnostics = diagnostics });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            navigator.NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test).AsTask());

        Assert.Null(navigator.CurrentState.ActiveWindow);
        Assert.Empty(navigator.History.Entries);
        Assert.Contains(NavigationDiagnosticEventKind.PresentationFailed, events);
        Assert.Contains(NavigationDiagnosticEventKind.NavigationFailed, events);
    }

    [Fact]
    public async Task HistoryIsBoundedByNavigatorOptions()
    {
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RouteEchoPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { MaxHistoryEntries = 2 });

        await navigator.NavigateAsync(new TestRoutes.StoreRoute("one"), NavigationRequestSource.Test);
        await navigator.NavigateAsync(new TestRoutes.StoreRoute("two"), NavigationRequestSource.Test);
        await navigator.NavigateAsync(new TestRoutes.StoreRoute("three"), NavigationRequestSource.Test);

        Assert.Equal(2, navigator.History.Entries.Count);
        Assert.Equal("two", ((TestRoutes.StoreRoute)navigator.History.Entries[0].Route).StoreId);
        Assert.Equal("three", ((TestRoutes.StoreRoute)navigator.History.Current!.Route).StoreId);
    }

    [Fact]
    public async Task BackAsyncAppliesBackPlanAndReportsUnhandledExit()
    {
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
            new RouteEchoPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { InitialState = initialState });

        var handled = await navigator.BackAsync();
        var unhandled = await navigator.BackAsync();

        Assert.True(handled.Handled);
        Assert.False(unhandled.Handled);
        var stack = Assert.IsType<StackNode>(navigator.CurrentState.ActiveWindow!.Root);
        Assert.Single(stack.Entries);
    }

    [Fact]
    public async Task BackAsyncReportsPresentedModalRouteAfterPoppingModalContent()
    {
        var initialState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "root-stack",
                    TestNavigationState.Entry("home", new TestRoutes.StoreRoute("northwind"))),
                new[]
                {
                    TestNavigationState.Modal(
                        "cart-modal",
                        TestNavigationState.Entry("cart-modal-shell", new TestRoutes.StoreRoute("cart-shell")),
                        TestNavigationState.Stack(
                            "cart-stack",
                            TestNavigationState.Entry("cart", new TestRoutes.StoreRoute("northwind-cart")),
                            TestNavigationState.Entry("catalog", new TestRoutes.CatalogRoute("northwind")),
                            TestNavigationState.Entry("product", new TestRoutes.ProductDetailRoute("northwind", 123))))
                }));
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RouteEchoPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { InitialState = initialState });

        var handled = await navigator.BackAsync();

        Assert.True(handled.Handled);
        var route = Assert.IsType<TestRoutes.CatalogRoute>(handled.HandledNavigationResult!.Route);
        Assert.Equal("northwind", route.StoreId);
        Assert.Equal(route, navigator.History.Current!.Route);
        var modal = Assert.Single(navigator.CurrentState.ActiveWindow!.Modals);
        var stack = Assert.IsType<StackNode>(modal.Content);
        Assert.Equal(route, stack.Top!.Route);
    }

    [Fact]
    public async Task BackAsyncReportsRemainingModalRouteAfterDismissingTopModal()
    {
        var lowerModalRoute = new TestRoutes.CatalogRoute("northwind");
        var initialState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "root-stack",
                    TestNavigationState.Entry("home", new TestRoutes.StoreRoute("home"))),
                new[]
                {
                    TestNavigationState.Modal(
                        "catalog-modal",
                        TestNavigationState.Entry("catalog-modal-shell", new TestRoutes.StoreRoute("catalog-shell")),
                        TestNavigationState.Stack(
                            "catalog-stack",
                            TestNavigationState.Entry("catalog", lowerModalRoute))),
                    TestNavigationState.Modal(
                        "confirmation-modal",
                        TestNavigationState.Entry("confirmation-modal-shell", new TestRoutes.StoreRoute("confirm-shell")))
                }));
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RouteEchoPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { InitialState = initialState });

        var handled = await navigator.BackAsync();

        Assert.True(handled.Handled);
        Assert.Equal(lowerModalRoute, handled.HandledNavigationResult!.Route);
        Assert.Equal(lowerModalRoute, navigator.History.Current!.Route);
        Assert.Single(navigator.CurrentState.ActiveWindow!.Modals);
        var remainingModal = Assert.Single(navigator.CurrentState.ActiveWindow.Modals);
        var remainingStack = Assert.IsType<StackNode>(remainingModal.Content);
        Assert.Equal(lowerModalRoute, remainingStack.Top!.Route);
    }

    [Fact]
    public async Task BackAsyncForSecondaryWindowUsesThatWindowRouteWithoutChangingActiveWindow()
    {
        var secondaryRoute = new TestRoutes.CatalogRoute("northwind");
        var initialState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "main-stack",
                    TestNavigationState.Entry("home", new TestRoutes.StoreRoute("home")))),
            TestNavigationState.Window(
                "secondary",
                TestNavigationState.Stack(
                    "secondary-stack",
                    TestNavigationState.Entry("catalog", secondaryRoute),
                    TestNavigationState.Entry("product", new TestRoutes.ProductDetailRoute("northwind", 123)))));
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new RouteEchoPlanner(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { InitialState = initialState });

        var handled = await navigator.BackAsync("secondary");

        Assert.True(handled.Handled);
        Assert.Equal("main", navigator.CurrentState.ActiveWindowId);
        Assert.Equal(secondaryRoute, handled.HandledNavigationResult!.Route);
        Assert.Equal(secondaryRoute, navigator.History.Current!.Route);
        var secondaryWindow = navigator.CurrentState.FindWindow("secondary");
        var secondaryStack = Assert.IsType<StackNode>(secondaryWindow!.Root);
        Assert.Single(secondaryStack.Entries);
        Assert.Equal(secondaryRoute, secondaryStack.Top!.Route);
    }

    [Fact]
    public void DiagnosticsObserversAreIsolated()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();

        diagnostics.EventWritten += (_, _) => throw new InvalidOperationException("observer failed");
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);

        diagnostics.Write(NavigationDiagnosticEventKind.RouteMatched, "operation", "matched");

        Assert.Contains(NavigationDiagnosticEventKind.RouteMatched, events);
        Assert.Contains(NavigationDiagnosticEventKind.DiagnosticObserverFailed, events);
    }

    private sealed class RouteEchoPlanner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            var state = new NavigationState(new[]
            {
                new WindowNode("main", new StackNode("stack", new[] { new RouteEntry("route", context.Route) }))
            }, "main");

            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }

    private sealed class CountingPlanner : IAppNavigationPlanner
    {
        public int ApplyCount { get; private set; }

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            var state = new NavigationState(new[]
            {
                new WindowNode("main", new StackNode("stack", new[] { new RouteEntry("route", context.Route) }))
            }, "main");

            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }

    private sealed class PassThroughRequestPolicy : INavigationRequestPolicy
    {
        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(request);
        }
    }

    private sealed class BlockingPresenter : INavigationPresenter
    {
        private int _presentationCount;

        public TaskCompletionSource FirstPresentationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstPresentation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> PresentedStoreIds { get; } = new();

        public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
        {
            add { }
            remove { }
        }

        public async ValueTask ApplyAsync(
            NavigationPlan plan,
            NavigationPresentationContext context,
            CancellationToken cancellationToken = default)
        {
            var route = Assert.IsType<TestRoutes.StoreRoute>(context.Route);
            PresentedStoreIds.Add(route.StoreId);

            if (Interlocked.Increment(ref _presentationCount) == 1)
            {
                FirstPresentationStarted.SetResult();
                await ReleaseFirstPresentation.Task;
            }
        }
    }

    private sealed class ThrowingPresenter : INavigationPresenter
    {
        public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
        {
            add { }
            remove { }
        }

        public ValueTask ApplyAsync(
            NavigationPlan plan,
            NavigationPresentationContext context,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Presentation failed.");
        }
    }
}

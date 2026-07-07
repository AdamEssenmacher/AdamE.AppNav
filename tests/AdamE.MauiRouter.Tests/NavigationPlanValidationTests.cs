using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Tests;

public sealed class NavigationPlanValidationTests
{
    [Fact]
    public void RouterRejectsInvalidInitialState()
    {
        var invalidState = StateWithMissingSelectedBranch();
        var presenter = new RecordingNavigationPresenter();

        Assert.Throws<InvalidOperationException>(() => new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter,
            new RouterNavigatorOptions { InitialState = invalidState }));

        Assert.Equal(0, presenter.ApplyCount);
    }

    [Fact]
    public async Task RouterRejectsInvalidPlannerPlanBeforePresentation()
    {
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new FixedPlanPlanner(StateWithMissingSelectedBranch()),
            presenter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator
            .NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test)
            .AsTask());

        Assert.Equal(0, presenter.ApplyCount);
    }

    [Fact]
    public async Task RouterRejectsInvalidBackPlanBeforePresentation()
    {
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter,
            new RouterNavigatorOptions
            {
                BackNavigator = new FixedBackNavigator(StateWithMissingSelectedBranch()),
                InitialState = ValidStackState()
            });

        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator.BackAsync().AsTask());

        Assert.Equal(0, presenter.ApplyCount);
    }

    [Fact]
    public async Task RouterRejectsInvalidReconciliationBeforePresentation()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter,
            new RouterNavigatorOptions { Diagnostics = diagnostics });

        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator
            .ReconcileAsync(new NavigationReconciliation(
                StateWithMissingSelectedBranch(),
                NavigationReconciliationSource.NativeBackGesture,
                new TestRoutes.StoreRoute("northwind"),
                "invalid test reconciliation"))
            .AsTask());

        Assert.Equal(0, presenter.ApplyCount);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationStarted, events);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationFailed, events);
    }

    [Fact]
    public async Task RouterAllowsCustomNavigationNodesForCustomPresenters()
    {
        var customState = new NavigationState(new[]
        {
            new WindowNode("main", new CustomNode("custom-root"))
        }, "main");
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new FixedPlanPlanner(customState),
            presenter);

        var result = await navigator.NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test);

        Assert.True(result.Presented);
        Assert.Equal(1, presenter.ApplyCount);
        Assert.Same(customState, navigator.CurrentState);
        Assert.IsType<CustomNode>(navigator.CurrentState.ActiveWindow!.Root);
    }

    private static NavigationState ValidStackState()
    {
        return new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new StackNode("main-stack", new[]
                {
                    new RouteEntry("home", new TestRoutes.StoreRoute("northwind"))
                }))
        }, "main");
    }

    private static NavigationState StateWithMissingSelectedBranch()
    {
        var branchHost = new BranchHostNode(
            "branchHost",
            new[]
            {
                new NavigationBranch(
                    "home",
                    "Home",
                    new StackNode("home-stack", new[]
                    {
                        new RouteEntry("home", new TestRoutes.StoreRoute("northwind"))
                    }))
            },
            "home",
            "home");

        return new NavigationState(new[]
        {
            new WindowNode("main", branchHost with { SelectedBranchId = "missing" })
        }, "main");
    }

    private sealed class FixedPlanPlanner(NavigationState state) : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }

    private sealed class FixedBackNavigator(NavigationState state) : IBackNavigator
    {
        public NavigationPlan? CreateBackPlan(BackNavigationContext context)
        {
            return new NavigationPlan(state, NavigationPlanKind.Back, "invalid test back plan");
        }
    }

    private sealed record CustomNode(string NodeId) : NavigationNode(NodeId);
}

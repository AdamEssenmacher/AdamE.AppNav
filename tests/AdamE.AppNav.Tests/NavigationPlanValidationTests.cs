using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class NavigationPlanValidationTests
{
    [Fact]
    public void RouterRejectsInvalidInitialState()
    {
        var invalidState = StateWithMissingSelectedBranch();
        var presenter = new RecordingNavigationPresenter();

        Assert.Throws<AppNavigationConfigurationException>(() => new RouterNavigator(
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

        await Assert.ThrowsAsync<AppNavigationConfigurationException>(() => navigator
            .NavigateAsync(RouterNavigationRequest.FromRoute(
                new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test))
            .AsTask());

        Assert.Equal(0, presenter.ApplyCount);
    }

    [Fact]
    public async Task RouterRejectsMissingActiveWindowBeforePresentation()
    {
        var invalidState = new NavigationState(
            [new WindowNode("main", new StackNode("main-stack", []))],
            "missing");
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            new FixedPlanPlanner(invalidState),
            presenter);

        await Assert.ThrowsAsync<AppNavigationConfigurationException>(() => navigator
            .NavigateAsync(RouterNavigationRequest.FromRoute(
                new TestRoutes.StoreRoute("northwind"),
                NavigationRequestSource.Test))
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

        await Assert.ThrowsAsync<AppNavigationConfigurationException>(() => navigator.BackAsync().AsTask());

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

        await Assert.ThrowsAsync<AppNavigationConfigurationException>(() => navigator
            .ReconcileAsync(new NavigationReconciliation(
                StateWithMissingSelectedBranch(),
                NavigationReconciliationSource.HostBack,
                new TestRoutes.StoreRoute("northwind"),
                "invalid test reconciliation"))
            .AsTask());

        Assert.Equal(0, presenter.ApplyCount);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationStarted, events);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationFailed, events);
    }

    [Fact]
    public void NavigationNodeCannotBeDerivedOutsideTheCoreAssembly()
    {
        var constructor = Assert.Single(
            typeof(NavigationNode).GetConstructors(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic),
            candidate => candidate.GetParameters() is [{ ParameterType: var parameterType }] &&
                         parameterType == typeof(string));
        Assert.True(constructor.IsFamilyAndAssembly);

        var nodeTypeSeal = Assert.Single(
            typeof(NavigationNode).GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic),
            candidate => candidate.Name == "SealNodeType");
        Assert.True(nodeTypeSeal.IsAbstract);
        Assert.True(nodeTypeSeal.IsFamilyAndAssembly);
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
}

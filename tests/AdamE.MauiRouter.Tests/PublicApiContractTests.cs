using System.Reflection;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Maui;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Tests;

public sealed class PublicApiContractTests
{
    [Fact]
    public void CoreAssemblyPublicApiMatchesAllowlist()
    {
        AssertPublicApi(
            typeof(AppRoute).Assembly,
            [
                "AdamE.MauiRouter.AppRoute",
                "AdamE.MauiRouter.AppRouteRequest",
                "AdamE.MauiRouter.Back.BackNavigationContext",
                "AdamE.MauiRouter.Back.BackNavigationOptions",
                "AdamE.MauiRouter.Back.DefaultBackNavigator",
                "AdamE.MauiRouter.Back.IBackNavigator",
                "AdamE.MauiRouter.Diagnostics.INavigationDiagnosticObserver",
                "AdamE.MauiRouter.Diagnostics.NavigationActivitySources",
                "AdamE.MauiRouter.Diagnostics.NavigationDiagnosticDataKeys",
                "AdamE.MauiRouter.Diagnostics.NavigationDiagnosticEvent",
                "AdamE.MauiRouter.Diagnostics.NavigationDiagnosticEventKind",
                "AdamE.MauiRouter.Diagnostics.NavigationDiagnosticPhase",
                "AdamE.MauiRouter.Diagnostics.NavigationDiagnostics",
                "AdamE.MauiRouter.History.NavigationHistory",
                "AdamE.MauiRouter.History.NavigationHistoryEntry",
                "AdamE.MauiRouter.Navigation.BackNavigationResult",
                "AdamE.MauiRouter.Navigation.IRouterNavigator",
                "AdamE.MauiRouter.Navigation.NavigationCommitKind",
                "AdamE.MauiRouter.Navigation.NavigationCommittedEventArgs",
                "AdamE.MauiRouter.Navigation.NavigationFallbackContext",
                "AdamE.MauiRouter.Navigation.NavigationResult",
                "AdamE.MauiRouter.Navigation.RouteRedirectLoopException",
                "AdamE.MauiRouter.Navigation.RouterNavigatorFactory",
                "AdamE.MauiRouter.Navigation.RouterNavigatorFactoryOptions",
                "AdamE.MauiRouter.Persistence.BranchHostNodeSnapshot",
                "AdamE.MauiRouter.Persistence.FadeNavigationTransitionSnapshot",
                "AdamE.MauiRouter.Persistence.INavigationRestorePolicy",
                "AdamE.MauiRouter.Persistence.INavigationSnapshotMetadataSerializer",
                "AdamE.MauiRouter.Persistence.INavigationStateStore",
                "AdamE.MauiRouter.Persistence.ModalNodeSnapshot",
                "AdamE.MauiRouter.Persistence.NavigationBranchSnapshot",
                "AdamE.MauiRouter.Persistence.NavigationHistoryEntrySnapshot",
                "AdamE.MauiRouter.Persistence.NavigationHistorySnapshot",
                "AdamE.MauiRouter.Persistence.NavigationMetadataValueSnapshot",
                "AdamE.MauiRouter.Persistence.NavigationNodeSnapshot",
                "AdamE.MauiRouter.Persistence.NavigationPersistenceOptions",
                "AdamE.MauiRouter.Persistence.NavigationRequestProvenanceSnapshot",
                "AdamE.MauiRouter.Persistence.NavigationRequestSnapshot",
                "AdamE.MauiRouter.Persistence.NavigationRestoreContext",
                "AdamE.MauiRouter.Persistence.NavigationRestoreDecision",
                "AdamE.MauiRouter.Persistence.NavigationRestoreOptions",
                "AdamE.MauiRouter.Persistence.NavigationRestoreResult",
                "AdamE.MauiRouter.Persistence.NavigationSnapshot",
                "AdamE.MauiRouter.Persistence.NavigationStateSnapshot",
                "AdamE.MauiRouter.Persistence.NavigationTransitionSnapshot",
                "AdamE.MauiRouter.Persistence.NoNavigationTransitionSnapshot",
                "AdamE.MauiRouter.Persistence.PlatformDefaultNavigationTransitionSnapshot",
                "AdamE.MauiRouter.Persistence.RouteEntrySnapshot",
                "AdamE.MauiRouter.Persistence.SharedElementNavigationTransitionSnapshot",
                "AdamE.MauiRouter.Persistence.SharedElementPairSnapshot",
                "AdamE.MauiRouter.Persistence.SlideNavigationTransitionSnapshot",
                "AdamE.MauiRouter.Persistence.StackNodeSnapshot",
                "AdamE.MauiRouter.Persistence.WindowNodeSnapshot",
                "AdamE.MauiRouter.Planning.BranchHostNavigationModelBuilder`1",
                "AdamE.MauiRouter.Planning.BranchHostNavigationModel`1",
                "AdamE.MauiRouter.Planning.BranchHostRouteRecipeBuilder`2",
                "AdamE.MauiRouter.Planning.ContextualStackEligibility",
                "AdamE.MauiRouter.Planning.ContextualStackMutationKind",
                "AdamE.MauiRouter.Planning.ContextualStackPushBehavior",
                "AdamE.MauiRouter.Planning.StackNavigationModelBuilder`1",
                "AdamE.MauiRouter.Planning.StackNavigationModel`1",
                "AdamE.MauiRouter.Planning.StackRouteRecipeBuilder`2",
                "AdamE.MauiRouter.Planning.StackRouteStep`1",
                "AdamE.MauiRouter.Plans.FadeNavigationTransition",
                "AdamE.MauiRouter.Plans.NavigationPlan",
                "AdamE.MauiRouter.Plans.NavigationPlanKind",
                "AdamE.MauiRouter.Plans.NavigationSlideDirection",
                "AdamE.MauiRouter.Plans.NavigationTransition",
                "AdamE.MauiRouter.Plans.NoNavigationTransition",
                "AdamE.MauiRouter.Plans.PlatformDefaultNavigationTransition",
                "AdamE.MauiRouter.Plans.SharedElementNavigationTransition",
                "AdamE.MauiRouter.Plans.SharedElementPair",
                "AdamE.MauiRouter.Plans.SlideNavigationTransition",
                "AdamE.MauiRouter.Policies.AccessGateNavigationPolicy",
                "AdamE.MauiRouter.Policies.IAppNavigationPlanner",
                "AdamE.MauiRouter.Policies.INavigationAccessEvaluator",
                "AdamE.MauiRouter.Policies.INavigationPlanPolicy",
                "AdamE.MauiRouter.Policies.INavigationRequestPolicy",
                "AdamE.MauiRouter.Policies.NavigationAccessDecision",
                "AdamE.MauiRouter.Policies.NavigationPlanPolicyContext",
                "AdamE.MauiRouter.Policies.NavigationPlanningContext",
                "AdamE.MauiRouter.Policies.NavigationRequestPolicyContext",
                "AdamE.MauiRouter.Policies.RoutePlannerNotFoundException",
                "AdamE.MauiRouter.Presentation.INavigationPresenter",
                "AdamE.MauiRouter.Presentation.NavigationPresentationContext",
                "AdamE.MauiRouter.Presentation.NavigationReconciliation",
                "AdamE.MauiRouter.Presentation.NavigationReconciliationRequestedEventArgs",
                "AdamE.MauiRouter.Presentation.NavigationReconciliationSource",
                "AdamE.MauiRouter.Requests.DeferredNavigationReplayResult",
                "AdamE.MauiRouter.Requests.DeferredNavigationRequestPersistenceOptions",
                "AdamE.MauiRouter.Requests.DeferredNavigationRequestReplayer",
                "AdamE.MauiRouter.Requests.DeferredNavigationRequestSerializer",
                "AdamE.MauiRouter.Requests.DeferredNavigationRequestStoreSnapshot",
                "AdamE.MauiRouter.Requests.IDeferredNavigationRequestReplayer",
                "AdamE.MauiRouter.Requests.IDeferredNavigationRequestStore",
                "AdamE.MauiRouter.Requests.InMemoryDeferredNavigationRequestStore",
                "AdamE.MauiRouter.Requests.NavigationRequestProvenance",
                "AdamE.MauiRouter.Requests.NavigationRequestSource",
                "AdamE.MauiRouter.Requests.RouterNavigationDisposition",
                "AdamE.MauiRouter.Requests.RouterNavigationRequest",
                "AdamE.MauiRouter.RouteStateLifetime",
                "AdamE.MauiRouter.RouteStateRegistry",
                "AdamE.MauiRouter.RouteStateRegistryBuilder",
                "AdamE.MauiRouter.Routing.ConventionRouteBuilder`1",
                "AdamE.MauiRouter.Routing.IRouteTableModule",
                "AdamE.MauiRouter.Routing.RouteDefinition",
                "AdamE.MauiRouter.Routing.RouteDiagnostic",
                "AdamE.MauiRouter.Routing.RouteFormatBuilder`1",
                "AdamE.MauiRouter.Routing.RouteMatchContext",
                "AdamE.MauiRouter.Routing.RouteMatchResult",
                "AdamE.MauiRouter.Routing.RouteMetadataKey`1",
                "AdamE.MauiRouter.Routing.RouteNotMatchedException",
                "AdamE.MauiRouter.Routing.RouteTable",
                "AdamE.MauiRouter.Routing.RouteTableBuilder",
                "AdamE.MauiRouter.Routing.RouteTemplate",
                "AdamE.MauiRouter.State.BranchHostNode",
                "AdamE.MauiRouter.State.ModalNode",
                "AdamE.MauiRouter.State.NavigationBranch",
                "AdamE.MauiRouter.State.NavigationNode",
                "AdamE.MauiRouter.State.NavigationState",
                "AdamE.MauiRouter.State.RouteEntry",
                "AdamE.MauiRouter.State.StackNode",
                "AdamE.MauiRouter.State.WindowNode"
            ]);
    }

    [Fact]
    public void PublicResultTypesUseValueSemantics()
    {
        Assert.True(typeof(BackNavigationResult).IsValueType);
        Assert.True(typeof(NavigationRestoreDecision).IsValueType);
        Assert.True(typeof(DeferredNavigationReplayResult).IsValueType);
    }

    [Fact]
    public void MauiAssemblyPublicApiMatchesAllowlist()
    {
        AssertPublicApi(
            typeof(MauiRoutePageRegistry).Assembly,
            [
                "AdamE.MauiRouter.Maui.AppLinks.IMauiExternalNavigationDispatcher",
                "AdamE.MauiRouter.Maui.AppLinks.MauiAppLinkProvenanceProviders",
                "AdamE.MauiRouter.Maui.AppLinks.MauiRouterAppLinkBuilderExtensions",
                "AdamE.MauiRouter.Maui.AppLinks.MauiRouterAppLinkOptions",
                "AdamE.MauiRouter.Maui.DependencyInjection.MauiRouterServiceCollectionExtensions",
                "AdamE.MauiRouter.Maui.IMauiPresentationState",
                "AdamE.MauiRouter.Maui.IMauiRoutePageLifecycleHook",
                "AdamE.MauiRouter.Maui.IMauiRoutePageModule",
                "AdamE.MauiRouter.Maui.IMauiRouterStartupService",
                "AdamE.MauiRouter.Maui.MauiBranchHostPresentation",
                "AdamE.MauiRouter.Maui.MauiRoutePageRegistry",
                "AdamE.MauiRouter.Maui.MauiRoutePageReuseKind",
                "AdamE.MauiRouter.Maui.MauiRoutePageUpdateContext",
                "AdamE.MauiRouter.Maui.MauiRouterStartupOptions",
                "AdamE.MauiRouter.Maui.MauiRouterStartupOutcome",
                "AdamE.MauiRouter.Maui.MauiRouterStartupResult",
                "AdamE.MauiRouter.Maui.MauiRouterTransition",
                "AdamE.MauiRouter.Maui.Requests.MauiFileDeferredNavigationRequestStoreOptions"
            ]);
    }

    [Fact]
    public void RouterNavigatorInterfaceIncludesRuntimeNavigationSurface()
    {
        var methods = typeof(IRouterNavigator).GetMethods();
        var navigationCommitted = Assert.Single(
            typeof(IRouterNavigator).GetEvents(),
            static eventInfo => eventInfo.Name == nameof(IRouterNavigator.NavigationCommitted));
        Assert.Equal(typeof(EventHandler<NavigationCommittedEventArgs>), navigationCommitted.EventHandlerType);

        Assert.Contains(methods, static method =>
            method.Name == nameof(IRouterNavigator.ReconcileAsync) &&
            method.GetParameters() is
            [
                { ParameterType: var firstParameterType },
                { ParameterType: var secondParameterType }
            ] &&
            firstParameterType == typeof(Presentation.NavigationReconciliation) &&
            secondParameterType == typeof(CancellationToken));

        Assert.Contains(methods, static method =>
            method.Name == nameof(IRouterNavigator.RestoreAsync) &&
            method.GetParameters() is
            [
                { ParameterType: var firstParameterType },
                { ParameterType: var secondParameterType },
                { ParameterType: var thirdParameterType }
            ] &&
            firstParameterType == typeof(Persistence.NavigationSnapshot) &&
            secondParameterType == typeof(Persistence.NavigationRestoreOptions) &&
            thirdParameterType == typeof(CancellationToken));

        Assert.Contains(methods, static method =>
            method.Name == nameof(IRouterNavigator.RestoreFromStoreAsync) &&
            method.GetParameters() is
            [
                { ParameterType: var firstParameterType },
                { ParameterType: var secondParameterType }
            ] &&
            firstParameterType == typeof(Persistence.NavigationRestoreOptions) &&
            secondParameterType == typeof(CancellationToken));

        Assert.Equal(
            3,
            methods.Count(static method =>
                method.Name == nameof(IRouterNavigator.NavigateAsync) &&
                method.GetParameters().FirstOrDefault()?.ParameterType == typeof(Uri)));
    }

    [Fact]
    public void NavigationDiagnosticEventKindPreservesExistingNumericValues()
    {
        Assert.Equal(54, (int)NavigationDiagnosticEventKind.AppLinkReceived);
        Assert.Equal(55, (int)NavigationDiagnosticEventKind.AppLinkBuffered);
        Assert.Equal(56, (int)NavigationDiagnosticEventKind.AppLinkDispatched);
        Assert.Equal(57, (int)NavigationDiagnosticEventKind.AppLinkFailed);
        Assert.Equal(58, (int)NavigationDiagnosticEventKind.DiagnosticObserverFailed);
        Assert.Equal(59, (int)NavigationDiagnosticEventKind.NavigationCommittedHandlerFailed);
        Assert.Equal(60, (int)NavigationDiagnosticEventKind.PresentationVerificationFailed);
    }

    [Fact]
    public void NavigationTransitionIsClosedToExternalDerivation()
    {
        var constructors = typeof(NavigationTransition).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var marker = typeof(NavigationTransition).GetProperty(
            "BuiltInTransitionMarker",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotEmpty(constructors);
        Assert.NotNull(marker);
        Assert.DoesNotContain(constructors, static constructor =>
            constructor.IsPublic);
        Assert.True(marker!.GetMethod is { IsAbstract: true, IsFamilyAndAssembly: true });
    }

    private static void AssertPublicApi(Assembly assembly, IReadOnlyList<string> expected)
    {
        var actual = assembly.GetExportedTypes()
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }
}

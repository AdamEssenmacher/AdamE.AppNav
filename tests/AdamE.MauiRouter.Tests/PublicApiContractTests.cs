using System.Reflection;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Maui;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.Testing;

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
                "AdamE.MauiRouter.Navigation.NavigationFallbackContext",
                "AdamE.MauiRouter.Navigation.NavigationResult",
                "AdamE.MauiRouter.Navigation.RouteRedirectLoopException",
                "AdamE.MauiRouter.Navigation.RouterNavigatorFactory",
                "AdamE.MauiRouter.Navigation.RouterNavigatorFactoryOptions",
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
                "AdamE.MauiRouter.Plans.NavigationPlan",
                "AdamE.MauiRouter.Plans.NavigationPlanKind",
                "AdamE.MauiRouter.Policies.AccessGateNavigationPolicy",
                "AdamE.MauiRouter.Policies.IAppNavigationPlanner",
                "AdamE.MauiRouter.Policies.INavigationAccessEvaluator",
                "AdamE.MauiRouter.Policies.INavigationRequestPolicy",
                "AdamE.MauiRouter.Policies.NavigationAccessDecision",
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
                "AdamE.MauiRouter.Requests.INavigationRequestMetadataSerializer",
                "AdamE.MauiRouter.Requests.InMemoryDeferredNavigationRequestStore",
                "AdamE.MauiRouter.Requests.NavigationMetadataValueSnapshot",
                "AdamE.MauiRouter.Requests.NavigationRequestProvenance",
                "AdamE.MauiRouter.Requests.NavigationRequestProvenanceSnapshot",
                "AdamE.MauiRouter.Requests.NavigationRequestSnapshot",
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
        Assert.True(typeof(DeferredNavigationReplayResult).IsValueType);
    }

    [Fact]
    public void NavigationHistoryConstructionSurfaceIsNotPublic()
    {
        Assert.Empty(typeof(NavigationHistory).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        Assert.DoesNotContain(
            typeof(NavigationHistory).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            static method => method.Name == "Push");

        Assert.DoesNotContain(
            typeof(RouterNavigatorFactoryOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => property.Name == "InitialHistory");

        Assert.DoesNotContain(
            typeof(RouterTestNavigatorOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => property.Name == "InitialHistory");
    }

    [Fact]
    public void NavigationHistoryEntrySurfaceContainsOnlyCommittedRouteState()
    {
        string[] propertyNames = typeof(NavigationHistoryEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => property.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            nameof(NavigationHistoryEntry.Request),
            nameof(NavigationHistoryEntry.Route),
            nameof(NavigationHistoryEntry.State)
        ], propertyNames);
    }

    [Fact]
    public void NavigationRequestPolicyUsesContextOnlyApplySurface()
    {
        var method = Assert.Single(typeof(INavigationRequestPolicy).GetMethods(BindingFlags.Public | BindingFlags.Instance));
        ParameterInfo[] parameters = method.GetParameters();

        Assert.Equal(nameof(INavigationRequestPolicy.ApplyAsync), method.Name);
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(NavigationRequestPolicyContext), parameters[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[1].ParameterType);
        Assert.True(parameters[1].HasDefaultValue);
    }

    [Fact]
    public void RouteDiagnosticDataSurfaceIsConstructorOnly()
    {
        PropertyInfo property = Assert.Single(
            typeof(RouteDiagnostic).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static property => property.Name == nameof(RouteDiagnostic.Data));

        Assert.Null(property.SetMethod);
    }

    [Fact]
    public void NavigationReconciliationSourceExposesOnlyCommittedSources()
    {
        Assert.Equal(
            [
                nameof(NavigationReconciliationSource.NativeBackGesture),
                nameof(NavigationReconciliationSource.ModalDismissed),
                nameof(NavigationReconciliationSource.TabChanged)
            ],
            Enum.GetNames<NavigationReconciliationSource>());
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
                "AdamE.MauiRouter.Maui.MauiRoutePageRegistry",
                "AdamE.MauiRouter.Maui.MauiRoutePageReuseKind",
                "AdamE.MauiRouter.Maui.MauiRoutePageUpdateContext",
                "AdamE.MauiRouter.Maui.MauiRouterStartupOptions",
                "AdamE.MauiRouter.Maui.MauiRouterStartupOutcome",
                "AdamE.MauiRouter.Maui.MauiRouterStartupResult",
                "AdamE.MauiRouter.Maui.Requests.MauiFileDeferredNavigationRequestStoreOptions"
            ]);
    }

    [Fact]
    public void RouterNavigatorInterfaceIncludesRuntimeNavigationSurface()
    {
        var methods = typeof(IRouterNavigator).GetMethods();
        Assert.Empty(typeof(IRouterNavigator).GetEvents());

        Assert.Contains(methods, static method =>
            method.Name == nameof(IRouterNavigator.ReconcileAsync) &&
            method.GetParameters() is
            [
                { ParameterType: var firstParameterType },
                { ParameterType: var secondParameterType }
            ] &&
            firstParameterType == typeof(Presentation.NavigationReconciliation) &&
            secondParameterType == typeof(CancellationToken));

        Assert.DoesNotContain(methods, static method => method.Name == "RestoreAsync");
        Assert.DoesNotContain(methods, static method => method.Name == "RestoreFromStoreAsync");

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
        Assert.Equal(60, (int)NavigationDiagnosticEventKind.PresentationVerificationFailed);
        Assert.Equal(40, (int)NavigationDiagnosticEventKind.StartupDeferredRequestPending);
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

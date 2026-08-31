using System.Reflection;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.History;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Tests;

public sealed class PublicApiContractTests
{
    [Fact]
    public void CoreAssemblyPublicApiMatchesAllowlist()
    {
        AssertPublicApi(
            typeof(AppRoute).Assembly,
            [
                "AdamE.AppNav.AppRoute",
                "AdamE.AppNav.AppRouteRequest",
                "AdamE.AppNav.Back.BackNavigationContext",
                "AdamE.AppNav.Back.BackNavigationOptions",
                "AdamE.AppNav.Back.BackNavigationPolicyContext",
                "AdamE.AppNav.Back.BackNavigationPolicyDecision",
                "AdamE.AppNav.Back.BackNavigationRequest",
                "AdamE.AppNav.Back.BackNavigationSource",
                "AdamE.AppNav.Back.DefaultBackNavigator",
                "AdamE.AppNav.Back.IBackNavigationPolicy",
                "AdamE.AppNav.Back.IBackNavigator",
                "AdamE.AppNav.Diagnostics.INavigationDiagnosticObserver",
                "AdamE.AppNav.Diagnostics.INavigationDiagnosticRedactor",
                "AdamE.AppNav.Diagnostics.NavigationActivitySources",
                "AdamE.AppNav.Diagnostics.NavigationDiagnosticDataKeys",
                "AdamE.AppNav.Diagnostics.NavigationDiagnosticDataMode",
                "AdamE.AppNav.Diagnostics.NavigationDiagnosticEvent",
                "AdamE.AppNav.Diagnostics.NavigationDiagnosticEventKind",
                "AdamE.AppNav.Diagnostics.NavigationDiagnosticPhase",
                "AdamE.AppNav.Diagnostics.NavigationDiagnostics",
                "AdamE.AppNav.Diagnostics.NavigationDiagnosticsOptions",
                "AdamE.AppNav.History.NavigationHistory",
                "AdamE.AppNav.History.NavigationHistoryEntry",
                "AdamE.AppNav.Navigation.AppNavigationConfigurationException",
                "AdamE.AppNav.Navigation.BackNavigationResult",
                "AdamE.AppNav.Navigation.BackNavigationStatus",
                "AdamE.AppNav.Navigation.IRouterNavigator",
                "AdamE.AppNav.Navigation.NavigationFallbackContext",
                "AdamE.AppNav.Navigation.NavigationResult",
                "AdamE.AppNav.Navigation.RouteRedirectLoopException",
                "AdamE.AppNav.Navigation.RouterNavigatorExtensions",
                "AdamE.AppNav.Navigation.RouterNavigatorFactory",
                "AdamE.AppNav.Navigation.RouterNavigatorFactoryOptions",
                "AdamE.AppNav.Planning.BranchHostNavigationModelBuilder`1",
                "AdamE.AppNav.Planning.BranchHostNavigationModel`1",
                "AdamE.AppNav.Planning.BranchHostRouteRecipeBuilder`2",
                "AdamE.AppNav.Planning.ContextualStackEligibility",
                "AdamE.AppNav.Planning.ContextualStackMutationKind",
                "AdamE.AppNav.Planning.ContextualStackPushBehavior",
                "AdamE.AppNav.Planning.INavigationModel`1",
                "AdamE.AppNav.Planning.NavigationModelPlanner`1",
                "AdamE.AppNav.Planning.StackNavigationModelBuilder`1",
                "AdamE.AppNav.Planning.StackNavigationModel`1",
                "AdamE.AppNav.Planning.StackRouteRecipeBuilder`2",
                "AdamE.AppNav.Planning.StackRouteStep`1",
                "AdamE.AppNav.Plans.NavigationPlan",
                "AdamE.AppNav.Plans.NavigationPlanKind",
                "AdamE.AppNav.Policies.AccessGateNavigationPolicy",
                "AdamE.AppNav.Policies.IAppNavigationPlanner",
                "AdamE.AppNav.Policies.INavigationAccessEvaluator",
                "AdamE.AppNav.Policies.INavigationRequestPolicy",
                "AdamE.AppNav.Policies.INavigationRequestTransformer",
                "AdamE.AppNav.Policies.NavigationAccessDecision",
                "AdamE.AppNav.Policies.NavigationPlanningContext",
                "AdamE.AppNav.Policies.NavigationRequestPolicyContext",
                "AdamE.AppNav.Policies.NavigationRequestTransformContext",
                "AdamE.AppNav.Policies.RoutePlannerNotFoundException",
                "AdamE.AppNav.Presentation.INavigationPresenter",
                "AdamE.AppNav.Presentation.NavigationPresentationContext",
                "AdamE.AppNav.Presentation.NavigationReconciliation",
                "AdamE.AppNav.Presentation.NavigationReconciliationRequestedEventArgs",
                "AdamE.AppNav.Presentation.NavigationReconciliationSource",
                "AdamE.AppNav.Requests.DeferredNavigationReplayResult",
                "AdamE.AppNav.Requests.DeferredNavigationRequestPersistenceOptions",
                "AdamE.AppNav.Requests.DeferredNavigationRequestReplayer",
                "AdamE.AppNav.Requests.DeferredNavigationRequestSerializer",
                "AdamE.AppNav.Requests.DeferredNavigationRequestStoreSnapshot",
                "AdamE.AppNav.Requests.IDeferredNavigationRequestLease",
                "AdamE.AppNav.Requests.IDeferredNavigationRequestReplayer",
                "AdamE.AppNav.Requests.IDeferredNavigationRequestStore",
                "AdamE.AppNav.Requests.INavigationRequestMetadataSerializer",
                "AdamE.AppNav.Requests.InMemoryDeferredNavigationRequestStore",
                "AdamE.AppNav.Requests.InvalidDeferredNavigationRequestDataException",
                "AdamE.AppNav.Requests.NavigationMetadataValueSnapshot",
                "AdamE.AppNav.Requests.NavigationRequestProvenance",
                "AdamE.AppNav.Requests.NavigationRequestProvenanceSnapshot",
                "AdamE.AppNav.Requests.NavigationRequestSnapshot",
                "AdamE.AppNav.Requests.NavigationRequestSource",
                "AdamE.AppNav.Requests.RouterNavigationDisposition",
                "AdamE.AppNav.Requests.RouterNavigationRequest",
                "AdamE.AppNav.Requests.UnsupportedDeferredNavigationRequestSchemaException",
                "AdamE.AppNav.RouteStateLifetime",
                "AdamE.AppNav.RouteStateRegistry",
                "AdamE.AppNav.RouteStateRegistryBuilder",
                "AdamE.AppNav.Routing.AppNavQueryAttribute",
                "AdamE.AppNav.Routing.AppNavQueryMetadataAttribute",
                "AdamE.AppNav.Routing.AppNavRouteAttribute",
                "AdamE.AppNav.Routing.ConventionRouteBuilder`1",
                "AdamE.AppNav.Routing.IRouteTableModule",
                "AdamE.AppNav.Routing.RouteDefinition",
                "AdamE.AppNav.Routing.RouteDiagnostic",
                "AdamE.AppNav.Routing.RouteFormatBuilder`1",
                "AdamE.AppNav.Routing.RouteMatchContext",
                "AdamE.AppNav.Routing.RouteMatchResult",
                "AdamE.AppNav.Routing.RouteMetadataKey`1",
                "AdamE.AppNav.Routing.RouteNotMatchedException",
                "AdamE.AppNav.Routing.RouteTable",
                "AdamE.AppNav.Routing.RouteTableBuilder",
                "AdamE.AppNav.Routing.RouteTemplate",
                "AdamE.AppNav.State.BranchHostNode",
                "AdamE.AppNav.State.ModalNode",
                "AdamE.AppNav.State.NavigationBranch",
                "AdamE.AppNav.State.NavigationNode",
                "AdamE.AppNav.State.NavigationState",
                "AdamE.AppNav.State.RouteEntry",
                "AdamE.AppNav.State.StackNode",
                "AdamE.AppNav.State.WindowNode"
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
                nameof(NavigationReconciliationSource.HostBack),
                nameof(NavigationReconciliationSource.ModalDismissed),
                nameof(NavigationReconciliationSource.BranchChanged)
            ],
            Enum.GetNames<NavigationReconciliationSource>());
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

        var navigate = Assert.Single(
            methods,
            static method => method.Name == nameof(IRouterNavigator.NavigateAsync));
        Assert.Equal(typeof(RouterNavigationRequest), navigate.GetParameters()[0].ParameterType);

        var typedExtensions = typeof(RouterNavigatorExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(static method => method.Name == nameof(IRouterNavigator.NavigateAsync))
            .ToArray();
        Assert.Equal(4, typedExtensions.Length);
        Assert.DoesNotContain(
            typedExtensions,
            static method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(Uri)));
    }

    [Fact]
    public void NavigationDiagnosticEventKindPreservesExistingNumericValues()
    {
        Assert.Equal(13, (int)NavigationDiagnosticEventKind.RequestTransformStarted);
        Assert.Equal(14, (int)NavigationDiagnosticEventKind.RequestTransformCompleted);
        Assert.Equal(15, (int)NavigationDiagnosticEventKind.RequestTransformFailed);
        Assert.Equal(54, (int)NavigationDiagnosticEventKind.AppLinkReceived);
        Assert.Equal(55, (int)NavigationDiagnosticEventKind.AppLinkBuffered);
        Assert.Equal(56, (int)NavigationDiagnosticEventKind.AppLinkDispatched);
        Assert.Equal(57, (int)NavigationDiagnosticEventKind.AppLinkFailed);
        Assert.Equal(58, (int)NavigationDiagnosticEventKind.DiagnosticObserverFailed);
        Assert.Equal(60, (int)NavigationDiagnosticEventKind.PresentationVerificationFailed);
        Assert.Equal(61, (int)NavigationDiagnosticEventKind.PresentationRollbackStarted);
        Assert.Equal(62, (int)NavigationDiagnosticEventKind.PresentationRollbackCompleted);
        Assert.Equal(63, (int)NavigationDiagnosticEventKind.PresentationRollbackFailed);
        Assert.Equal(64, (int)NavigationDiagnosticEventKind.PresentationPageReleaseFailed);
        Assert.Equal(65, (int)NavigationDiagnosticEventKind.DeferredRequestStoreQuarantined);
        Assert.Equal(72, (int)NavigationDiagnosticEventKind.DeferredRequestStoreReset);
        Assert.Equal(73, (int)NavigationDiagnosticEventKind.DeferredRequestStorePruned);
        Assert.Equal(74, (int)NavigationDiagnosticEventKind.DeferredRequestStoreOverflowed);
        Assert.Equal(40, (int)NavigationDiagnosticEventKind.StartupDeferredRequestPending);
        Assert.Equal(11, (int)NavigationDiagnosticPhase.Persistence);
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

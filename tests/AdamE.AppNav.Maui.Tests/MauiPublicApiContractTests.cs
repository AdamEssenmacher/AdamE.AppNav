using System.Reflection;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiPublicApiContractTests
{
    [Fact]
    public void MauiAssemblyPublicApiMatchesAllowlist()
    {
        AssertPublicApi(
            typeof(MauiRoutePageRegistry).Assembly,
            [
                "AdamE.AppNav.Maui.AppLinks.AppNavExternalNavigationBuilderExtensions",
                "AdamE.AppNav.Maui.AppLinks.IMauiExternalNavigationDispatcher",
                "AdamE.AppNav.Maui.AppLinks.MauiAppLinkProvenanceProviders",
                "AdamE.AppNav.Maui.AppLinks.MauiExternalNavigationFailureDisposition",
                "AdamE.AppNav.Maui.AppLinks.MauiExternalNavigationOptions",
                "AdamE.AppNav.Maui.AppNavNavigatorOptions",
                "AdamE.AppNav.Maui.AppNavStartupOptions",
                "AdamE.AppNav.Maui.AppNavStartupOutcome",
                "AdamE.AppNav.Maui.AppNavStartupResult",
                "AdamE.AppNav.Maui.DependencyInjection.AppNavServiceCollectionExtensions",
                "AdamE.AppNav.Maui.IAppNavStartupService",
                "AdamE.AppNav.Maui.IMauiHostBackDispatcher",
                "AdamE.AppNav.Maui.IMauiPresentationOperationPolicy",
                "AdamE.AppNav.Maui.IMauiPresentationState",
                "AdamE.AppNav.Maui.IMauiBranchHost",
                "AdamE.AppNav.Maui.IMauiBranchHostFactory",
                "AdamE.AppNav.Maui.IMauiBranchHostUpdate",
                "AdamE.AppNav.Maui.IMauiRoutePageLifecycleHook",
                "AdamE.AppNav.Maui.IMauiRoutePageModule",
                "AdamE.AppNav.Maui.IMauiRoutePresentationNavigator",
                "AdamE.AppNav.Maui.MauiHostBackResult",
                "AdamE.AppNav.Maui.MauiHostBackStatus",
                "AdamE.AppNav.Maui.MauiNavigationPresentationOptions",
                "AdamE.AppNav.Maui.MauiBranchHostBranch",
                "AdamE.AppNav.Maui.MauiBranchHostCreationContext",
                "AdamE.AppNav.Maui.MauiBranchHostPlacement",
                "AdamE.AppNav.Maui.MauiBranchHostSelectionChangedEventArgs",
                "AdamE.AppNav.Maui.MauiBranchHostUpdateContext",
                "AdamE.AppNav.Maui.MauiFlyoutBranchHostFactory",
                "AdamE.AppNav.Maui.MauiPresentationConsistencyException",
                "AdamE.AppNav.Maui.MauiPresentationMotion",
                "AdamE.AppNav.Maui.MauiPresentationOperationContext",
                "AdamE.AppNav.Maui.MauiPresentationOperationKind",
                "AdamE.AppNav.Maui.MauiPresentationOperationOptions",
                "AdamE.AppNav.Maui.MauiRoutePageAttribute",
                "AdamE.AppNav.Maui.MauiRoutePageRegistry",
                "AdamE.AppNav.Maui.MauiTabbedBranchHostFactory",
                "AdamE.AppNav.Maui.MauiRoutePageReuseKind",
                "AdamE.AppNav.Maui.MauiRoutePageUpdateContext",
                "AdamE.AppNav.Maui.MauiRoutePresentationPageOptions",
                "AdamE.AppNav.Maui.Requests.MauiFileDeferredNavigationRequestStoreOptions"
            ]);
    }

    [Fact]
    public void MauiRoutePageAttributeIncludesPageModelTypeApi()
    {
        PropertyInfo? property = typeof(MauiRoutePageAttribute).GetProperty(nameof(MauiRoutePageAttribute.PageModelType));

        Assert.NotNull(property);
        Assert.Equal(typeof(Type), property.PropertyType);
        Assert.True(property.CanRead);
        Assert.True(property.CanWrite);
    }

    private static void AssertPublicApi(Assembly assembly, IReadOnlyList<string> expected)
    {
        string[] actual = assembly.GetExportedTypes()
            .Where(type => type.FullName is not "ObjCRuntime.__Registrar__")
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }
}

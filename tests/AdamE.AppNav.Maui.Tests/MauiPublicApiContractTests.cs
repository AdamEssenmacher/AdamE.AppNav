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
                "AdamE.AppNav.Maui.IMauiPresentationOperationPolicy",
                "AdamE.AppNav.Maui.IMauiPresentationState",
                "AdamE.AppNav.Maui.IMauiRoutePageLifecycleHook",
                "AdamE.AppNav.Maui.IMauiRoutePageModule",
                "AdamE.AppNav.Maui.IMauiRoutePresentationNavigator",
                "AdamE.AppNav.Maui.MauiNavigationPresentationOptions",
                "AdamE.AppNav.Maui.MauiPresentationConsistencyException",
                "AdamE.AppNav.Maui.MauiPresentationMotion",
                "AdamE.AppNav.Maui.MauiPresentationOperationContext",
                "AdamE.AppNav.Maui.MauiPresentationOperationKind",
                "AdamE.AppNav.Maui.MauiPresentationOperationOptions",
                "AdamE.AppNav.Maui.MauiRoutePageAttribute",
                "AdamE.AppNav.Maui.MauiRoutePageRegistry",
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

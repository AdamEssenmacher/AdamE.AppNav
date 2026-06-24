using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Tests;

public sealed class RouterNavigationDispositionTests
{
    [Fact]
    public void FromRoute_DefaultsToAutoDisposition()
    {
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.InAppCommand);

        Assert.Equal(RouterNavigationDisposition.Auto, request.Disposition);
    }

    [Fact]
    public void FromRoute_PreservesExplicitDisposition()
    {
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.InAppCommand,
            disposition: RouterNavigationDisposition.ReplaceCurrent);

        Assert.Equal(RouterNavigationDisposition.ReplaceCurrent, request.Disposition);
    }

    [Fact]
    public void FromUri_DefaultsToAutoDisposition()
    {
        var request = RouterNavigationRequest.FromUri(
            new Uri("https://example.com/stores/northwind"),
            NavigationRequestSource.AppLink);

        Assert.Equal(RouterNavigationDisposition.Auto, request.Disposition);
    }

    [Fact]
    public void FromUri_PreservesExplicitDisposition()
    {
        var request = RouterNavigationRequest.FromUri(
            new Uri("https://example.com/stores/northwind"),
            NavigationRequestSource.AppLink,
            disposition: RouterNavigationDisposition.Canonical);

        Assert.Equal(RouterNavigationDisposition.Canonical, request.Disposition);
    }
}

using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AdamE.AppNav.Maui.Tests;

[Collection(ExternalNavigationBridgeTestCollection.Name)]
public sealed class MauiAppLinkRequestFactoryTests
{
    [Fact]
    public void ProviderConstantsMatchDocumentedProviderNames()
    {
        Assert.Equal("android-intent", MauiAppLinkProvenanceProviders.AndroidIntent);
        Assert.Equal("ios-open-url", MauiAppLinkProvenanceProviders.IosOpenUrl);
        Assert.Equal("ios-user-activity", MauiAppLinkProvenanceProviders.IosUserActivity);
        Assert.Equal("maui-app-link", MauiAppLinkProvenanceProviders.MauiAppLink);
    }

    [Fact]
    public void GenericUriFactoryPopulatesMauiAppLinkProvenance()
    {
        var incoming = new Uri("https://example.com/stores/northwind");

        var request = MauiAppLinkRequestFactory.FromUri(incoming);

        AssertProvenance(request, MauiAppLinkProvenanceProviders.MauiAppLink, incoming);
    }

    [Fact]
    public void AppLinkOptionsDispatchRequestsByDefault()
    {
        var request = RouterNavigationRequest.FromUri(
            new Uri("https://example.com/stores/northwind"),
            NavigationRequestSource.AppLink);
        using var provider = CreateAppLinkDispatcherProvider();
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();

        var dispatched = AppNavAppLinkBuilderExtensions.Dispatch(request, new AppNavAppLinkOptions());

        Assert.True(dispatched);
        Assert.True(dispatcher.HasPendingRequests);
    }

    [Fact]
    public void AppLinkOptionsCanSuppressRequestDispatch()
    {
        var request = RouterNavigationRequest.FromUri(
            new Uri("https://example.com/stores/northwind"),
            NavigationRequestSource.AppLink);
        var options = new AppNavAppLinkOptions
        {
            ShouldDispatch = static _ => false
        };
        using var provider = CreateAppLinkDispatcherProvider();
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();

        var dispatched = AppNavAppLinkBuilderExtensions.Dispatch(request, options);

        Assert.False(dispatched);
        Assert.False(dispatcher.HasPendingRequests);
    }

    [Theory]
    [InlineData(MauiAppLinkProvenanceProviders.AndroidIntent)]
    [InlineData(MauiAppLinkProvenanceProviders.IosOpenUrl)]
    [InlineData(MauiAppLinkProvenanceProviders.IosUserActivity)]
    public void ProviderUriStringFactoryPopulatesRequestedProvenance(string provider)
    {
        var incoming = new Uri("https://example.com/stores/northwind");

        var request = MauiAppLinkRequestFactory.TryFromUriString(incoming.ToString(), provider);

        AssertProvenance(request, provider, incoming);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uri")]
    public void ProviderUriStringFactoryRejectsInvalidUris(string? value)
    {
        var request = MauiAppLinkRequestFactory.TryFromUriString(
            value,
            MauiAppLinkProvenanceProviders.MauiAppLink);

        Assert.Null(request);
    }

    private static void AssertProvenance(
        RouterNavigationRequest? request,
        string provider,
        Uri incoming)
    {
        Assert.NotNull(request);
        Assert.Equal(NavigationRequestSource.AppLink, request.Source);
        Assert.Equal(incoming, request.Uri);
        Assert.NotNull(request.Provenance);
        var provenance = request.Provenance;
        Assert.Equal(provider, provenance.Provider);
        Assert.Equal(incoming, provenance.OriginalUri);
        Assert.Null(provenance.ReferrerUri);
        Assert.Null(provenance.CorrelationId);
        Assert.Null(provenance.IsColdStart);
        Assert.Empty(provenance.Attributes);
    }

    private static ServiceProvider CreateAppLinkDispatcherProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NavigationDiagnostics());
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());
        return services.BuildServiceProvider();
    }
}

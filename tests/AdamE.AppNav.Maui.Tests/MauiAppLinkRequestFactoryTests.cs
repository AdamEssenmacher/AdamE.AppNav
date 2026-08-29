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
    public void ExternalNavigationOptionsDispatchTrustedRequestsByDefault()
    {
        var request = RouterNavigationRequest.FromUri(
            new Uri("https://example.com/stores/northwind"),
            NavigationRequestSource.AppLink);
        var options = new MauiExternalNavigationOptions()
            .AllowOrigin(new Uri("https://example.com"));
        using var provider = CreateAppLinkDispatcherProvider(options);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();

        var dispatched = AppNavExternalNavigationBuilderExtensions.Dispatch(request, options);

        Assert.True(dispatched);
        Assert.True(dispatcher.HasPendingRequests);
    }

    [Fact]
    public void ExternalNavigationOptionsCanSuppressTrustedRequestDispatch()
    {
        var request = RouterNavigationRequest.FromUri(
            new Uri("https://example.com/stores/northwind"),
            NavigationRequestSource.AppLink);
        var options = new MauiExternalNavigationOptions
        {
            ShouldDispatch = static _ => false
        };
        options.AllowOrigin(new Uri("https://example.com"));
        using var provider = CreateAppLinkDispatcherProvider(options);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();

        var dispatched = AppNavExternalNavigationBuilderExtensions.Dispatch(request, options);

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

    [Fact]
    public void OversizedLifecycleValueIsRejectedBeforeUriParsingOrApplicationFiltering()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        int filterCalls = 0;
        var options = new MauiExternalNavigationOptions
        {
            MaximumUriLength = 32,
            ShouldDispatch = _ =>
            {
                filterCalls++;
                return true;
            }
        };
        options.AllowOrigin(new Uri("https://example.com"));
        using var provider = CreateAppLinkDispatcherProvider(options, diagnostics);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        MauiExternalNavigationIngress ingress = MauiAppLinkRequestFactory.ParseUriString(
            $"https://example.com/{new string('x', 64)}",
            MauiAppLinkProvenanceProviders.AndroidIntent,
            options.MaximumUriLength);

        bool dispatched = AppNavExternalNavigationBuilderExtensions.Dispatch(ingress, options);

        Assert.False(dispatched);
        Assert.Null(ingress.Request);
        Assert.Equal(0, filterCalls);
        Assert.False(dispatcher.HasPendingRequests);
        NavigationDiagnosticEvent rejected = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationRejected);
        Assert.Equal("UriTooLong", rejected.Data[NavigationDiagnosticDataKeys.ExternalNavigationReason]);
        Assert.DoesNotContain(NavigationDiagnosticDataKeys.Uri, rejected.Data.Keys);
        Assert.DoesNotContain(NavigationDiagnosticDataKeys.ProvenanceProvider, rejected.Data.Keys);
        Assert.DoesNotContain(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.AppLinkReceived);
    }

    [Fact]
    public void RelativeLifecycleValueProducesOnlyStructuralRejectionDiagnostics()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var options = new MauiExternalNavigationOptions();
        options.AllowOrigin(new Uri("https://example.com"));
        using var provider = CreateAppLinkDispatcherProvider(options, diagnostics);
        _ = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        MauiExternalNavigationIngress ingress = MauiAppLinkRequestFactory.ParseUriString(
            "stores/northwind",
            MauiAppLinkProvenanceProviders.IosOpenUrl,
            options.MaximumUriLength);

        Assert.False(AppNavExternalNavigationBuilderExtensions.Dispatch(ingress, options));

        NavigationDiagnosticEvent rejected = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationRejected);
        Assert.Equal("RelativeUri", rejected.Data[NavigationDiagnosticDataKeys.ExternalNavigationReason]);
        Assert.Equal(
            [NavigationDiagnosticDataKeys.ExternalNavigationReason, NavigationDiagnosticDataKeys.PendingRequestCount],
            rejected.Data.Keys.Order(StringComparer.Ordinal));
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

    private static ServiceProvider CreateAppLinkDispatcherProvider(
        MauiExternalNavigationOptions options,
        NavigationDiagnostics? diagnostics = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(diagnostics ?? new NavigationDiagnostics());
        services.AddSingleton(options);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());
        return services.BuildServiceProvider();
    }
}

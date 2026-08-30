using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
#if ANDROID
using Android.Content;
using Android.OS;
#endif

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

#if ANDROID
    [Fact]
    public void FreshAndroidIntentIsAcceptedWithProvenanceAndMarkedConsumed()
    {
        var incoming = new Uri("https://example.com/stores/northwind");
        using var intent = CreateAndroidIntent(incoming.ToString());
        var options = new MauiExternalNavigationOptions();

        MauiExternalNavigationIngress ingress = AndroidAppLinkRequestFactory.FromIntent(intent, options);

        AssertProvenance(ingress.Request, MauiAppLinkProvenanceProviders.AndroidIntent, incoming);
        Assert.Equal(MauiExternalNavigationRejectionReason.None, ingress.RejectionReason);
        Assert.True(intent.HasCategory(AndroidAppLinkRequestFactory.ConsumedCategoryName));
    }

    [Fact]
    public void SameAndroidIntentIsConsumedOnceWithoutDuplicateDiagnostics()
    {
        using var intent = CreateAndroidIntent("https://example.com/stores/northwind");
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var options = new MauiExternalNavigationOptions();
        options.AllowOrigin(new Uri("https://example.com"));
        using var provider = CreateAppLinkDispatcherProvider(options, diagnostics);
        _ = provider.GetRequiredService<MauiExternalNavigationDispatcher>();

        MauiExternalNavigationIngress first = AndroidAppLinkRequestFactory.FromIntent(intent, options);
        MauiExternalNavigationIngress replay = AndroidAppLinkRequestFactory.FromIntent(intent, options);

        Assert.True(AppNavExternalNavigationBuilderExtensions.Dispatch(first, options));
        Assert.False(AppNavExternalNavigationBuilderExtensions.Dispatch(replay, options));
        Assert.Null(replay.Request);
        Assert.Equal(MauiExternalNavigationRejectionReason.None, replay.RejectionReason);
        Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.AppLinkReceived);
    }

    [Fact]
    public void AndroidIntentLaunchedFromHistoryIsIgnoredOnFirstConsumption()
    {
        using var intent = CreateAndroidIntent("https://example.com/stores/northwind");
        intent.AddFlags(ActivityFlags.LaunchedFromHistory);
        var options = new MauiExternalNavigationOptions();

        MauiExternalNavigationIngress ingress = AndroidAppLinkRequestFactory.FromIntent(intent, options);

        Assert.Null(ingress.Request);
        Assert.Equal(MauiExternalNavigationRejectionReason.None, ingress.RejectionReason);
        Assert.False(intent.HasCategory(AndroidAppLinkRequestFactory.ConsumedCategoryName));
    }

    [Fact]
    public void DistinctAndroidIntentsWithSameUriAreSeparateBoundaryActivations()
    {
        const string incoming = "https://example.com/stores/northwind";
        using var firstIntent = CreateAndroidIntent(incoming);
        using var secondIntent = CreateAndroidIntent(incoming);
        var options = new MauiExternalNavigationOptions();

        MauiExternalNavigationIngress first = AndroidAppLinkRequestFactory.FromIntent(firstIntent, options);
        MauiExternalNavigationIngress second = AndroidAppLinkRequestFactory.FromIntent(secondIntent, options);

        Assert.NotNull(first.Request);
        Assert.NotNull(second.Request);
        Assert.NotSame(first.Request, second.Request);
        Assert.True(firstIntent.HasCategory(AndroidAppLinkRequestFactory.ConsumedCategoryName));
        Assert.True(secondIntent.HasCategory(AndroidAppLinkRequestFactory.ConsumedCategoryName));
    }

    [Theory]
    [InlineData("stores/northwind", 2048, "RelativeUri")]
    [InlineData("https://example.com/stores/northwind", 12, "UriTooLong")]
    public void RejectedAndroidIntentIsConsumedOnceThenIgnored(
        string incoming,
        int maximumUriLength,
        string expectedReason)
    {
        using var intent = CreateAndroidIntent(incoming);
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var options = new MauiExternalNavigationOptions
        {
            MaximumUriLength = maximumUriLength
        };
        using var provider = CreateAppLinkDispatcherProvider(options, diagnostics);
        _ = provider.GetRequiredService<MauiExternalNavigationDispatcher>();

        MauiExternalNavigationIngress rejected = AndroidAppLinkRequestFactory.FromIntent(intent, options);
        MauiExternalNavigationIngress replay = AndroidAppLinkRequestFactory.FromIntent(intent, options);

        Assert.False(AppNavExternalNavigationBuilderExtensions.Dispatch(rejected, options));
        Assert.False(AppNavExternalNavigationBuilderExtensions.Dispatch(replay, options));
        Assert.Null(rejected.Request);
        Assert.Equal(expectedReason, rejected.RejectionReason.ToString());
        Assert.True(intent.HasCategory(AndroidAppLinkRequestFactory.ConsumedCategoryName));
        Assert.Null(replay.Request);
        Assert.Equal(MauiExternalNavigationRejectionReason.None, replay.RejectionReason);
        NavigationDiagnosticEvent diagnostic = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationRejected);
        Assert.Equal(expectedReason, diagnostic.Data[NavigationDiagnosticDataKeys.ExternalNavigationReason]);
        Assert.DoesNotContain(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.AppLinkReceived);
    }

    [Fact]
    public void AndroidIntentWithoutDataIsConsumedOnce()
    {
        using var intent = new Intent();
        var options = new MauiExternalNavigationOptions();

        MauiExternalNavigationIngress first = AndroidAppLinkRequestFactory.FromIntent(intent, options);
        MauiExternalNavigationIngress replay = AndroidAppLinkRequestFactory.FromIntent(intent, options);

        Assert.Null(first.Request);
        Assert.Equal(MauiExternalNavigationRejectionReason.None, first.RejectionReason);
        Assert.True(intent.HasCategory(AndroidAppLinkRequestFactory.ConsumedCategoryName));
        Assert.Null(replay.Request);
        Assert.Equal(MauiExternalNavigationRejectionReason.None, replay.RejectionReason);
    }

    [Fact]
    public void ConsumedAndroidIntentRemainsConsumedAfterParcelRoundTrip()
    {
        using var intent = CreateAndroidIntent("https://example.com/stores/northwind");
        var options = new MauiExternalNavigationOptions();

        MauiExternalNavigationIngress first = AndroidAppLinkRequestFactory.FromIntent(intent, options);
        using Intent restored = RoundTrip(intent);
        MauiExternalNavigationIngress replay = AndroidAppLinkRequestFactory.FromIntent(restored, options);

        Assert.NotNull(first.Request);
        Assert.True(restored.HasCategory(AndroidAppLinkRequestFactory.ConsumedCategoryName));
        Assert.Null(replay.Request);
        Assert.Equal(MauiExternalNavigationRejectionReason.None, replay.RejectionReason);
    }

    [Fact]
    public void AndroidIntentWithUnavailableParcelableExtraIsAcceptedWithoutInspectingExtras()
    {
        var incoming = new Uri("https://example.com/stores/northwind");
        using var intent = CreateAndroidIntent(incoming.ToString());
        using Bundle extras = CreateBundleWithUnavailableParcelable();
        intent.ReplaceExtras(extras);
        var options = new MauiExternalNavigationOptions();

        MauiExternalNavigationIngress ingress = AndroidAppLinkRequestFactory.FromIntent(intent, options);

        AssertProvenance(ingress.Request, MauiAppLinkProvenanceProviders.AndroidIntent, incoming);
        Assert.Equal(MauiExternalNavigationRejectionReason.None, ingress.RejectionReason);
        Assert.True(intent.HasCategory(AndroidAppLinkRequestFactory.ConsumedCategoryName));
    }

    [Fact]
    public void NullAndroidIntentIsIgnored()
    {
        var options = new MauiExternalNavigationOptions();

        MauiExternalNavigationIngress ingress = AndroidAppLinkRequestFactory.FromIntent(null, options);

        Assert.Null(ingress.Request);
        Assert.Equal(MauiExternalNavigationRejectionReason.None, ingress.RejectionReason);
    }

    private static Intent CreateAndroidIntent(string value)
    {
        return new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(value));
    }

    private static Intent RoundTrip(Intent intent)
    {
        using Parcel parcel = Parcel.Obtain();
        intent.WriteToParcel(parcel, ParcelableWriteFlags.None);
        parcel.SetDataPosition(0);
        return (Intent)Intent.Creator!.CreateFromParcel(parcel)!;
    }

    private static Bundle CreateBundleWithUnavailableParcelable()
    {
        const int bundleMagic = 0x4C444E42;
        const int parcelableValueType = 4;

        using Parcel parcel = Parcel.Obtain();
        parcel.WriteInt(0);
        parcel.WriteInt(bundleMagic);
        int payloadStart = parcel.DataPosition();
        parcel.WriteInt(1);
        parcel.WriteString("untrusted");
        parcel.WriteInt(parcelableValueType);
        parcel.WriteString("com.example.appnav.UnavailableParcelable");
        int payloadEnd = parcel.DataPosition();
        parcel.SetDataPosition(0);
        parcel.WriteInt(payloadEnd - payloadStart);
        parcel.SetDataPosition(0);
        return (Bundle)Bundle.Creator!.CreateFromParcel(parcel)!;
    }
#endif

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

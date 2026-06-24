using AdamE.MauiRouter.Maui.AppLinks;
using AdamE.MauiRouter.Requests;

#if ANDROID
using Android.Content;
#endif

#if IOS || MACCATALYST
using Foundation;
#endif

namespace AdamE.MauiRouter.Maui.Tests;

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

#if ANDROID
    [Fact]
    public void AndroidIntentFactoryPopulatesAndroidIntentProvenance()
    {
        var incoming = new Uri("https://example.com/stores/northwind");
        using var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(incoming.ToString()));

        var request = AndroidAppLinkRequestFactory.FromIntent(intent);

        AssertProvenance(request, MauiAppLinkProvenanceProviders.AndroidIntent, incoming);
    }
#endif

#if IOS || MACCATALYST
    [Fact]
    public void AppleOpenUrlFactoryPopulatesOpenUrlProvenance()
    {
        var incoming = new Uri("https://example.com/stores/northwind");
        using var url = new NSUrl(incoming.ToString());

        var request = AppleAppLinkRequestFactory.FromOpenUrl(url);

        AssertProvenance(request, MauiAppLinkProvenanceProviders.IosOpenUrl, incoming);
    }

    [Fact]
    public void AppleUserActivityFactoryPopulatesUserActivityProvenance()
    {
        var incoming = new Uri("https://example.com/stores/northwind");
        using var activity = new NSUserActivity("NSUserActivityTypeBrowsingWeb")
        {
            WebPageUrl = new NSUrl(incoming.ToString())
        };

        var request = AppleAppLinkRequestFactory.FromUserActivity(activity);

        AssertProvenance(request, MauiAppLinkProvenanceProviders.IosUserActivity, incoming);
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
}

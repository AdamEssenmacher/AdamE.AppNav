#if IOS || MACCATALYST
using AdamE.AppNav.Requests;
using Foundation;

namespace AdamE.AppNav.Maui.AppLinks;

internal static class AppleAppLinkRequestFactory
{
    public static RouterNavigationRequest? FromOpenUrl(NSUrl? url)
    {
        return TryCreate(url?.AbsoluteString, MauiAppLinkProvenanceProviders.IosOpenUrl);
    }

    public static RouterNavigationRequest? FromUserActivity(NSUserActivity? activity)
    {
        return TryCreate(activity?.WebPageUrl?.AbsoluteString, MauiAppLinkProvenanceProviders.IosUserActivity);
    }

    private static RouterNavigationRequest? TryCreate(string? value, string provider)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? RouterNavigationRequest.FromUri(
                uri,
                NavigationRequestSource.AppLink,
                provenance: new NavigationRequestProvenance(
                    provider: provider,
                    originalUri: uri))
            : null;
    }
}
#endif

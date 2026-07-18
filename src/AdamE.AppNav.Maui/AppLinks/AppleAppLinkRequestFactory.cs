#if IOS || MACCATALYST
using AdamE.AppNav.Requests;
using Foundation;

namespace AdamE.AppNav.Maui.AppLinks;

internal static class AppleAppLinkRequestFactory
{
    public static RouterNavigationRequest? FromOpenUrl(NSUrl? url)
    {
        return MauiAppLinkRequestFactory.TryFromUriString(
            url?.AbsoluteString,
            MauiAppLinkProvenanceProviders.IosOpenUrl);
    }

    public static RouterNavigationRequest? FromUserActivity(NSUserActivity? activity)
    {
        return MauiAppLinkRequestFactory.TryFromUriString(
            activity?.WebPageUrl?.AbsoluteString,
            MauiAppLinkProvenanceProviders.IosUserActivity);
    }
}
#endif

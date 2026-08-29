#if IOS || MACCATALYST
using AdamE.AppNav.Requests;
using Foundation;

namespace AdamE.AppNav.Maui.AppLinks;

internal static class AppleAppLinkRequestFactory
{
    public static MauiExternalNavigationIngress FromOpenUrl(
        NSUrl? url,
        MauiExternalNavigationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return MauiAppLinkRequestFactory.ParseUriString(
            url?.AbsoluteString,
            MauiAppLinkProvenanceProviders.IosOpenUrl,
            options.MaximumUriLength);
    }

    public static MauiExternalNavigationIngress FromUserActivity(
        NSUserActivity? activity,
        MauiExternalNavigationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return MauiAppLinkRequestFactory.ParseUriString(
            activity?.WebPageUrl?.AbsoluteString,
            MauiAppLinkProvenanceProviders.IosUserActivity,
            options.MaximumUriLength);
    }
}
#endif

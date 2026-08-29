#if ANDROID
using AdamE.AppNav.Requests;
using Android.Content;

namespace AdamE.AppNav.Maui.AppLinks;

internal static class AndroidAppLinkRequestFactory
{
    public static MauiExternalNavigationIngress FromIntent(
        Intent? intent,
        MauiExternalNavigationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return MauiAppLinkRequestFactory.ParseUriString(
            intent?.DataString,
            MauiAppLinkProvenanceProviders.AndroidIntent,
            options.MaximumUriLength);
    }
}
#endif

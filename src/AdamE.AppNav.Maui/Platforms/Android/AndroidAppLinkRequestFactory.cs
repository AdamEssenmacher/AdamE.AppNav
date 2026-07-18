#if ANDROID
using AdamE.AppNav.Requests;
using Android.Content;

namespace AdamE.AppNav.Maui.AppLinks;

internal static class AndroidAppLinkRequestFactory
{
    public static RouterNavigationRequest? FromIntent(Intent? intent)
    {
        return MauiAppLinkRequestFactory.TryFromUriString(
            intent?.DataString,
            MauiAppLinkProvenanceProviders.AndroidIntent);
    }
}
#endif

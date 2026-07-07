#if ANDROID
using AdamE.AppNav.Requests;
using Android.Content;

namespace AdamE.AppNav.Maui.AppLinks;

internal static class AndroidAppLinkRequestFactory
{
    public static RouterNavigationRequest? FromIntent(Intent? intent)
    {
        return Uri.TryCreate(intent?.DataString, UriKind.Absolute, out var uri)
            ? RouterNavigationRequest.FromUri(
                uri,
                NavigationRequestSource.AppLink,
                provenance: new NavigationRequestProvenance(
                    provider: MauiAppLinkProvenanceProviders.AndroidIntent,
                    originalUri: uri))
            : null;
    }
}
#endif

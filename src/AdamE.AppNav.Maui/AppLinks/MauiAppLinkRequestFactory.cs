using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Maui.AppLinks;

internal static class MauiAppLinkRequestFactory
{
    public static RouterNavigationRequest FromUri(Uri uri)
    {
        return RouterNavigationRequest.FromUri(
            uri,
            NavigationRequestSource.AppLink,
            provenance: new NavigationRequestProvenance(
                provider: MauiAppLinkProvenanceProviders.MauiAppLink,
                originalUri: uri));
    }
}

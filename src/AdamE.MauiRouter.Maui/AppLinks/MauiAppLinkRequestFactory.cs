using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Maui.AppLinks;

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

using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Maui.AppLinks;

internal static class MauiAppLinkRequestFactory
{
    public static RouterNavigationRequest FromUri(Uri uri)
    {
        return FromUri(uri, MauiAppLinkProvenanceProviders.MauiAppLink);
    }

    public static RouterNavigationRequest? TryFromUriString(string? value, string provider)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? FromUri(uri, provider)
            : null;
    }

    private static RouterNavigationRequest FromUri(Uri uri, string provider)
    {
        return RouterNavigationRequest.FromUri(
            uri,
            NavigationRequestSource.AppLink,
            provenance: new NavigationRequestProvenance(
                provider: provider,
                originalUri: uri));
    }
}

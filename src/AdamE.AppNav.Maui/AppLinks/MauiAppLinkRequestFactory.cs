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
        return ParseUriString(value, provider, Int32.MaxValue).Request;
    }

    public static MauiExternalNavigationIngress ParseUriString(
        string? value,
        string provider,
        int maximumUriLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (maximumUriLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumUriLength));

        if (string.IsNullOrWhiteSpace(value))
            return MauiExternalNavigationIngress.Empty;

        // Apply the transport bound before URI parsing so an oversized platform payload never
        // reaches URI normalization, request construction, application filters, or analytics.
        if (value.Length > maximumUriLength)
        {
            return MauiExternalNavigationIngress.Rejected(
                MauiExternalNavigationRejectionReason.UriTooLong);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return MauiExternalNavigationIngress.Rejected(
                Uri.TryCreate(value, UriKind.Relative, out _)
                    ? MauiExternalNavigationRejectionReason.RelativeUri
                    : MauiExternalNavigationRejectionReason.InvalidUri);
        }

        return MauiExternalNavigationIngress.Accepted(FromUri(uri, provider));
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

internal readonly record struct MauiExternalNavigationIngress(
    RouterNavigationRequest? Request,
    MauiExternalNavigationRejectionReason RejectionReason)
{
    public static MauiExternalNavigationIngress Empty { get; } = new(null, MauiExternalNavigationRejectionReason.None);

    public static MauiExternalNavigationIngress Accepted(RouterNavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new MauiExternalNavigationIngress(request, MauiExternalNavigationRejectionReason.None);
    }

    public static MauiExternalNavigationIngress Rejected(MauiExternalNavigationRejectionReason reason)
    {
        if (reason == MauiExternalNavigationRejectionReason.None)
            throw new ArgumentOutOfRangeException(nameof(reason));

        return new MauiExternalNavigationIngress(null, reason);
    }
}

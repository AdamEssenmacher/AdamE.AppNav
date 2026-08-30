#if ANDROID
using AdamE.AppNav.Requests;
using Android.Content;

namespace AdamE.AppNav.Maui.AppLinks;

internal static class AndroidAppLinkRequestFactory
{
    internal const string ConsumedExtraName = "AdamE.AppNav.Maui.AppLinks.Consumed";

    public static MauiExternalNavigationIngress FromIntent(
        Intent? intent,
        MauiExternalNavigationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (intent is null ||
            (intent.Flags & ActivityFlags.LaunchedFromHistory) != 0 ||
            intent.HasExtra(ConsumedExtraName))
        {
            return MauiExternalNavigationIngress.Empty;
        }

        // Mark the boundary activation before parsing so malformed and oversized values cannot
        // be replayed later with a fresh request timestamp or duplicate rejection diagnostics.
        intent.PutExtra(ConsumedExtraName, true);

        return MauiAppLinkRequestFactory.ParseUriString(
            intent.DataString,
            MauiAppLinkProvenanceProviders.AndroidIntent,
            options.MaximumUriLength);
    }
}
#endif

namespace AdamE.MauiRouter.Maui.AppLinks;

/// <summary>
/// Provider names that MauiRouter sets automatically for built-in MAUI app-link ingress.
/// </summary>
public static class MauiAppLinkProvenanceProviders
{
    /// <summary>
    /// Android intent app-link ingress.
    /// </summary>
    public const string AndroidIntent = "android-intent";

    /// <summary>
    /// iOS and Mac Catalyst open-url ingress.
    /// </summary>
    public const string IosOpenUrl = "ios-open-url";

    /// <summary>
    /// iOS and Mac Catalyst user-activity ingress.
    /// </summary>
    public const string IosUserActivity = "ios-user-activity";

    /// <summary>
    /// Generic MAUI app-link URI ingress.
    /// </summary>
    public const string MauiAppLink = "maui-app-link";
}

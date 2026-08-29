using AdamE.AppNav.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;

namespace AdamE.AppNav.Maui.AppLinks;

/// <summary>
/// Enables allowlisted external URI ingress through MAUI lifecycle callbacks.
/// </summary>
public static class AppNavExternalNavigationBuilderExtensions
{
    /// <summary>
    /// Enables external navigation after applying the required origin and queue policy.
    /// </summary>
    public static MauiAppBuilder UseAppNavExternalNavigation(
        this MauiAppBuilder builder,
        Action<MauiExternalNavigationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new MauiExternalNavigationOptions();
        configure(options);
        options.ValidateForEnablement();
        builder.Services.AddSingleton(options);

        builder.ConfigureLifecycleEvents(events =>
        {
#if ANDROID
            events.AddAndroid(android => android
                .OnCreate((activity, _) => Dispatch(AndroidAppLinkRequestFactory.FromIntent(activity.Intent, options), options))
                .OnNewIntent((_, intent) => Dispatch(AndroidAppLinkRequestFactory.FromIntent(intent, options), options)));
#endif

#if IOS || MACCATALYST
            events.AddiOS(ios => ios
                .OpenUrl((_, url, _) =>
                    Dispatch(AppleAppLinkRequestFactory.FromOpenUrl(url, options), options))
                .ContinueUserActivity((_, userActivity, _) =>
                    Dispatch(AppleAppLinkRequestFactory.FromUserActivity(userActivity, options), options)));
#endif
        });

        return builder;
    }

    internal static bool Dispatch(
        RouterNavigationRequest? request,
        MauiExternalNavigationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return MauiExternalNavigationBridge.Submit(request, options);
    }

    internal static bool Dispatch(
        MauiExternalNavigationIngress ingress,
        MauiExternalNavigationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return MauiExternalNavigationBridge.Submit(ingress, options);
    }
}

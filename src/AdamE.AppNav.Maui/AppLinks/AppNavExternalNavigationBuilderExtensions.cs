using AdamE.AppNav.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;
#if IOS || MACCATALYST
using Foundation;
using UIKit;
#endif

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
                    Dispatch(AppleAppLinkRequestFactory.FromUserActivity(userActivity, options), options))
                .SceneOpenUrl((_, contexts) => DispatchSceneUrls(contexts, options))
                .SceneContinueUserActivity((_, userActivity) =>
                    Dispatch(AppleAppLinkRequestFactory.FromUserActivity(userActivity, options), options))
                .SceneWillConnect((_, _, connectionOptions) =>
                {
                    // MAUI raises this before creating the window. The bridge buffers startup links.
                    DispatchSceneUrls(connectionOptions.UrlContexts, options);
                    foreach (NSUserActivity userActivity in connectionOptions.UserActivities)
                        Dispatch(AppleAppLinkRequestFactory.FromUserActivity(userActivity, options), options);
                }));
#endif
        });

        return builder;
    }

#if IOS || MACCATALYST
    private static bool DispatchSceneUrls(
        NSSet<UIOpenUrlContext> contexts,
        MauiExternalNavigationOptions options)
    {
        bool handled = false;
        foreach (UIOpenUrlContext context in contexts)
            handled |= Dispatch(AppleAppLinkRequestFactory.FromOpenUrl(context.Url, options), options);

        return handled;
    }
#endif

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

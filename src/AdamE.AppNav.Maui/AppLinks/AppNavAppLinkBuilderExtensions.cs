using AdamE.AppNav.Requests;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;

namespace AdamE.AppNav.Maui.AppLinks;

public static class AppNavAppLinkBuilderExtensions
{
    public static MauiAppBuilder UseAppNavAppLinks(this MauiAppBuilder builder)
    {
        return UseAppNavAppLinks(builder, configure: null);
    }

    public static MauiAppBuilder UseAppNavAppLinks(
        this MauiAppBuilder builder,
        Action<AppNavAppLinkOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AppNavAppLinkOptions();
        configure?.Invoke(options);

        builder.ConfigureLifecycleEvents(events =>
        {
#if ANDROID
            events.AddAndroid(android => android
                .OnCreate((activity, _) => Dispatch(AndroidAppLinkRequestFactory.FromIntent(activity.Intent), options))
                .OnNewIntent((_, intent) => Dispatch(AndroidAppLinkRequestFactory.FromIntent(intent), options)));
#endif

#if IOS || MACCATALYST
            events.AddiOS(ios => ios
                .OpenUrl((_, url, _) =>
                {
                    var request = AppleAppLinkRequestFactory.FromOpenUrl(url);
                    Dispatch(request, options);
                    return request is not null;
                })
                .ContinueUserActivity((_, userActivity, _) =>
                {
                    var request = AppleAppLinkRequestFactory.FromUserActivity(userActivity);
                    Dispatch(request, options);
                    return request is not null;
                }));
#endif
        });

        return builder;
    }

    internal static bool Dispatch(RouterNavigationRequest? request, AppNavAppLinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (request is null)
        {
            return false;
        }

        if (!options.ShouldDispatch(request))
        {
            return false;
        }

        MauiExternalNavigationDispatcher.Submit(request);
        return true;
    }
}

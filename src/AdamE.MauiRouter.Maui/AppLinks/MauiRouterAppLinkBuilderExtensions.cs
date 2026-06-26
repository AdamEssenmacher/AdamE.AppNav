using AdamE.MauiRouter.Requests;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;

namespace AdamE.MauiRouter.Maui.AppLinks;

public static class MauiRouterAppLinkBuilderExtensions
{
    public static MauiAppBuilder UseMauiRouterAppLinks(this MauiAppBuilder builder)
    {
        return UseMauiRouterAppLinks(builder, configure: null);
    }

    public static MauiAppBuilder UseMauiRouterAppLinks(
        this MauiAppBuilder builder,
        Action<MauiRouterAppLinkOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new MauiRouterAppLinkOptions();
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

    internal static bool Dispatch(RouterNavigationRequest? request, MauiRouterAppLinkOptions options)
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

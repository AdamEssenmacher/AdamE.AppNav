using AdamE.MauiRouter.Requests;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;

namespace AdamE.MauiRouter.Maui.AppLinks;

public static class MauiRouterAppLinkBuilderExtensions
{
    public static MauiAppBuilder UseMauiRouterAppLinks(this MauiAppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureLifecycleEvents(events =>
        {
#if ANDROID
            events.AddAndroid(android => android
                .OnCreate((activity, _) => Dispatch(AndroidAppLinkRequestFactory.FromIntent(activity.Intent)))
                .OnNewIntent((_, intent) => Dispatch(AndroidAppLinkRequestFactory.FromIntent(intent))));
#endif

#if IOS || MACCATALYST
            events.AddiOS(ios => ios
                .OpenUrl((_, url, _) =>
                {
                    var request = AppleAppLinkRequestFactory.FromOpenUrl(url);
                    Dispatch(request);
                    return request is not null;
                })
                .ContinueUserActivity((_, userActivity, _) =>
                {
                    var request = AppleAppLinkRequestFactory.FromUserActivity(userActivity);
                    Dispatch(request);
                    return request is not null;
                }));
#endif
        });

        return builder;
    }

    private static void Dispatch(RouterNavigationRequest? request)
    {
        MauiExternalNavigationDispatcher.Submit(request);
    }
}

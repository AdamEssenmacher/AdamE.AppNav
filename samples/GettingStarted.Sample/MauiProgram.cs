using AdamE.AppNav;
using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Maui.DependencyInjection;

namespace GettingStarted.Sample;

public static class MauiProgram
{
    // #region getting-started-registration
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // #region getting-started-registration-services
        builder.Services.AddAppNavStartup(options =>
        {
            options.AppLinkGracePeriod = TimeSpan.Zero;
            options.FallbackRouteFactory = static (_, _) =>
                ValueTask.FromResult<AppRoute?>(new HomeRoute());
        });
        builder.Services.AddAppNav(
            AppNavGenerated.CreateRouteTable(),
            GettingStartedNavigationModel.Create(),
            pages => pages.AddModule(AppNavGenerated.MauiPageModule));
        // #endregion getting-started-registration-services

        return builder.Build();
    }
    // #endregion getting-started-registration
}

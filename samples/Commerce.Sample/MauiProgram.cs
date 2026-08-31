using AdamE.AppNav;
using AdamE.AppNav.Maui.DependencyInjection;
using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Policies;
using Commerce.Sample.Navigation;
using Commerce.Sample.Pages;
using Commerce.Sample.Routes;
using Microsoft.Extensions.Logging;

namespace Commerce.Sample;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseAppNavExternalNavigation(options =>
			{
				options.AllowOrigin(new Uri("https://example.com"));
				options.AllowOrigin(new Uri("https://legacy.example.com"));
#if DEBUG
				options.AllowOrigin(new Uri("appnav-commerce://shop"));
#endif
			})
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		builder.Services.AddAppNavDiagnostics();
		builder.Services.AddAppNavMauiPresentation(options =>
			options.MapFlyoutBranchHost("store-tabs", "Store"));
		builder.Services.AddSingleton<INavigationRequestTransformer, LegacyProductUrlTransformer>();
		builder.Services.AddAppNavStartup(options =>
		{
			options.FallbackRouteFactory = (_, _) =>
				ValueTask.FromResult<AppRoute?>(
					new ProductDetailRoute(
						"northwind",
						123,
						"blue",
						"spring"));
		});
		builder.Services.AddAppNav(
			AppNavGenerated.CreateRouteTable(),
			CommerceNavigationModel.Create(),
			options => options
				.AddModule(AppNavGenerated.MauiPageModule)
				.MapPage<CommerceNotFoundRoute, CommerceNotFoundPage>(),
			options => options.FallbackRouteFactory = context =>
				new CommerceNotFoundRoute("northwind", context.Request.Uri!));

		return builder.Build();
	}
}

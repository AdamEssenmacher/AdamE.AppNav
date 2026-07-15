using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Maui.DependencyInjection;
using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Requests;
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
			.UseAppNavAppLinks()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		builder.Services.AddSingleton<NavigationDiagnostics>();
		builder.Services.AddSingleton<INavigationRequestTransformer, LegacyProductUrlTransformer>();
		builder.Services.AddAppNavFileDeferredNavigationRequests(options =>
		{
			options.BaseUri = new Uri("https://example.com/");
			options.RouteStateRegistry = CommerceRouteMetadata.RouteStateRegistry;
		});
		builder.Services.AddAppNavStartup(options =>
		{
			options.FallbackRequestFactory = (_, _) =>
				ValueTask.FromResult<RouterNavigationRequest?>(
					RouterNavigationRequest.FromUri(
						new Uri("https://example.com/stores/northwind/products/123?variant=blue&promo=spring&campaign=spring-launch"),
						NavigationRequestSource.InAppCommand));
		});
		builder.Services.AddAppNav<CommerceNavigationPlanner>(
			AppNavGenerated.CreateRouteTable(),
			options => options
				.AddModule(AppNavGenerated.MauiPageModule)
				.MapPage<CommerceNotFoundRoute, CommerceNotFoundPage>());

		return builder.Build();
	}
}

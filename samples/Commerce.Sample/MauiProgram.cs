using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Maui.DependencyInjection;
using AdamE.MauiRouter.Maui.AppLinks;
using AdamE.MauiRouter.Maui.Persistence;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Requests;
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
			.UseMauiRouterAppLinks()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		builder.Services.AddSingleton<NavigationDiagnostics>();
		builder.Services.AddMauiRouterFileNavigationPersistence(options =>
		{
			options.BaseUri = new Uri("https://example.com/");
			options.RouteStateRegistry = CommerceRouteMetadata.RouteStateRegistry;
		});
		builder.Services.AddMauiRouterStartup(options =>
		{
			options.FallbackRequestFactory = (_, _) =>
				ValueTask.FromResult<RouterNavigationRequest?>(
					RouterNavigationRequest.FromUri(
						new Uri("https://example.com/stores/northwind/products/123?variant=blue&promo=spring&campaign=spring-launch"),
						NavigationRequestSource.Restore));
		});
		builder.Services.AddSingleton<INavigationRequestPolicy>(_ =>
			new AllowedUriOriginPolicy(new[] { new Uri("https://example.com") }));
		builder.Services.AddMauiRouter<CommerceNavigationPlanner>(
			SampleRouteTable.Create(),
			options => options
				.MapPage<StoreHomeRoute, StoreHomePage>()
				.MapPage<StoreCatalogRoute, StoreCatalogPage>()
				.MapPage<ProductDetailRoute, ProductDetailPage>()
				.MapPage<CartRoute, CartPage>()
				.MapPage<OrdersRoute, OrdersPage>()
				.MapPage<CommerceNotFoundRoute, CommerceNotFoundPage>());

		return builder.Build();
	}
}

using AdamE.AppNav;
using AdamE.AppNav.Routing;

namespace Commerce.Sample.Routes;

[AppNavRoute("/stores/{storeId}")]
public sealed record StoreHomeRoute(string StoreId) : AppRoute;

[AppNavRoute("/stores/{storeId}/catalog")]
public sealed record StoreCatalogRoute(string StoreId) : AppRoute;

[AppNavRoute("/stores/{storeId}/products/{productId:int}")]
[AppNavQuery("Variant")]
[AppNavQuery("Promo")]
[AppNavQueryMetadata(typeof(CommerceRouteMetadata), nameof(CommerceRouteMetadata.Campaign))]
public sealed record ProductDetailRoute(string StoreId, int ProductId, string? Variant = null, string? Promo = null) : AppRoute;

[AppNavRoute("/stores/{storeId}/cart")]
public sealed record CartRoute(string StoreId) : AppRoute;

[AppNavRoute("/stores/{storeId}/orders")]
public sealed record OrdersRoute(string StoreId) : AppRoute;

public sealed record CommerceNotFoundRoute(string StoreId, Uri Uri) : AppRoute;

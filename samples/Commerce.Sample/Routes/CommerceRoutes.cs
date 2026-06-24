using AdamE.MauiRouter;

namespace Commerce.Sample.Routes;

public sealed record StoreHomeRoute(string StoreId) : AppRoute;

public sealed record StoreCatalogRoute(string StoreId) : AppRoute;

public sealed record ProductDetailRoute(string StoreId, int ProductId, string? Variant = null, string? Promo = null) : AppRoute;

public sealed record CartRoute(string StoreId) : AppRoute;

public sealed record OrdersRoute(string StoreId) : AppRoute;

public sealed record CommerceNotFoundRoute(string StoreId, Uri Uri) : AppRoute;

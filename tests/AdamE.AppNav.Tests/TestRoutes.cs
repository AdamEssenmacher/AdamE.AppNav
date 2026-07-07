using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Tests;

internal static class TestRoutes
{
    public sealed record StoreRoute(string StoreId) : AppRoute;

    public sealed record CatalogRoute(string StoreId) : AppRoute;

    public sealed record ProductDetailRoute(string StoreId, int ProductId, string? Variant = null, string? Promo = null) : AppRoute;

    public static RouteTable CreateTable()
    {
        return RouteTable.Create(routes => routes
            .Map(
                "/stores/{storeId}",
                match => new StoreRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))
            .Map(
                "/stores/{storeId}/catalog",
                match => new CatalogRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))
            .Map(
                "/stores/{storeId}/products/{productId:int}",
                match => new ProductDetailRoute(
                    match.Path("storeId"),
                    match.Path<int>("productId"),
                    match.Query("variant"),
                    match.Query("promo")),
                format => format
                    .PathParam("storeId", route => route.StoreId)
                    .PathParam("productId", route => route.ProductId)
                    .QueryParam("variant", route => route.Variant)
                    .QueryParam("promo", route => route.Promo)));
    }
}

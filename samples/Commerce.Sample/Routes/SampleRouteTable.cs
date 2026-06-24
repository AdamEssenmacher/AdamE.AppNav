using AdamE.MauiRouter.Routing;

namespace Commerce.Sample.Routes;

public static class SampleRouteTable
{
    public static RouteTable Create()
    {
        return RouteTable.Create(routes => routes
            .Map(
                "/stores/{storeId}",
                match => new StoreHomeRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))
            .Map(
                "/stores/{storeId}/catalog",
                match => new StoreCatalogRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))
            .Map(
                "/stores/{storeId}/products/{productId:int}",
                match =>
                {
                    match.QueryMetadata(CommerceRouteMetadata.Campaign);
                    return new ProductDetailRoute(
                        match.Path("storeId"),
                        match.Path<int>("productId"),
                        match.Query("variant"),
                        match.Query("promo"));
                },
                format => format
                    .PathParam("storeId", route => route.StoreId)
                    .PathParam("productId", route => route.ProductId)
                    .QueryParam("variant", route => route.Variant)
                    .QueryParam("promo", route => route.Promo)
                    .QueryMetadata(CommerceRouteMetadata.Campaign))
            .Map(
                "/stores/{storeId}/cart",
                match => new CartRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))
            .Map(
                "/stores/{storeId}/orders",
                match => new OrdersRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId)));
    }
}

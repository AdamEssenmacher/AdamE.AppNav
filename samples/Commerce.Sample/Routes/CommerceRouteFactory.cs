using AdamE.AppNav;

namespace Commerce.Sample.Routes;

public static class CommerceRouteFactory
{
    public static AppRouteRequest StoreHome(string storeId)
    {
        return AppRouteRequest.For(new StoreHomeRoute(storeId));
    }

    public static AppRouteRequest StoreCatalog(string storeId)
    {
        return AppRouteRequest.For(new StoreCatalogRoute(storeId));
    }

    public static AppRouteRequest ProductDetail(
        string storeId,
        int productId,
        string? variant = null,
        string? promo = null,
        string? campaign = null)
    {
        return AppRouteRequest
            .For(new ProductDetailRoute(storeId, productId, variant, promo))
            .WithMetadata(CommerceRouteMetadata.Campaign, NormalizeCampaign(campaign));
    }

    public static AppRouteRequest Cart(string storeId)
    {
        return AppRouteRequest.For(new CartRoute(storeId));
    }

    public static AppRouteRequest Orders(string storeId)
    {
        return AppRouteRequest.For(new OrdersRoute(storeId));
    }

    private static string? NormalizeCampaign(string? campaign)
    {
        return string.IsNullOrWhiteSpace(campaign) ? null : campaign;
    }
}

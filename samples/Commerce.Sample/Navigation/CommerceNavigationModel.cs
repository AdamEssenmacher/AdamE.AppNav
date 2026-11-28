using AdamE.AppNav;
using AdamE.AppNav.Planning;
using Commerce.Sample.Routes;

namespace Commerce.Sample.Navigation;

public static class CommerceNavigationModel
{
    public static BranchHostNavigationModel<AppRoute> Create()
    {
        return BranchHostNavigationModel<AppRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "store-tabs");
            builder.Branch("home", "Home", route => new StoreHomeRoute(GetStoreId(route)));
            builder.Branch("catalog", "Catalog", route => new StoreCatalogRoute(GetStoreId(route)));
            builder.Branch("cart", "Cart", route => new CartRoute(GetStoreId(route)));
            builder.Branch("orders", "Orders", route => new OrdersRoute(GetStoreId(route)));

            builder.Map<StoreHomeRoute>("home", recipe => recipe
                .EntryId(route => $"home:{Uri.EscapeDataString(route.StoreId)}")
                .ScopeKey(route => route.StoreId));

            builder.Map<CommerceNotFoundRoute>("home", recipe => recipe
                .EntryId(route => $"not-found:{route.Uri}")
                .ScopeKey(route => route.StoreId)
                .Canonical((route, metadata) =>
                [
                    Step(new StoreHomeRoute(route.StoreId)),
                    Step(route, metadata)
                ]));

            builder.Map<StoreCatalogRoute>("catalog", recipe => recipe
                .EntryId(route => $"catalog:{Uri.EscapeDataString(route.StoreId)}")
                .ScopeKey(route => route.StoreId));

            builder.Map<ProductDetailRoute>("catalog", recipe => recipe
                .EntryId(ProductEntryId)
                .ScopeKey(route => route.StoreId)
                .SlotId(_ => "product-detail")
                .Canonical((route, metadata) =>
                [
                    Step(new StoreCatalogRoute(route.StoreId)),
                    Step(route, metadata)
                ]));

            builder.Map<CartRoute>("cart", recipe => recipe
                .EntryId(route => $"cart:{Uri.EscapeDataString(route.StoreId)}")
                .ScopeKey(route => route.StoreId));

            builder.Map<OrdersRoute>("orders", recipe => recipe
                .EntryId(route => $"orders:{Uri.EscapeDataString(route.StoreId)}")
                .ScopeKey(route => route.StoreId));
        });
    }

    private static string ProductEntryId(ProductDetailRoute route)
    {
        var id = FormattableString.Invariant($"product:{Uri.EscapeDataString(route.StoreId)}:{route.ProductId}");
        if (route.Variant is { } variant)
            id += $":variant={Uri.EscapeDataString(variant)}";
        if (route.Promo is { } promo)
            id += $":promo={Uri.EscapeDataString(promo)}";
        return id;
    }

    private static StackRouteStep<AppRoute> Step(
        AppRoute route,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        return new StackRouteStep<AppRoute>(route, metadata);
    }

    private static string GetStoreId(AppRoute route)
    {
        return route switch
        {
            StoreHomeRoute home => home.StoreId,
            StoreCatalogRoute catalog => catalog.StoreId,
            ProductDetailRoute detail => detail.StoreId,
            CartRoute cart => cart.StoreId,
            OrdersRoute orders => orders.StoreId,
            CommerceNotFoundRoute notFound => notFound.StoreId,
            _ => throw new NotSupportedException(
                $"Route '{route.GetType().Name}' is not supported by the Commerce navigation model.")
        };
    }
}

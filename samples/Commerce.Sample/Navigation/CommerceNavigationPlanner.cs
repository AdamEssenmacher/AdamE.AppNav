using AdamE.MauiRouter;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.State;
using Commerce.Sample.Routes;

namespace Commerce.Sample.Navigation;

public sealed class CommerceNavigationPlanner : IAppNavigationPlanner
{
    public ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        var storeId = GetStoreId(context.Route);
        var root = new BranchHostNode(
            "store-tabs",
            new[]
            {
                new NavigationBranch(
                    "home",
                    "Home",
                    new StackNode("home-stack", CreateHomeEntries(context.Route, storeId))),
                new NavigationBranch(
                    "catalog",
                    "Catalog",
                    new StackNode("catalog-stack", CreateCatalogEntries(context.Route, storeId))),
                new NavigationBranch(
                    "cart",
                    "Cart",
                    new StackNode("cart-stack", new[]
                    {
                        Entry("cart", new CartRoute(storeId))
                    })),
                new NavigationBranch(
                    "orders",
                    "Orders",
                    new StackNode("orders-stack", new[]
                    {
                        Entry("orders", new OrdersRoute(storeId))
                    }))
            },
            SelectedBranchId: GetSelectedTab(context.Route),
            DefaultBranchId: "home");

        var state = new NavigationState(
            new[]
            {
                new WindowNode("main", root)
            },
            ActiveWindowId: "main");

        return ValueTask.FromResult(new NavigationPlan(state, NavigationPlanKind.Navigate, "Route planned by the sample app."));
    }

    private static IReadOnlyList<RouteEntry> CreateCatalogEntries(AppRoute route, string storeId)
    {
        var entries = new List<RouteEntry>
        {
            Entry("catalog", new StoreCatalogRoute(storeId))
        };

        if (route is ProductDetailRoute detail)
        {
            entries.Add(Entry($"product-{detail.ProductId}", detail));
        }

        return entries;
    }

    private static IReadOnlyList<RouteEntry> CreateHomeEntries(AppRoute route, string storeId)
    {
        var entries = new List<RouteEntry>
        {
            Entry("home", new StoreHomeRoute(storeId))
        };

        if (route is CommerceNotFoundRoute notFound)
        {
            entries.Add(Entry("not-found", notFound));
        }

        return entries;
    }

    private static RouteEntry Entry(string id, AppRoute route)
    {
        return new RouteEntry(id, route);
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
            _ => throw new NotSupportedException($"Route '{route.GetType().Name}' is not supported by the sample planner.")
        };
    }

    private static string GetSelectedTab(AppRoute route)
    {
        return route switch
        {
            StoreCatalogRoute or ProductDetailRoute => "catalog",
            CartRoute => "cart",
            OrdersRoute => "orders",
            _ => "home"
        };
    }
}

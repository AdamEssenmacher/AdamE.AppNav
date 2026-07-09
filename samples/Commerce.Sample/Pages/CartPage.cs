using AdamE.AppNav.Navigation;
using AdamE.AppNav.Maui;
using AdamE.AppNav.Requests;
using Commerce.Sample.Routes;

namespace Commerce.Sample.Pages;

[MauiRoutePage(typeof(CartRoute))]
public sealed class CartPage : ContentPage
{
    private readonly CartRoute _route;
    private readonly IRouterNavigator _navigator;

    public CartPage(CartRoute route, IRouterNavigator navigator)
    {
        _route = route;
        _navigator = navigator;
        Title = "Cart";

        var ordersButton = new Button { Text = "View orders" };
        ordersButton.Clicked += async (_, _) =>
        {
            await _navigator.NavigateAsync(
                CommerceRouteFactory.Orders(_route.StoreId),
                NavigationRequestSource.InAppCommand);
        };

        Content = new VerticalStackLayout
        {
            Padding = 24,
            Spacing = 12,
            Children =
            {
                new Label
                {
                    Text = $"Cart for {_route.StoreId}",
                    FontSize = 26,
                    FontAttributes = FontAttributes.Bold
                },
                new Label
                {
                    Text = "Cart is a primary tab, not a query parameter.",
                    FontSize = 16
                },
                ordersButton
            }
        };
    }
}

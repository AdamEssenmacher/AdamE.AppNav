using AdamE.AppNav.Navigation;
using AdamE.AppNav.Maui;
using Commerce.Sample.Routes;

namespace Commerce.Sample.Pages;

[MauiRoutePage(typeof(StoreHomeRoute))]
public sealed class StoreHomePage : ContentPage
{
    private readonly StoreHomeRoute _route;
    private readonly IRouterNavigator _navigator;

    public StoreHomePage(StoreHomeRoute route, IRouterNavigator navigator)
    {
        _route = route;
        _navigator = navigator;
        Title = "Home";

        var catalogButton = new Button { Text = "Browse catalog" };
        catalogButton.Clicked += async (_, _) =>
        {
            await _navigator.NavigateAsync(CommerceRouteFactory.StoreCatalog(_route.StoreId));
        };

        var cartButton = new Button { Text = "View cart" };
        cartButton.Clicked += async (_, _) =>
        {
            await _navigator.NavigateAsync(CommerceRouteFactory.Cart(_route.StoreId));
        };

        Content = new VerticalStackLayout
        {
            Padding = 24,
            Spacing = 16,
            Children =
            {
                new Label
                {
                    Text = _route.StoreId,
                    FontSize = 34,
                    FontAttributes = FontAttributes.Bold
                },
                new Label
                {
                    Text = "The store home route is one branch in the store tab host.",
                    FontSize = 16
                },
                catalogButton,
                cartButton
            }
        };
    }
}

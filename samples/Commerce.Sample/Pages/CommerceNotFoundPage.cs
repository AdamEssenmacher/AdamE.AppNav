using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Requests;
using Commerce.Sample.Routes;

namespace Commerce.Sample.Pages;

public sealed class CommerceNotFoundPage : ContentPage
{
    private readonly CommerceNotFoundRoute _route;
    private readonly IRouterNavigator _navigator;

    public CommerceNotFoundPage(CommerceNotFoundRoute route, IRouterNavigator navigator)
    {
        _route = route;
        _navigator = navigator;
        Title = "Not found";

        var homeButton = new Button { Text = "Go home" };
        homeButton.Clicked += async (_, _) =>
        {
            await _navigator.NavigateAsync(
                CommerceRouteFactory.StoreHome(_route.StoreId),
                NavigationRequestSource.InAppCommand);
        };

        var catalogButton = new Button { Text = "Browse catalog" };
        catalogButton.Clicked += async (_, _) =>
        {
            await _navigator.NavigateAsync(
                CommerceRouteFactory.StoreCatalog(_route.StoreId),
                NavigationRequestSource.InAppCommand);
        };

        Content = new VerticalStackLayout
        {
            Padding = 24,
            Spacing = 16,
            Children =
            {
                new Label
                {
                    Text = "Page not found",
                    FontSize = 30,
                    FontAttributes = FontAttributes.Bold
                },
                new Label
                {
                    Text = _route.Uri.ToString(),
                    FontSize = 14,
                    TextColor = Colors.Gray
                },
                homeButton,
                catalogButton
            }
        };
    }
}

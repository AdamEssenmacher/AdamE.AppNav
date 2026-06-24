using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Maui;
using AdamE.MauiRouter.Requests;
using Commerce.Sample.Routes;

namespace Commerce.Sample.Pages;

public sealed class ProductDetailPage : ContentPage
{
    private readonly ProductDetailRoute _route;
    private readonly IRouterNavigator _navigator;

    public ProductDetailPage(ProductDetailRoute route, IRouterNavigator navigator)
    {
        _route = route;
        _navigator = navigator;
        Title = $"Product {_route.ProductId}";

        var variant = string.IsNullOrWhiteSpace(_route.Variant)
            ? "Default variant"
            : $"Variant: {_route.Variant}";
        var promo = string.IsNullOrWhiteSpace(_route.Promo)
            ? "No promo applied"
            : $"Promo: {_route.Promo}";

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 16,
                Children =
                {
                    new Label
                    {
                        Text = $"Store {_route.StoreId}",
                        FontSize = 14,
                        TextColor = Colors.Gray
                    },
                    ProductHero(_route.ProductId),
                    new Label
                    {
                        Text = $"Product {_route.ProductId}",
                        FontSize = 30,
                        FontAttributes = FontAttributes.Bold
                    },
                    new Label
                    {
                        Text = variant,
                        FontSize = 18
                    },
                    new Label
                    {
                        Text = promo,
                        FontSize = 18
                    },
                    Button("Open product 456", async () =>
                    {
                        await _navigator.NavigateAsync(
                            CommerceRouteFactory.ProductDetail(_route.StoreId, 456, "black", "spring", "cross-sell"),
                            NavigationRequestSource.InAppCommand);
                    }),
                    Button("Back to catalog", async () =>
                    {
                        await _navigator.NavigateAsync(
                            CommerceRouteFactory.StoreCatalog(_route.StoreId),
                            NavigationRequestSource.InAppCommand);
                    }),
                    Button("Add to cart", async () =>
                    {
                        await _navigator.NavigateAsync(
                            CommerceRouteFactory.Cart(_route.StoreId),
                            NavigationRequestSource.InAppCommand);
                    })
                }
            }
        };
    }

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text };
        button.Clicked += async (_, _) => await action();
        return button;
    }

    private static Border ProductHero(int productId)
    {
        var hero = new Border
        {
            HeightRequest = 150,
            StrokeThickness = 0,
            BackgroundColor = productId switch
            {
                123 => Colors.SteelBlue,
                456 => Colors.Black,
                _ => Colors.SeaGreen
            },
            Content = new Label
            {
                Text = $"#{productId}",
                FontSize = 28,
                FontAttributes = FontAttributes.Bold,
                TextColor = Colors.White,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            }
        };
        MauiRouterTransition.SetSharedElementId(hero, ProductSharedElementId(productId));
        return hero;
    }

    private static string ProductSharedElementId(int productId)
    {
        return $"product-{productId}-tile";
    }
}

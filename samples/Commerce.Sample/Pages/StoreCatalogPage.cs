using AdamE.AppNav.Navigation;
using AdamE.AppNav.Maui;
using AdamE.AppNav.Requests;
using Commerce.Sample.Routes;

namespace Commerce.Sample.Pages;

[MauiRoutePage(typeof(StoreCatalogRoute))]
public sealed class StoreCatalogPage : ContentPage
{
    private readonly StoreCatalogRoute _route;
    private readonly IRouterNavigator _navigator;

    public StoreCatalogPage(StoreCatalogRoute route, IRouterNavigator navigator)
    {
        _route = route;
        _navigator = navigator;
        Title = "Catalog";

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
                        Text = $"{_route.StoreId} catalog",
                        FontSize = 30,
                        FontAttributes = FontAttributes.Bold
                    },
                    ProductTile(123, "blue", "spring", "spring-launch"),
                    ProductTile(456, "black", null, null),
                    ProductTile(789, null, "clearance", "clearance-spotlight")
                }
            }
        };
    }

    private Border ProductTile(int productId, string? variant, string? promo, string? campaign)
    {
        var swatch = ProductSwatch(productId);
        var details = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                new Label
                {
                    Text = $"Product {productId}",
                    FontSize = 18,
                    FontAttributes = FontAttributes.Bold
                },
                new Label
                {
                    Text = variant is null ? "Standard option" : $"Variant: {variant}",
                    FontSize = 14,
                    TextColor = Colors.DimGray
                },
                new Label
                {
                    Text = promo is null ? "No active promo" : $"Promo: {promo}",
                    FontSize = 14,
                    TextColor = Colors.DimGray
                },
                new Label
                {
                    Text = campaign is null ? "Direct catalog route" : $"Campaign query: {campaign}",
                    FontSize = 14,
                    TextColor = Colors.DimGray
                }
            }
        };
        Grid.SetColumn(details, 1);

        var tile = new Border
        {
            StrokeThickness = 1,
            Stroke = Colors.LightGray,
            Padding = 16,
            Content = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = 14,
                Children =
                {
                    swatch,
                    details
                }
            }
        };
        tile.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(async () =>
            {
                await _navigator.NavigateAsync(
                    CommerceRouteFactory.ProductDetail(_route.StoreId, productId, variant, promo, campaign),
                    NavigationRequestSource.InAppCommand);
            })
        });
        return tile;
    }

    private static Border ProductSwatch(int productId)
    {
        var swatch = new Border
        {
            WidthRequest = 56,
            HeightRequest = 56,
            StrokeThickness = 0,
            BackgroundColor = productId switch
            {
                123 => Colors.SteelBlue,
                456 => Colors.Black,
                _ => Colors.SeaGreen
            }
        };
        return swatch;
    }
}

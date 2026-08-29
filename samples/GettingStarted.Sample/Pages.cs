using AdamE.AppNav.Maui;
using AdamE.AppNav.Navigation;

namespace GettingStarted.Sample;

// #region getting-started-pages
[MauiRoutePage(typeof(HomeRoute))]
public sealed class HomePage : ContentPage
{
    public HomePage(HomeRoute route, IRouterNavigator navigator)
    {
        Title = "Home";
        var openDetail = new Button { Text = "Open detail" };
        openDetail.Clicked += async (_, _) =>
        {
            // #region getting-started-typed-navigation
            await navigator.NavigateAsync(new DetailRoute(42));
            // #endregion getting-started-typed-navigation
        };

        Content = new VerticalStackLayout
        {
            Padding = 24,
            Spacing = 16,
            Children =
            {
                new Label { Text = "Home", FontSize = 32 },
                openDetail
            }
        };
    }
}

[MauiRoutePage(typeof(DetailRoute))]
public sealed class DetailPage : ContentPage
{
    public DetailPage(DetailRoute route)
    {
        Title = "Detail";
        Content = new VerticalStackLayout
        {
            Padding = 24,
            Spacing = 16,
            Children =
            {
                new Label { Text = $"Item {route.ItemId}", FontSize = 32 },
                new Label { Text = "Use the native Back button or gesture to return Home." }
            }
        };
    }
}
// #endregion getting-started-pages

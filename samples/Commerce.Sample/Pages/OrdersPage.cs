using Commerce.Sample.Routes;

namespace Commerce.Sample.Pages;

public sealed class OrdersPage : ContentPage
{
    public OrdersPage(OrdersRoute route)
    {
        Title = "Orders";
        Content = new VerticalStackLayout
        {
            Padding = 24,
            Spacing = 12,
            Children =
            {
                new Label
                {
                    Text = $"Orders for {route.StoreId}",
                    FontSize = 26,
                    FontAttributes = FontAttributes.Bold
                },
                new Label
                {
                    Text = "The orders tab is a separate branch with its own stack.",
                    FontSize = 16
                }
            }
        };
    }
}

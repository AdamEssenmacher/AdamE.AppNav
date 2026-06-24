using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace AdamE.MauiRouter.Maui.Tests;

public sealed class TestApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new ContentPage
        {
            Content = new Label
            {
                Text = "AdamE.MauiRouter MAUI adapter platform tests",
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                TextColor = Colors.Black
            }
        });
    }
}

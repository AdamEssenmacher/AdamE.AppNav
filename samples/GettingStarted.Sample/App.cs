using AdamE.AppNav.Maui;

namespace GettingStarted.Sample;

public sealed class App(IAppNavStartupService startup) : Application
{
    // #region getting-started-window-start
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new ContentPage());
        startup.Start(window, "main");
        return window;
    }
    // #endregion getting-started-window-start
}

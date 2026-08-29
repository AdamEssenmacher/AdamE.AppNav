#if MACCATALYST
using Foundation;
using UIKit;

namespace AdamE.AppNav.Maui.Tests;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp()
    {
        return MauiProgram.CreateMauiApp();
    }
}
#endif

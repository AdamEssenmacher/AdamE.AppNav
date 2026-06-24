#if IOS
using Foundation;
using UIKit;

namespace AdamE.MauiRouter.Maui.Tests;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        AppleTestDiagnostics.Reset();
        AppleTestDiagnostics.Write("iOS AppDelegate.FinishedLaunching completed.");
        _ = RunTestsAsync();
        return true;
    }

    private static async Task RunTestsAsync()
    {
        try
        {
            AppleTestDiagnostics.Write("Invoking iOS XHarness test entrypoint.");
            var entryPoint = new XHarnessAppleTestEntryPoint();
            await entryPoint.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppleTestDiagnostics.Write(exception.ToString());
            Console.WriteLine(exception);
        }
    }
}
#endif

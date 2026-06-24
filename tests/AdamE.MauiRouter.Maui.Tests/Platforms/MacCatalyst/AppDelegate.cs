#if MACCATALYST
using Foundation;
using UIKit;

namespace AdamE.MauiRouter.Maui.Tests;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp()
    {
        return MauiProgram.CreateMauiApp();
    }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        var result = base.FinishedLaunching(application, launchOptions);
        AppleTestDiagnostics.Reset();
        AppleTestDiagnostics.Write("Mac Catalyst AppDelegate.FinishedLaunching completed.");
        _ = RunTestsAsync();
        return result;
    }

    private static async Task RunTestsAsync()
    {
        try
        {
            AppleTestDiagnostics.Write("Invoking Mac Catalyst XHarness test entrypoint.");
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

namespace AdamE.AppNav.iOS.NativeChecks;

internal static class Application
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
internal sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;
}

/// <summary>
/// Runs every check in the scene's own window, then reports: a line per check and a final PASS or FAIL to standard
/// output and to Library/Caches/appnav-ios-checks.txt, which eng/run-ios-native-checks.sh reads.
/// </summary>
[Register("SceneDelegate")]
internal sealed class SceneDelegate : UIWindowSceneDelegate
{
    public override UIWindow? Window { get; set; }

    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene)
            return;
        var window = new UIWindow(windowScene);
        Window = window;
        // Begun on the main thread before the window is shown: the first check's presenter reveals it.
        _ = CheckRunner.RunAsync(window);
    }
}

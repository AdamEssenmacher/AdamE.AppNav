namespace AdamE.AppNav.iOS.NativeChecks;

internal static class CheckRunner
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    public static async Task RunAsync(UIWindow window)
    {
        string run = Argument("--run") ?? "local";
        string report = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "..",
            "Library",
            "Caches",
            "appnav-ios-checks.txt");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(report) ?? throw new InvalidOperationException("No Caches."));
        using var deadline = new CancellationTokenSource(Deadline);
        CancellationToken token = deadline.Token;
        await File.WriteAllTextAsync(report, $"RUN {run}\n", CancellationToken.None);
        string? current = null;
        try
        {
            foreach ((string name, Func<UIWindow, CancellationToken, Task> check) in TopologyChecks.All
                         .Concat(PresenterChecks.All)
                         .Concat(LeaseChecks.All))
            {
                current = name;
                await check(window, token);
                await WriteAsync(report, $"OK {name}");
            }

            window.RootViewController = new UIViewController
            {
                View = new UILabel
                {
                    Text = "AppNav iOS checks passed",
                    TextAlignment = UITextAlignment.Center,
                    BackgroundColor = UIColor.SystemBackground,
                    TextColor = UIColor.Label
                }
            };
            window.MakeKeyAndVisible();
            await WriteAsync(report, $"PASS {run}");
        }
        catch (Exception failure)
        {
            string text = failure.ToString().ReplaceLineEndings("\n    ");
            await WriteAsync(report, $"FAIL {run} {current}: {text}");
        }
    }

    private static async Task WriteAsync(string report, string line)
    {
        Console.WriteLine($"APPNAV-IOS-CHECKS {line}");
        await File.AppendAllTextAsync(report, line + "\n", CancellationToken.None);
    }

    private static string? Argument(string name)
    {
        string[] args = NSProcessInfo.ProcessInfo.Arguments;
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

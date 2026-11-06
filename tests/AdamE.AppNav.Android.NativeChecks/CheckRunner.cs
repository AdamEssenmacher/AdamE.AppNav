using Android.Util;
using Android.Widget;
using AndroidX.Activity;

namespace AdamE.AppNav.Android.NativeChecks;

internal static class CheckRunner
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

    public static async Task RunAsync(ComponentActivity activity, string run)
    {
        string report = Path.Combine(
            activity.FilesDir?.AbsolutePath ?? throw new InvalidOperationException("No files directory."),
            "appnav-android-checks.txt");
        using var deadline = new CancellationTokenSource(Deadline);
        CancellationToken token = deadline.Token;
        await File.WriteAllTextAsync(report, $"RUN {run}\n", CancellationToken.None);
        string? current = null;
        try
        {
            foreach ((string name, Func<ComponentActivity, CancellationToken, Task> check) in TopologyChecks.All
                         .Concat(PresenterChecks.All)
                         .Concat(ModalChecks.All)
                         .Concat(ActivityChecks.All)
                         .Concat(LeaseChecks.All))
            {
                current = name;
                await check(activity, token);
                await WriteAsync(report, $"OK {name}");
            }

            activity.SetContentView(new TextView(activity) { Text = "AppNav Android checks passed" });
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
        Log.Info("APPNAV-ANDROID-CHECKS", line);
        await File.AppendAllTextAsync(report, line + "\n", CancellationToken.None);
    }
}

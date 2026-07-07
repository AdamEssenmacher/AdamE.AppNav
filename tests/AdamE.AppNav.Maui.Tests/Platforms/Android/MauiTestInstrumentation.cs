#if ANDROID
using Android.App;
using Android.OS;
using Android.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Application = Android.App.Application;

namespace AdamE.AppNav.Maui.Tests;

[Instrumentation(Name = "com.adame.appnav.maui.tests.AndroidMauiTestInstrumentation")]
public sealed class MauiTestInstrumentation : Instrumentation
{
    private readonly TaskCompletionSource _waitForApplication = new();

    public MauiTestInstrumentation(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public IServiceProvider Services { get; private set; } = null!;

    public override void CallApplicationOnCreate(Application? app)
    {
        base.CallApplicationOnCreate(app);

        if (app is null)
        {
            _waitForApplication.TrySetException(new ArgumentNullException(nameof(app)));
        }
        else
        {
            _waitForApplication.TrySetResult();
        }
    }

    public override void OnCreate(Bundle? arguments)
    {
        base.OnCreate(arguments);
        Start();
    }

    public override async void OnStart()
    {
        base.OnStart();

        await _waitForApplication.Task.ConfigureAwait(false);
        Services = IPlatformApplication.Current?.Services ?? Services;

        var bundle = await RunTestsAsync().ConfigureAwait(false);
        CopyResultsFile(bundle);
        Finish(Result.Ok, bundle);
    }

    private Task<Bundle> RunTestsAsync()
    {
        var runner = Services.GetRequiredService<HeadlessTestRunner>();
        return runner.RunTestsAsync();
    }

    private static void CopyResultsFile(Bundle bundle)
    {
        var resultsPath = bundle.GetString("test-results-path");
        if (string.IsNullOrWhiteSpace(resultsPath) || !File.Exists(resultsPath))
        {
            return;
        }

        var root = Application.Context.GetExternalFilesDir(null)?.AbsolutePath ??
                   Application.Context.CacheDir!.AbsolutePath;
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var finalPath = Path.Combine(directory, Path.GetFileName(resultsPath));
        File.Copy(resultsPath, finalPath, overwrite: true);
        bundle.PutString("test-results-path", finalPath);
    }
}
#endif

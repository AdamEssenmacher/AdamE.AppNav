using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Activity;

// Debuggable in every configuration: eng/run-android-native-checks.sh reads the report with run-as.
[assembly: Application(Debuggable = true)]

namespace AdamE.AppNav.Android.NativeChecks;

/// <summary>
/// Runs every check, then reports: a line per check and a final PASS or FAIL to logcat (tag APPNAV-ANDROID-CHECKS)
/// and to files/appnav-android-checks.txt, which eng/run-android-native-checks.sh reads. The run's ID comes in the
/// "run" extra.
/// </summary>
[Activity(Label = "AppNav Android checks", MainLauncher = true, Exported = true,
    Name = "com.adame.appnav.android.nativechecks.CheckActivity",
    Theme = "@android:style/Theme.Material.Light.NoActionBar",
    ConfigurationChanges = global::Android.Content.PM.ConfigChanges.ScreenSize
                           | global::Android.Content.PM.ConfigChanges.Orientation
                           | global::Android.Content.PM.ConfigChanges.ScreenLayout
                           | global::Android.Content.PM.ConfigChanges.SmallestScreenSize)]
public sealed class CheckActivity : ComponentActivity
{
    private static bool _started;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (_started)
            return;
        _started = true;
        _ = CheckRunner.RunAsync(this, Intent?.GetStringExtra("run") ?? "local");
    }
}

/// <summary>A second activity, started by a check to stand in for a recreated one.</summary>
[Activity(Label = "AppNav probe", Name = "com.adame.appnav.android.nativechecks.ProbeActivity",
    Theme = "@android:style/Theme.Material.Light.NoActionBar")]
public sealed class ProbeActivity : ComponentActivity
{
    private static TaskCompletionSource<ProbeActivity>? _created;

    public TaskCompletionSource Destroyed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Called from OnCreate, before anything else, for a check that attaches during creation.</summary>
    public static Action<ProbeActivity>? Creating { get; set; }

    /// <summary>Called from OnDestroy: where an application detaches.</summary>
    public Action<ProbeActivity>? Destroying { get; set; }

    /// <summary>Starts a probe activity from <paramref name="from"/> and returns it once it is created.</summary>
    public static async Task<ProbeActivity> StartAsync(Activity from, CancellationToken token)
    {
        var created = new TaskCompletionSource<ProbeActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
        _created = created;
        from.StartActivity(new Intent(from, typeof(ProbeActivity)));
        return await created.Task.WaitAsync(Checks.Soon, token);
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Creating?.Invoke(this);
        _ = Interlocked.Exchange(ref _created, null)?.TrySetResult(this);
    }

    protected override void OnDestroy()
    {
        Destroying?.Invoke(this);
        base.OnDestroy();
        _ = Destroyed.TrySetResult();
    }
}

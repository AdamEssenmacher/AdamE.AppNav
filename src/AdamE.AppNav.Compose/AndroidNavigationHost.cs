using AdamE.AppNav.Leases;
using AdamE.AppNav.Oahu;
using AdamE.AppNav.Oahu.Hosting;
using AdamE.AppNav.State;
using Android.OS;
using Android.Views;
using AndroidX.Activity;
using global::Oahu;
using global::Oahu.Android;

namespace AdamE.AppNav.Compose;

/// <summary>One Navigation 3 surface attachment and its OAHU hosting view.</summary>
public sealed class AndroidNavigationHost : IDisposable
{
    private static readonly Handler Main = new(Looper.MainLooper!);
    private readonly NavigationShellViewModel shell;
    private readonly OahuScreen screen;
    private readonly Action<Exception> reportFailure;
    private bool disposed;

    private AndroidNavigationHost(NavigationShellViewModel shell, OahuScreen screen, Action<Exception> reportFailure)
    {
        this.shell = shell;
        this.screen = screen;
        this.reportFailure = reportFailure;
        AndroidPortHost.Main.Faulted += reportFailure;
        AndroidPortHost.Main.MethodFailed += reportFailure;
    }

    public View View => screen.View;

    /// <summary>The epoch assigned to this activity's surface.</summary>
    public long Epoch => shell.Epoch;

    /// <summary>
    /// Creates a host on the activity's main thread. Install <see cref="View"/> into the activity, then dispose the
    /// host on the main thread during teardown. Disposal removes the view before closing its OAHU screen.
    /// The application owns the presenter and router lifetimes.
    /// </summary>
    public static AndroidNavigationHost Attach<TContract>(
        ComponentActivity activity,
        OahuNavigationPresenter presenter,
        RouteEntryLeaseRegistry leases,
        Func<RouteEntry, RouteEntryLease, OahuMount> mount,
        Func<INavigationShellViewModel, TContract> createContract,
        Action<Exception> reportFailure) where TContract : class =>
        Attach(activity, presenter, leases, (entry, lease, _) => new OahuMountedScreen(mount(entry, lease)),
            createContract, reportFailure);

    /// <summary>Attaches a host with a screen factory that can provide a native teardown callback.</summary>
    public static AndroidNavigationHost Attach<TContract>(
        ComponentActivity activity,
        OahuNavigationPresenter presenter,
        RouteEntryLeaseRegistry leases,
        OahuScreenFactory mount,
        Func<INavigationShellViewModel, TContract> createContract,
        Action<Exception> reportFailure) where TContract : class
    {
        EnsureMainThread();
        var shell = NavigationShellViewModel.Attach(presenter, leases, mount, DispatchAsync, reportFailure);
        try
        {
            var screen = OahuScreens.Create(activity, createContract(shell));
            return new AndroidNavigationHost(shell, screen, reportFailure);
        }
        catch
        {
            shell.Dispose();
            throw;
        }
    }

    /// <summary>Creates the UI-thread dispatcher and failure boundary for a router's lease registry.</summary>
    public static IRouteEntryLeaseHost CreateLeaseHost(Action<Exception> reportFailure) => new LeaseHost(reportFailure);

    /// <summary>Dispatches work to a later turn of the Android main looper.</summary>
    public static Task DispatchAsync(Action callback)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Main.Post(() =>
        {
            try
            {
                callback();
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }))
            completion.SetException(new InvalidOperationException("The Android main looper is unavailable."));
        return completion.Task;
    }

    /// <summary>Stops presenter work and native observations for this attachment.</summary>
    public void Detach() => shell.Detach();

    public void Dispose()
    {
        EnsureMainThread();
        if (disposed)
            return;

        disposed = true;
        Detach();
        AndroidPortHost.Main.Faulted -= reportFailure;
        AndroidPortHost.Main.MethodFailed -= reportFailure;
        if (screen.View.Parent is ViewGroup parent)
            parent.RemoveView(screen.View);
        try
        {
            screen.Dispose();
        }
        finally
        {
            shell.Dispose();
        }
    }

    private static void EnsureMainThread()
    {
        if (Looper.MainLooper?.IsCurrentThread != true)
            throw new InvalidOperationException("The Android navigation host must be used on the main thread.");
    }

    private sealed class LeaseHost(Action<Exception> reportFailure) : IRouteEntryLeaseHost
    {
        public async Task InvokeAsync(Func<ValueTask> work)
        {
            ValueTask pending = default;
            await DispatchAsync(() => { pending = work(); }).ConfigureAwait(false);
            await pending.ConfigureAwait(false);
        }

        public void ReportDefect(RouteEntryLeaseReleaseException error) => reportFailure(error);
        public void ReportFatal(RouteEntryLeaseReleaseException error) => reportFailure(error);
    }
}

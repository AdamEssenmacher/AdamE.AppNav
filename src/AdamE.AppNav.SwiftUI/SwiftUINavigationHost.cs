using AdamE.AppNav.Leases;
using AdamE.AppNav.Oahu;
using AdamE.AppNav.Oahu.Hosting;
using AdamE.AppNav.State;
using CoreFoundation;
using Foundation;
using global::Oahu;
using global::Oahu.iOS;
using UIKit;

namespace AdamE.AppNav.SwiftUI;

/// <summary>Owns the managed attachment and the SwiftUI hosting controller for one iOS scene.</summary>
public sealed class SwiftUINavigationHost : IDisposable
{
    private readonly NavigationShellViewModel navigation;
    private readonly OahuScreen screen;
    private bool disposed;

    private SwiftUINavigationHost(NavigationShellViewModel navigation, OahuScreen screen)
    {
        this.navigation = navigation;
        this.screen = screen;
    }

    public UIViewController ViewController => screen.ViewController;

    /// <summary>Hosts the default NavigationShell OAHU contract.</summary>
    public static SwiftUINavigationHost Attach(
        OahuNavigationPresenter presenter, RouteEntryLeaseRegistry leases,
        OahuScreenFactory mountEntry, Action<Exception> reportFailure) =>
        Attach<INavigationShellViewModel>(presenter, leases, mountEntry, static shell => shell, reportFailure);

    public static SwiftUINavigationHost Attach<TContract>(
        OahuNavigationPresenter presenter, RouteEntryLeaseRegistry leases,
        Func<RouteEntry, RouteEntryLease, OahuMount> mountEntry,
        Func<NavigationShellViewModel, TContract> createShell, Action<Exception> reportFailure) where TContract : class =>
        Attach(presenter, leases, (entry, lease, _) => new OahuMountedScreen(mountEntry(entry, lease)),
            createShell, reportFailure);

    /// <summary>
    /// Mounts the app's shell contract on the UI thread. Initialize OahuScreens before attaching.
    /// Create the router with the same leases, then start navigation or refresh after recreating the host.
    /// </summary>
    public static SwiftUINavigationHost Attach<TContract>(
        OahuNavigationPresenter presenter, RouteEntryLeaseRegistry leases,
        OahuScreenFactory mountEntry,
        Func<NavigationShellViewModel, TContract> createShell, Action<Exception> reportFailure) where TContract : class
    {
        EnsureMainThread();
        var navigation = NavigationShellViewModel.Attach(presenter, leases, mountEntry, PostAsync, reportFailure);
        try
        {
            var screen = OahuScreens.Create<TContract>(createShell(navigation));
            return new SwiftUINavigationHost(navigation, screen);
        }
        catch
        {
            navigation.Dispose();
            throw;
        }
    }

    /// <summary>Stops pending observations and presenter work before the scene removes the hosting controller.</summary>
    public void Detach()
    {
        EnsureMainThread();
        navigation.Detach();
    }

    /// <summary>Disposes the shell and mounted destinations after the hosting controller is removed.</summary>
    public void Dispose()
    {
        if (disposed) return;
        EnsureMainThread();
        disposed = true;
        try { navigation.Detach(); }
        finally
        {
            try { screen.Dispose(); }
            finally { navigation.Dispose(); }
        }
    }

    private static void EnsureMainThread()
    {
        if (!NSThread.IsMain)
            throw new InvalidOperationException("The SwiftUI navigation host must be used on the main thread.");
    }

    /// <summary>Posts work to a later main-loop turn, including work originating in OAHU callbacks.</summary>
    public static Task PostAsync(Action work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            try { work(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        });
        return completion.Task;
    }
}

/// <summary>Runs lease retirement work on the main thread.</summary>
public sealed class SwiftUIRouteEntryLeaseHost(
    Action<RouteEntryLeaseReleaseException> reportDefect,
    Action<RouteEntryLeaseReleaseException> reportFatal) : IRouteEntryLeaseHost
{
    public async Task InvokeAsync(Func<ValueTask> work)
    {
        ValueTask release = default;
        await SwiftUINavigationHost.PostAsync(() => release = work()).ConfigureAwait(false);
        await release.ConfigureAwait(false);
    }

    public void ReportDefect(RouteEntryLeaseReleaseException defect) => reportDefect(defect);
    public void ReportFatal(RouteEntryLeaseReleaseException failure) => reportFatal(failure);
}

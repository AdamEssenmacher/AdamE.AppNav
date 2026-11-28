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
        UIViewController? controller = null;
        // The SwiftUI shell presents a split at the window root with NavigationSplitView, whose compact-column binding
        // needs iOS 17. Earlier releases refuse splits before anything is staged, whatever the package floor.
        var navigation = NavigationShellViewModel.Attach(presenter, leases, mountEntry, PostAsync, reportFailure,
            presentsSplits: OperatingSystem.IsIOSVersionAtLeast(17),
            waitUntilReady: token => WaitForCoveredViewAsync(() => controller, token));
        try
        {
            var screen = OahuScreens.Create<TContract>(createShell(navigation));
            controller = screen.ViewController;
            return new SwiftUINavigationHost(navigation, screen);
        }
        catch
        {
            navigation.Dispose();
            throw;
        }
    }

    private static async Task WaitForCoveredViewAsync(Func<UIViewController?> getController, CancellationToken token)
    {
        WindowAttachmentView? observer = null;
        try
        {
            await PostAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                if (CoveredView(getController()) is not { } view) return;
                observer = new WindowAttachmentView { UserInteractionEnabled = false };
                view.AddSubview(observer);
            }).ConfigureAwait(false);
            if (observer is { } waiting)
                await waiting.Attached.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            if (observer is { } waiting)
                await PostAsync(() =>
                {
                    waiting.RemoveFromSuperview();
                    waiting.Dispose();
                }).ConfigureAwait(false);
        }
    }

    // UIKit removes the presenter's view for .fullScreen. Confirm that a presentation in the
    // hosting controller's containment chain accounts for the missing window before waiting.
    private static UIView? CoveredView(UIViewController? controller)
    {
        if (controller?.ViewIfLoaded is not { Window: null } view) return null;
        for (var ancestor = controller; ancestor is not null; ancestor = ancestor.ParentViewController)
        {
            if (ancestor.PresentedViewController is not { } cover ||
                cover.PresentingViewController != ancestor ||
                cover.PresentationController?.PresentationStyle != UIModalPresentationStyle.FullScreen)
                continue;
            for (var presented = cover; presented is not null; presented = presented.PresentedViewController)
                if (presented.ViewIfLoaded?.Window is not null)
                    return view;
        }
        return null;
    }

    private sealed class WindowAttachmentView : UIView
    {
        public TaskCompletionSource Attached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void MovedToWindow()
        {
            base.MovedToWindow();
            if (Window is not null) Attached.TrySetResult();
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

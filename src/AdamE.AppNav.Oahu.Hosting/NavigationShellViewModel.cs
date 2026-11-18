using AdamE.AppNav.Leases;
using AdamE.AppNav.State;
using global::Oahu;

namespace AdamE.AppNav.Oahu.Hosting;

/// <summary>
/// Creates a new screen for an entry on the UI thread. The adapter opened its lease during this presentation
/// when <paramref name="opened"/> is true. The factory owns cleanup for anything it creates before returning.
/// </summary>
public delegate OahuMountedScreen OahuScreenFactory(RouteEntry entry, RouteEntryLease lease, bool opened);

/// <summary>An OAHU mount and optional cleanup for resources owned by this native attachment.</summary>
public sealed record OahuMountedScreen(OahuMount Mount, Action? AfterTeardown = null);

/// <summary>
/// Mounts the application's route entries for an attached native shell and holds their lease attachments
/// until verified removal or host teardown. Use one instance per native surface attachment.
/// </summary>
/// <param name="leases">The registry configured on the router.</param>
/// <param name="mount">Creates an application screen on the UI thread. Register entry-owned resources on the lease.</param>
/// <param name="dispatch">Posts to a later UI turn and returns a task that completes after the action.</param>
/// <param name="observe">Forwards a native observation to the presenter using this surface's attachment epoch.</param>
/// <param name="reportFailure">Receives asynchronous observation failures. It may be called off the UI thread.</param>
public sealed class NavigationShellViewModel(
    RouteEntryLeaseRegistry leases,
    OahuScreenFactory mount,
    Func<Action, Task> dispatch,
    Func<NativeObservation, Task> observe,
    Action<Exception> reportFailure) : INavigationShellViewModel, IOahuNavigationSurface, IDisposable, IAsyncDisposable
{
    private readonly MountedNavigationSurface surface = new(leases, mount);
    private bool disposed;
    private Task? teardown;
    private Action? detach;

    /// <summary>The callback epoch assigned by Attach, or zero for manually wired instances.</summary>
    public long Epoch { get; private set; }

    public NavigationShellViewModel(RouteEntryLeaseRegistry leases,
        Func<RouteEntry, RouteEntryLease, OahuMount> mount, Func<Action, Task> dispatch,
        Func<NativeObservation, Task> observe, Action<Exception> reportFailure)
        : this(leases, (entry, lease, _) => new OahuMountedScreen(mount(entry, lease)),
            dispatch, observe, reportFailure)
    {
    }

    public static NavigationShellViewModel Attach(
        OahuNavigationPresenter presenter, RouteEntryLeaseRegistry leases,
        Func<RouteEntry, RouteEntryLease, OahuMount> mount, Func<Action, Task> dispatch,
        Action<Exception> reportFailure) =>
        Attach(presenter, leases, (entry, lease, _) => new OahuMountedScreen(mount(entry, lease)),
            dispatch, reportFailure);

    /// <summary>Creates a shell, attaches it to the presenter, and fences observations to its epoch.</summary>
    public static NavigationShellViewModel Attach(
        OahuNavigationPresenter presenter, RouteEntryLeaseRegistry leases,
        OahuScreenFactory mount, Func<Action, Task> dispatch, Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        long epoch = 0;
        var shell = new NavigationShellViewModel(leases, mount, dispatch, reading =>
            presenter.ObserveAsync(epoch, reading.Revision, reading.Branch, reading.Entries,
                reading.ModalId.Length == 0 ? null : reading.ModalId, reading.ModalEntries),
            reportFailure);
        shell.Epoch = epoch = presenter.Attach(shell);
        shell.detach = () => presenter.Detach(epoch);
        return shell;
    }

    /// <summary>Stops presenter work for this host before the native UI is removed.</summary>
    public void Detach() => Interlocked.Exchange(ref detach, null)?.Invoke();

    public IOahuState<ShellTarget> Target => surface.Target;

    public Task ShowAsync(OahuNavigationTarget next, CancellationToken cancellationToken) => dispatch(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        surface.Stage(next, cancellationToken);
    });

    public Task AcceptAsync(OahuNavigationTarget accepted, CancellationToken cancellationToken) => dispatch(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed) return;
        surface.Accept(accepted);
    });

    public async void Report(NativeObservation observation)
    {
        try
        {
            Task operation = Task.CompletedTask;
            await dispatch(() =>
            {
                if (!disposed)
                    operation = observe(observation);
            }).ConfigureAwait(false);
            await operation.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            reportFailure(error);
        }
    }

    /// <summary>Disconnects immediately and releases remaining mounts on a fresh UI turn.</summary>
    public void Dispose()
    {
        if (disposed) return;
        ObserveTeardown(DisposeAsync().AsTask());
    }

    private async void ObserveTeardown(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) { reportFailure(error); }
    }

    /// <summary>Waits for final screen cleanup after the native hosting view has been removed.</summary>
    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            Detach();
            teardown = dispatch(surface.ReleaseAll);
        }
        return new ValueTask(teardown ?? Task.CompletedTask);
    }
}

using AdamE.AppNav.Leases;
using AdamE.AppNav.State;
using global::Oahu;

namespace AdamE.AppNav.Oahu.Hosting;

/// <summary>
/// Creates a new screen for an entry on the UI thread. The adapter opened its lease during this presentation
/// when <paramref name="opened"/> is true. The factory owns cleanup for anything it creates before returning.
/// </summary>
public delegate OahuMountedScreen OahuScreenFactory(RouteEntry entry, RouteEntryLease lease, bool opened);

/// <summary>An OAHU mount and optional cleanup for resources owned by this native attachment.
/// If cleanup throws, a later release can call it again. It must be safe to resume after a partial failure.</summary>
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
    private readonly MountedNavigationSurface surface = new(leases, mount, reportFailure);
    private volatile bool disposed;
    private int disposing;
    private Task? teardown;
    private Action? detach;

    /// <summary>Whether the native shell presents a split at the window root. Set it before attaching; the presenter
    /// refuses splits for a shell that does not.</summary>
    public bool PresentsSplits { get; init; }

    /// <summary>The callback epoch assigned by Attach, or zero for manually wired instances.</summary>
    public long Epoch { get; private set; }

    private static OahuScreenFactory Adapt(Func<RouteEntry, RouteEntryLease, OahuMount> factory) =>
        (entry, lease, _) => new OahuMountedScreen(factory(entry, lease));

    public NavigationShellViewModel(RouteEntryLeaseRegistry leases,
        Func<RouteEntry, RouteEntryLease, OahuMount> mount, Func<Action, Task> dispatch,
        Func<NativeObservation, Task> observe, Action<Exception> reportFailure)
        : this(leases, Adapt(mount),
            dispatch, observe, reportFailure)
    {
    }

    public static NavigationShellViewModel Attach(
        OahuNavigationPresenter presenter, RouteEntryLeaseRegistry leases,
        Func<RouteEntry, RouteEntryLease, OahuMount> mount, Func<Action, Task> dispatch,
        Action<Exception> reportFailure, bool presentsSplits = false) =>
        Attach(presenter, leases, Adapt(mount),
            dispatch, reportFailure, presentsSplits);

    /// <summary>Creates a shell, attaches it to the presenter, and fences observations to its epoch.</summary>
    /// <param name="presentsSplits">Whether the native shell presents a split at the window root.</param>
    public static NavigationShellViewModel Attach(
        OahuNavigationPresenter presenter, RouteEntryLeaseRegistry leases,
        OahuScreenFactory mount, Func<Action, Task> dispatch, Action<Exception> reportFailure,
        bool presentsSplits = false)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        long epoch = 0;
        var shell = new NavigationShellViewModel(leases, mount, dispatch, reading =>
            presenter.ObserveAsync(epoch, reading.Revision, reading.Branch, reading.Entries,
                reading.ModalId.Length == 0 ? null : reading.ModalId, reading.ModalEntries,
                reading.SecondaryEntries, ParseCompactColumn(reading.CompactColumn)),
            reportFailure) { PresentsSplits = presentsSplits };
        shell.Epoch = epoch = presenter.Attach(shell);
        shell.detach = () => presenter.Detach(epoch);
        return shell;
    }

    private static SplitColumn? ParseCompactColumn(string column) => column switch
    {
        "" => null,
        "primary" => SplitColumn.Primary,
        "secondary" => SplitColumn.Secondary,
        _ => throw new ArgumentException($"Unknown split column '{column}'.", nameof(column))
    };

    /// <summary>Stops presenter work for this host before the native UI is removed.</summary>
    public void Detach() => Interlocked.Exchange(ref detach, null)?.Invoke();

    public IOahuState<ShellTarget> Target => surface.Target;

    public Task ShowAsync(OahuNavigationTarget next, CancellationToken cancellationToken) => dispatch(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        surface.Stage(next, cancellationToken);
    });

    public Task AcceptAsync(OahuNavigationTarget accepted, Func<Action, bool> applyIfCurrent, CancellationToken cancellationToken) => dispatch(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Returning without applying would report a false acceptance: the presenter treats a completed
        // AcceptAsync as proof that the target was published.
        ObjectDisposedException.ThrowIf(disposed, this);
        surface.Accept(accepted, applyIfCurrent);
    });

    public Task SettleAsync(OahuNavigationTarget committed, CancellationToken cancellationToken) => dispatch(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!disposed)
            surface.Settle(committed);
    });

    public async Task Report(NativeObservation observation)
    {
        try
        {
            Task operation = Task.CompletedTask;
            await dispatch(() =>
            {
                if (disposed)
                    return;
                // A native screen that does not map the split wire would report a split without its columns,
                // which never matches, and the presentation would stall until it timed out.
                if (observation.CompactColumn.Length == 0 && surface.Target.Current is { Split.SplitId.Length: > 0 } staged &&
                    staged.Revision == observation.Revision)
                    throw new InvalidOperationException(
                        $"The native shell reported revision {observation.Revision} of split '{staged.Split.SplitId}' " +
                        "without its split columns. Map ShellTarget.Split into the native projection and report the " +
                        "reading's SecondaryEntries and CompactColumn.");
                operation = observe(observation);
            }).ConfigureAwait(false);
            await operation.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ReportFailure(error);
        }
    }

    /// <summary>Disconnects immediately and releases remaining mounts on a fresh UI turn.</summary>
    public void Dispose()
    {
        if (Volatile.Read(ref disposing) == 0)
            _ = ObserveTeardownAsync(DisposeAsync().AsTask());
    }

    private async Task ObserveTeardownAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error)
        {
            ReportFailure(error);
        }
    }

    private void ReportFailure(Exception error)
    {
        try { reportFailure(error); }
        catch (Exception reportingFailure) { System.Diagnostics.Trace.TraceError("{0}", reportingFailure); }
    }

    /// <summary>Waits for final screen cleanup after the native hosting view has been removed.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposing, 1) == 0)
        {
            // Detach before marking this shell disposed: once the presenter has dropped the attachment, an
            // in-flight presentation sees a detached host and replays on the next one instead of failing
            // against a disposed shell.
            try { Detach(); }
            finally
            {
                disposed = true;
                teardown = dispatch(surface.ReleaseAll);
            }
        }
        else if (teardown is { IsFaulted: true })
            teardown = dispatch(surface.ReleaseAll);
        return new ValueTask(teardown ?? Task.CompletedTask);
    }
}

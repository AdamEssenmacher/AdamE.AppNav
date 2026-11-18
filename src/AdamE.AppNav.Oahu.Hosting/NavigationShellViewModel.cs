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
    private readonly record struct MountKey(string Id, AppRoute Route);

    private sealed record ScreenMount(OahuMountedScreen Screen, RouteEntryLeaseAttachment Attachment)
    {
        public long Id => Screen.Mount.Id;

        public void Release()
        {
            // OAHU invokes its callback even when native close fails. Only a successful return
            // on a later UI turn certifies that close succeeded and the borrower is gone.
            var closeRan = false;
            Screen.Mount.Dispose(() => closeRan = true);
            if (!closeRan)
                throw new InvalidOperationException("Mount teardown must run outside an OAHU callback.");
            Screen.AfterTeardown?.Invoke();
            Attachment.Detach();
        }
    }

    private readonly StateSource<ShellTarget> target = new(new(0, "", false, [], "", [], new("initial", "", "", "", "", "")));
    private readonly Dictionary<MountKey, ScreenMount> mounts = new();
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

    public IOahuState<ShellTarget> Target => target;

    public Task ShowAsync(OahuNavigationTarget next, CancellationToken cancellationToken) => dispatch(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (next.ModalOwner is { } owner && next.ModalEntries.All(entry => entry.Id != owner.Id))
            GetLease(owner, out _);
        var prepared = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in DisplayedEntries(next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            prepared.Add(entry.Id, GetOrMount(entry));
        }
        // A factory may retire its lease, or another entry's, while building screens. Resolve
        // mount IDs again before publishing so a prepared screen cannot spend revoked authority.
        var staged = ToWire(next, entry => ResolveVisibleMount(entry, prepared[entry.Id]));
        if (next.ModalOwner is { } modalOwner) RequireCurrentLease(modalOwner);
        cancellationToken.ThrowIfCancellationRequested();
        target.Set(staged);
    });

    public Task AcceptAsync(OahuNavigationTarget accepted, CancellationToken cancellationToken) => dispatch(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed) return;
        var current = target.Current;
        var currentMounts = ScreenEntries(current).ToDictionary(e => e.EntryId, e => e.Mount, StringComparer.Ordinal);
        ValidateAcceptedLeases(accepted, currentMounts);
        if (current.Revision == accepted.Revision)
        {
            // A same-revision change confirms a native Back or tab selection. Keep the exact mounts
            // already shown, including any placeholders used for retired entries.
            target.Set(ToWire(accepted, entry => currentMounts[entry.Id]));
            ReleaseExcept(ScreenEntries(target.Current).Select(entry => entry.Mount).ToHashSet());
        }
        else
        {
            var retained = accepted.Branches.Values.SelectMany(stack => stack).Concat(accepted.ModalEntries)
                .Select(entry => new MountKey(entry.Id, entry.Route))
                .Where(mounts.ContainsKey).Select(key => mounts[key].Id).ToHashSet();
            retained.UnionWith(currentMounts.Values);
            ReleaseExcept(retained);
        }
        ValidateAcceptedLeases(accepted, currentMounts);
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
            teardown = dispatch(() => ReleaseExcept([]));
        }
        return new ValueTask(teardown ?? Task.CompletedTask);
    }

    private RouteEntryLease GetLease(RouteEntry entry, out bool opened)
    {
        var lease = leases.GetOrOpen(entry.Id, out opened);
        ValidateLease(lease);
        return lease;
    }

    private RouteEntryLease RequireCurrentLease(RouteEntry entry)
    {
        if (!leases.TryGet(entry.Id, out var lease))
            throw new OperationCanceledException($"Route entry '{entry.Id}' lost its lease during presentation.");
        ValidateLease(lease);
        return lease;
    }

    private static void ValidateLease(RouteEntryLease lease)
    {
        if (lease.IsRetired && !lease.IsCommitted)
            throw new OperationCanceledException($"Route entry '{lease.EntryId}' retired during presentation.");
    }

    private long ResolveVisibleMount(RouteEntry entry, long prepared) =>
        RequireCurrentLease(entry).IsRetired ? 0 : prepared;

    private void ValidateAcceptedLeases(OahuNavigationTarget accepted, IReadOnlyDictionary<string, long> current)
    {
        foreach (var entry in DisplayedEntries(accepted))
        {
            var mountId = current.GetValueOrDefault(entry.Id);
            if (ResolveVisibleMount(entry, mountId) != mountId)
                throw new OperationCanceledException($"The mounted route entry '{entry.Id}' retired before acceptance.");
        }
        if (accepted.ModalOwner is { } owner) RequireCurrentLease(owner);
    }

    private long GetOrMount(RouteEntry entry)
    {
        var lease = GetLease(entry, out var opened);
        // A retired committed entry can still belong to the graph, but cannot acquire another attachment.
        if (lease.IsRetired) return 0;
        var key = new MountKey(entry.Id, entry.Route);
        if (mounts.TryGetValue(key, out var existing)) return existing.Id;
        if (!lease.TryAttach(out var attachment))
        {
            if (lease.IsCommitted) return 0;
            throw new OperationCanceledException($"Route entry '{entry.Id}' retired before it was mounted.");
        }
        try
        {
            var screen = mount(entry, lease, opened);
            if (screen?.Mount is not { IsDisposed: false } handle || mounts.Values.Any(existing => existing.Id == handle.Id))
                throw new InvalidOperationException($"The screen factory must return a fresh mount for '{entry.Id}'.");
            mounts.Add(key, new(screen, attachment));
            return handle.Id;
        }
        catch
        {
            attachment.Detach();
            throw;
        }
    }

    private void ReleaseExcept(HashSet<long> retained)
    {
        List<Exception> failures = [];
        foreach (var id in mounts.Where(pair => !retained.Contains(pair.Value.Id)).Select(pair => pair.Key).ToArray())
        {
            var screen = mounts[id];
            mounts.Remove(id);
            try { screen.Release(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count > 0)
            throw new AggregateException("Navigation mount teardown failed. Affected lease attachments remain held.", failures);
    }

    private static IEnumerable<ScreenEntry> ScreenEntries(ShellTarget value) =>
        value.Branches.SelectMany(stack => stack.Entries).Concat(value.ModalEntries);

    private static IEnumerable<RouteEntry> DisplayedEntries(OahuNavigationTarget value) =>
        value.Branches.Values.SelectMany(stack => stack).Concat(value.ModalEntries);

    private static ShellTarget ToWire(OahuNavigationTarget value, Func<RouteEntry, long> mountId)
    {
        ScreenEntry Entry(RouteEntry entry) => new(entry.Id, mountId(entry));
        var context = value.Transition;
        return new(value.Revision, value.Branch, value.IsBranchHost,
            value.Branches.Select(branch => new BranchStack(
                branch.Key, value.BranchTitles[branch.Key], branch.Value.Select(Entry).ToArray())).ToArray(),
            value.ModalId ?? "", value.ModalEntries.Select(Entry).ToArray(),
            new(context.Operation, context.SourceEntryId, context.DestinationEntryId, context.SourceKey,
                context.SourceScreen, context.DestinationScreen));
    }
}

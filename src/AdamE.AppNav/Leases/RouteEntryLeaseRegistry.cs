using System.Diagnostics.CodeAnalysis;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Leases;

/// <summary>
/// Holds the current lease of each leased route entry, and retires and releases leases as the router commits,
/// abandons, and shuts down.
/// </summary>
/// <remarks>
/// One registry serves one router: pass it through <c>RouterNavigatorFactoryOptions.EntryLeases</c> and give the same
/// instance to the presenter. Open leases on the application's UI thread before sending the request that presents
/// the entry.
/// </remarks>
public sealed class RouteEntryLeaseRegistry
{
    private readonly IRouteEntryLeaseHost _host;
    private readonly Dictionary<string, RouteEntryLease> _current = new(StringComparer.Ordinal);
    private readonly HashSet<RouteEntryLease> _unsettled = new(ReferenceEqualityComparer.Instance);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HashSet<string> _committedIds = new(StringComparer.Ordinal);
    private bool _bound;
    private bool _sealed;

    public RouteEntryLeaseRegistry(IRouteEntryLeaseHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    internal Lock Gate { get; } = new();

    /// <summary>Opens the lease of a new occurrence of <paramref name="entryId"/>.</summary>
    /// <param name="entryId">The route-entry ID the request will present.</param>
    /// <param name="parent">A lease that must outlive this one, or <see langword="null"/>.</param>
    /// <exception cref="InvalidOperationException">The entry ID already has a current lease.</exception>
    /// <exception cref="ObjectDisposedException">The router has shut down.</exception>
    public RouteEntryLease Open(string entryId, RouteEntryLease? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        RouteEntryLease lease;
        lock (Gate)
        {
            if (_current.ContainsKey(entryId))
            {
                throw new InvalidOperationException(
                    $"Route entry '{entryId}' already has a current lease. Use a new entry ID for a new occurrence.");
            }

            lease = OpenLocked(entryId, parent);
        }

        RetireIfBornRetired(lease);
        return lease;
    }

    /// <summary>Returns the current lease of <paramref name="entryId"/>, opening one when there is none.</summary>
    /// <remarks>
    /// For adapters that build per-entry resources during presentation. The returned lease may be retired; when it is
    /// also committed, present a placeholder for the entry.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The router has shut down.</exception>
    public RouteEntryLease GetOrOpen(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        lock (Gate)
        {
            if (_current.TryGetValue(entryId, out RouteEntryLease? existing))
                return existing;

            return OpenLocked(entryId, null);
        }
    }

    /// <summary>Gets the current lease of <paramref name="entryId"/>, if it has one.</summary>
    public bool TryGet(string entryId, [NotNullWhen(true)] out RouteEntryLease? lease)
    {
        ArgumentNullException.ThrowIfNull(entryId);
        lock (Gate)
        {
            return _current.TryGetValue(entryId, out lease);
        }
    }

    internal RouteEntryLease OpenChild(RouteEntryLease parent)
    {
        RouteEntryLease lease;
        lock (Gate)
        {
            lease = OpenLocked(null, parent);
        }

        RetireIfBornRetired(lease);
        return lease;
    }

    internal void Bind(NavigationState initialState)
    {
        lock (Gate)
        {
            if (_bound)
                throw new InvalidOperationException("A route-entry lease registry serves exactly one router.");

            _bound = true;
            CommitLocked(EntryIds(initialState), []);
        }
    }

    internal void Commit(NavigationState state)
    {
        HashSet<string> ids = EntryIds(state);
        var dropped = new List<RouteEntryLease>();
        lock (Gate)
        {
            CommitLocked(ids, dropped);
        }

        Retire(dropped, RouteEntryLeaseRetirement.Dropped);
    }

    internal void Abandon(NavigationState target, NavigationState committed)
    {
        HashSet<string> targetIds = EntryIds(target);
        HashSet<string> committedIds = EntryIds(committed);
        var abandoned = new List<RouteEntryLease>();
        lock (Gate)
        {
            foreach ((string entryId, RouteEntryLease lease) in _current.ToArray())
            {
                if (lease.IsCommittedLocked || !targetIds.Contains(entryId) || committedIds.Contains(entryId))
                    continue;

                abandoned.Add(lease);
                _current.Remove(entryId);
            }
        }

        Retire(abandoned, RouteEntryLeaseRetirement.Abandoned);
    }

    internal Task ShutdownAsync()
    {
        RouteEntryLease[] leases;
        lock (Gate)
        {
            if (!_sealed)
            {
                _sealed = true;
                _current.Clear();
            }

            leases = _unsettled.ToArray();
            if (leases.Length == 0)
                _drained.TrySetResult();
        }

        Retire(leases, RouteEntryLeaseRetirement.Shutdown);
        return _drained.Task;
    }

    internal void Retire(IReadOnlyCollection<RouteEntryLease> leases, RouteEntryLeaseRetirement reason)
    {
        if (leases.Count == 0)
            return;

        var retired = new List<RouteEntryLease>();
        lock (Gate)
        {
            foreach (RouteEntryLease lease in leases)
                lease.RetireLocked(reason, retired);
        }

        foreach (RouteEntryLease lease in retired)
            _ = ObserveCancellationAsync(lease.CancelRetirementAsync());

        // Descendants were added after their ancestors; release can only start bottom-up.
        for (int index = retired.Count - 1; index >= 0; index--)
            TryStartRelease(retired[index]);
    }

    internal void StartRelease(RouteEntryLease lease)
    {
        _ = ReleaseAsync(lease);
    }

    private RouteEntryLease OpenLocked(string? entryId, RouteEntryLease? parent)
    {
        ObjectDisposedException.ThrowIf(_sealed, this);
        if (parent is not null && !ReferenceEquals(parent.Registry, this))
            throw new ArgumentException("The parent lease belongs to another registry.", nameof(parent));

        var lease = new RouteEntryLease(
            this,
            entryId,
            parent,
            entryId is not null && _committedIds.Contains(entryId));
        parent?.AddChildLocked(lease);
        if (entryId is not null)
            _current.Add(entryId, lease);
        _unsettled.Add(lease);
        return lease;
    }

    private void RetireIfBornRetired(RouteEntryLease lease)
    {
        if (lease.Parent is { } parent && parent.IsRetired)
            Retire([lease], RouteEntryLeaseRetirement.Parent);
    }

    private void CommitLocked(HashSet<string> ids, List<RouteEntryLease> dropped)
    {
        _committedIds = ids;
        foreach ((string entryId, RouteEntryLease lease) in _current.ToArray())
        {
            if (ids.Contains(entryId))
            {
                lease.MarkCommittedLocked();
            }
            else if (lease.IsCommittedLocked)
            {
                dropped.Add(lease);
                _current.Remove(entryId);
            }
        }
    }

    private void TryStartRelease(RouteEntryLease lease)
    {
        bool release;
        lock (Gate)
        {
            release = lease.TryBeginReleaseLocked();
        }

        if (release)
            StartRelease(lease);
    }

    private async Task ReleaseAsync(RouteEntryLease lease)
    {
        try
        {
            await _host.InvokeAsync(lease.DisposeResourcesAsync).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fault(lease, ex);
            return;
        }

        RouteEntryLease? parent = lease.Parent;
        lock (Gate)
        {
            lease.CompleteReleaseLocked();
            // A committed occurrence stays current, so presentation still sees it retired until a commit drops it.
            if (lease.EntryId is { } entryId &&
                !lease.IsCommittedLocked &&
                _current.TryGetValue(entryId, out RouteEntryLease? current) &&
                ReferenceEquals(current, lease))
            {
                _current.Remove(entryId);
            }

            SettleLocked([lease]);
        }

        lease.SignalReleased();
        if (parent is not null)
            TryStartRelease(parent);
    }

    private void Fault(RouteEntryLease lease, Exception exception)
    {
        lock (Gate)
        {
            var settled = new List<RouteEntryLease>();
            lease.FaultLocked(settled);
            SettleLocked(settled);
        }

        var failure = new RouteEntryLeaseReleaseException(
            lease.EntryId,
            lease.EntryId is null
                ? "A child route-entry lease could not be released safely; its resources were kept."
                : $"The lease of route entry '{lease.EntryId}' could not be released safely; its resources were kept.",
            exception);
        try
        {
            _host.ReportFatal(failure);
        }
        catch
        {
            // The host's fatal boundary owns the failure; a failing reporter must not resurrect the lease.
        }
    }

    private void SettleLocked(IEnumerable<RouteEntryLease> leases)
    {
        foreach (RouteEntryLease lease in leases)
            _unsettled.Remove(lease);

        if (_sealed && _unsettled.Count == 0)
            _drained.TrySetResult();
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch
        {
            // Retirement callbacks belong to their registrants; they cannot stop retirement.
        }
    }

    internal static HashSet<string> EntryIds(NavigationState state)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (WindowNode window in state.Windows)
        {
            if (window.Root is not null)
                AddEntryIds(window.Root, ids);
            foreach (ModalNode modal in window.Modals)
                AddEntryIds(modal, ids);
        }

        return ids;
    }

    private static void AddEntryIds(NavigationNode node, HashSet<string> ids)
    {
        switch (node)
        {
            case StackNode stack:
                foreach (RouteEntry entry in stack.Entries)
                    ids.Add(entry.Id);
                break;
            case BranchHostNode branchHost:
                foreach (NavigationBranch branch in branchHost.Branches)
                    AddEntryIds(branch.Content, ids);
                break;
            case ModalNode modal:
                ids.Add(modal.RouteEntry.Id);
                if (modal.Content is not null)
                    AddEntryIds(modal.Content, ids);
                break;
        }
    }
}

using System.Diagnostics.CodeAnalysis;

namespace AdamE.AppNav.Leases;

/// <summary>
/// Owns what one occurrence of a route entry borrows, and releases it once core has retired the occurrence and no
/// native artifact, child lease, or tracked preparation still needs it.
/// </summary>
/// <remarks>
/// Open leases with <see cref="RouteEntryLeaseRegistry"/>. See the entry-leases design note for the full contract.
/// </remarks>
public sealed class RouteEntryLease
{
    private readonly RouteEntryLeaseRegistry _registry;
    private readonly CancellationTokenSource _retirement = new();
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<object> _resources = [];
    private readonly List<RouteEntryLease> _children = [];
    private int _attachments;
    private int _pendingWork;
    private RouteEntryLeaseRetirement _retirementReason;
    private LeaseState _state;
    private bool _committed;
    private bool _blocked;

    internal RouteEntryLease(
        RouteEntryLeaseRegistry registry,
        string? entryId,
        RouteEntryLease? parent,
        bool committed)
    {
        _registry = registry;
        EntryId = entryId;
        Parent = parent;
        _committed = committed;
        RetiredToken = _retirement.Token;
    }

    /// <summary>Gets the entry ID, or <see langword="null"/> for a child lease opened with <see cref="OpenChild"/>.</summary>
    public string? EntryId { get; }

    /// <summary>Gets the lease that must outlive this one, if any.</summary>
    public RouteEntryLease? Parent { get; }

    /// <summary>Gets a token that is cancelled when the lease retires and its authority ends.</summary>
    public CancellationToken RetiredToken { get; }

    /// <summary>Gets a task that completes after the lease's resources have been released.</summary>
    /// <remarks>It never completes for a lease whose release was unsafe; that failure goes to the host instead.</remarks>
    public Task Released => _released.Task;

    /// <summary>Gets whether the lease has retired.</summary>
    public bool IsRetired
    {
        get
        {
            lock (_registry.Gate)
            {
                return _state != LeaseState.Open;
            }
        }
    }

    /// <summary>Gets whether a router commit has contained the lease's entry.</summary>
    /// <remarks>A retired lease that is still committed is presented as a placeholder until a navigation removes it.</remarks>
    public bool IsCommitted
    {
        get
        {
            lock (_registry.Gate)
            {
                return _committed;
            }
        }
    }

    /// <summary>Gets why the lease retired, or <see cref="RouteEntryLeaseRetirement.None"/>.</summary>
    public RouteEntryLeaseRetirement Retirement
    {
        get
        {
            lock (_registry.Gate)
            {
                return _retirementReason;
            }
        }
    }

    /// <summary>Retires the lease for a reason the application owns. Retiring does not navigate.</summary>
    public void Retire()
    {
        _registry.Retire([this], RouteEntryLeaseRetirement.Application);
    }

    /// <summary>Adds a resource that is disposed when the lease is released, in reverse order of ownership.</summary>
    /// <exception cref="InvalidOperationException">The lease is already being released.</exception>
    public void Own(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        OwnCore(resource);
    }

    /// <inheritdoc cref="Own(IAsyncDisposable)"/>
    public void Own(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        OwnCore(resource);
    }

    /// <summary>Opens a child lease without an entry ID, which this lease waits for before its own release.</summary>
    public RouteEntryLease OpenChild()
    {
        return _registry.OpenChild(this);
    }

    /// <summary>Delays release until <paramref name="work"/> has finished, whatever its outcome.</summary>
    /// <remarks>Use it for in-flight preparations and for retirement work, such as a request that observes
    /// <see cref="RetiredToken"/>. The work's own exceptions remain the caller's to observe.</remarks>
    /// <exception cref="InvalidOperationException">The lease is already being released.</exception>
    public void Track(Task work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_registry.Gate)
        {
            ThrowIfReleasing();
            _pendingWork++;
        }

        _ = work.ContinueWith(
            static (_, state) => ((RouteEntryLease)state!).WorkFinished(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Records that a native artifact uses the lease. A retired lease accepts no new artifacts.</summary>
    /// <param name="attachment">The attachment to detach once the artifact no longer uses the lease.</param>
    /// <returns><see langword="false"/> when the lease has retired.</returns>
    public bool TryAttach([NotNullWhen(true)] out RouteEntryLeaseAttachment? attachment)
    {
        lock (_registry.Gate)
        {
            if (_state != LeaseState.Open)
            {
                attachment = null;
                return false;
            }

            _attachments++;
        }

        attachment = new RouteEntryLeaseAttachment(this);
        return true;
    }

    internal RouteEntryLeaseRegistry Registry => _registry;

    internal bool IsSettled => _state is LeaseState.Released or LeaseState.Faulted || _blocked;

    internal void MarkCommittedLocked() => _committed = true;

    internal bool IsCommittedLocked => _committed;

    internal void AddChildLocked(RouteEntryLease child)
    {
        ThrowIfReleasing();
        _children.Add(child);
    }

    // Retires this lease and its descendants under the registry gate; a lease that already retired is unchanged.
    internal void RetireLocked(RouteEntryLeaseRetirement reason, List<RouteEntryLease> retired)
    {
        if (_state != LeaseState.Open)
            return;

        _state = LeaseState.Retired;
        _retirementReason = reason;
        retired.Add(this);
        foreach (RouteEntryLease child in _children)
            child.RetireLocked(RouteEntryLeaseRetirement.Parent, retired);
    }

    internal Task CancelRetirementAsync() => _retirement.CancelAsync();

    internal bool TryBeginReleaseLocked()
    {
        if (_state != LeaseState.Retired || _blocked || _attachments > 0 || _pendingWork > 0 || _children.Count > 0)
            return false;

        _state = LeaseState.Releasing;
        return true;
    }

    internal async ValueTask DisposeResourcesAsync()
    {
        object[] resources;
        lock (_registry.Gate)
        {
            resources = _resources.ToArray();
        }

        for (int index = resources.Length - 1; index >= 0; index--)
        {
            switch (resources[index])
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }

            // Only what has been disposed leaves the lease: an unsafe release keeps the rest.
            lock (_registry.Gate)
            {
                _resources.RemoveAt(index);
            }
        }
    }

    internal void CompleteReleaseLocked()
    {
        _state = LeaseState.Released;
        Parent?._children.Remove(this);
    }

    internal void SignalReleased() => _released.TrySetResult();

    internal void FaultLocked(List<RouteEntryLease> settled)
    {
        _state = LeaseState.Faulted;
        settled.Add(this);
        for (RouteEntryLease? ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.IsSettled)
                break;

            ancestor._blocked = true;
            settled.Add(ancestor);
        }
    }

    internal void Detach()
    {
        bool release;
        lock (_registry.Gate)
        {
            _attachments--;
            release = TryBeginReleaseLocked();
        }

        if (release)
            _registry.StartRelease(this);
    }

    private void OwnCore(object resource)
    {
        lock (_registry.Gate)
        {
            ThrowIfReleasing();
            _resources.Add(resource);
        }
    }

    private void WorkFinished()
    {
        bool release;
        lock (_registry.Gate)
        {
            _pendingWork--;
            release = TryBeginReleaseLocked();
        }

        if (release)
            _registry.StartRelease(this);
    }

    private void ThrowIfReleasing()
    {
        if (_state is LeaseState.Releasing or LeaseState.Released or LeaseState.Faulted)
            throw new InvalidOperationException("The route-entry lease is already being released.");
    }

    internal enum LeaseState
    {
        Open,
        Retired,
        Releasing,
        Released,
        Faulted
    }
}

/// <summary>Records that one native artifact uses a <see cref="RouteEntryLease"/>.</summary>
public sealed class RouteEntryLeaseAttachment
{
    private RouteEntryLease? _lease;

    internal RouteEntryLeaseAttachment(RouteEntryLease lease)
    {
        _lease = lease;
    }

    /// <summary>Acknowledges that the artifact no longer uses the lease. Detaching twice has no effect.</summary>
    public void Detach()
    {
        Interlocked.Exchange(ref _lease, null)?.Detach();
    }
}

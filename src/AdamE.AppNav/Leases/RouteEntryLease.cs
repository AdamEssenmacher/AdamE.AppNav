using System.Diagnostics.CodeAnalysis;

namespace AdamE.AppNav.Leases;

/// <summary>
/// Owns what one occurrence of a route entry borrows, or, for an application lease, what several entry leases share,
/// and releases it once it is retired, closed, and no longer held by a native artifact, a child, or pending work.
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
    private readonly List<Func<ValueTask>> _retirementWork = [];
    private readonly List<RouteEntryLease> _children = [];
    private int _attachments;
    private int _pendingWork;
    private RouteEntryLeaseRetirement _retirementReason;
    private LeaseState _state;
    private bool _committed;
    private bool _sent;
    private bool _inFlight;
    private bool _closed;
    private bool _blocked;

    internal RouteEntryLease(
        RouteEntryLeaseRegistry registry,
        string? entryId,
        RouteEntryLease? parent,
        bool committed,
        bool sent)
    {
        _registry = registry;
        EntryId = entryId;
        Parent = parent;
        _committed = committed;
        _sent = sent;
        RetiredToken = _retirement.Token;
    }

    /// <summary>Gets the entry ID, or <see langword="null"/> for an application lease.</summary>
    public string? EntryId { get; }

    /// <summary>Gets the lease that must outlive this one, if any.</summary>
    public RouteEntryLease? Parent { get; }

    /// <summary>Gets a token that is cancelled when the lease retires. Its callbacks never run inline.</summary>
    public CancellationToken RetiredToken { get; }

    /// <summary>Gets a task that completes after the lease's resources have been released.</summary>
    /// <remarks>It never completes for a lease whose release could not run; that failure is fatal.</remarks>
    public Task Released => _released.Task;

    /// <summary>Gets whether the lease has retired and its authority has ended.</summary>
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

    /// <summary>
    /// Closes the lease to resources and children. Its opener calls this once it has attached everything; the lease is
    /// never released before. Closing an entry lease that was never sent nor committed abandons it.
    /// </summary>
    public void Close()
    {
        _registry.Close(this);
    }

    /// <summary>Adds a resource that is disposed when the lease is released, in reverse order of ownership.</summary>
    /// <remarks>Resources may be added after retirement, until the lease is closed.</remarks>
    /// <exception cref="InvalidOperationException">The lease is closed.</exception>
    public void Own(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        OwnCore(resource);
    }

    /// <summary>Adds a release action that runs when the lease is released, in reverse order of ownership.</summary>
    /// <remarks>Pass <c>resource.Dispose</c> to own a synchronously disposable resource.</remarks>
    /// <exception cref="InvalidOperationException">The lease is closed.</exception>
    public void Own(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        OwnCore(release);
    }

    /// <summary>
    /// Adds work that runs on the host dispatcher when the lease retires, and is awaited before release.
    /// </summary>
    /// <remarks>
    /// Use it for disposal the application owns at retirement, such as cancelling a pending provider request. A
    /// failure is reported to the host as a survived defect. Added after retirement, the work runs at once.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The lease is closed.</exception>
    public void OnRetired(Func<ValueTask> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        bool runNow;
        lock (_registry.Gate)
        {
            ThrowIfClosed();
            runNow = _state != LeaseState.Open;
            if (runNow)
                _pendingWork++;
            else
                _retirementWork.Add(work);
        }

        if (runNow)
            _ = RunRetirementWorkAsync(Task.CompletedTask, [work]);
    }

    /// <summary>Delays release until <paramref name="work"/> has finished, whatever its outcome.</summary>
    /// <remarks>Use it for in-flight preparations. The work's own exceptions remain the caller's to observe.</remarks>
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

    internal bool IsCommittedLocked => _committed;

    internal bool IsAbandonableLocked => _state == LeaseState.Open && !_committed;

    internal bool IsSentLocked => _sent;

    internal void MarkCommittedLocked() => _committed = true;

    internal bool IsOpenLocked => _state == LeaseState.Open;

    // A sent lease stays its entry's current lease until its request ends, even if it is released meanwhile.
    internal bool IsInFlightLocked => _inFlight;

    internal void MarkSentLocked()
    {
        _sent = true;
        _inFlight = true;
    }

    internal void EndRequestLocked() => _inFlight = false;

    internal bool IsReleasedLocked => _state == LeaseState.Released;

    // Returns whether closing abandons the lease: an entry lease that never reached a request or a commit.
    internal bool CloseLocked()
    {
        _closed = true;
        return EntryId is not null && !_sent && IsAbandonableLocked;
    }

    internal void AddChildLocked(RouteEntryLease child)
    {
        ThrowIfClosed();
        _children.Add(child);
    }

    // Retires this lease and its descendants under the registry gate; a lease that already retired is unchanged.
    internal void RetireLocked(RouteEntryLeaseRetirement reason, List<RouteEntryLease> retired)
    {
        if (_state != LeaseState.Open)
            return;

        _state = LeaseState.Retired;
        _retirementReason = reason;
        // Retirement notifications hold the lease until they have run.
        _pendingWork++;
        retired.Add(this);
        foreach (RouteEntryLease child in _children)
            child.RetireLocked(RouteEntryLeaseRetirement.Parent, retired);
    }

    // Runs after the registry gate is released: the token's callbacks and retirement work never run inline.
    internal void NotifyRetired()
    {
        Func<ValueTask>[] work;
        lock (_registry.Gate)
        {
            work = _retirementWork.ToArray();
            _retirementWork.Clear();
        }

        Task cancellation;
        try
        {
            cancellation = _retirement.CancelAsync();
        }
        catch (Exception ex)
        {
            cancellation = Task.FromException(ex);
        }

        _ = RunRetirementWorkAsync(cancellation, work);
    }

    internal bool TryBeginReleaseLocked()
    {
        if (_state != LeaseState.Retired || !_closed || _blocked ||
            _attachments > 0 || _pendingWork > 0 || _children.Count > 0)
        {
            return false;
        }

        _state = LeaseState.Releasing;
        return true;
    }

    internal async ValueTask DisposeResourcesAsync()
    {
        object[] resources;
        lock (_registry.Gate)
        {
            resources = _resources.ToArray();
            _resources.Clear();
        }

        for (int index = resources.Length - 1; index >= 0; index--)
        {
            try
            {
                switch (resources[index])
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync();
                        break;
                    case Action release:
                        release();
                        break;
                }
            }
            catch (Exception ex)
            {
                // A throwing disposer is a survived defect; the rest of the release continues.
                _registry.ReportDefect(this, ex);
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

    private async Task RunRetirementWorkAsync(Task cancellation, Func<ValueTask>[] work)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _registry.ReportDefect(this, ex);
        }

        foreach (Func<ValueTask> item in work)
        {
            try
            {
                await _registry.InvokeOnHostAsync(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _registry.ReportDefect(this, ex);
            }
        }

        WorkFinished();
    }

    private void OwnCore(object resource)
    {
        lock (_registry.Gate)
        {
            ThrowIfClosed();
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

    private void ThrowIfClosed()
    {
        if (_closed)
            throw new InvalidOperationException("The route-entry lease is closed to resources and children.");
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

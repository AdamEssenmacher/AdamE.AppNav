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
    private readonly List<OwnedResource> _resources = [];
    private readonly TaskCompletionSource _retirementNotified = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Func<ValueTask>> _retirementWork = [];
    private readonly List<RouteEntryLease> _children = [];
    private int _attachments;
    private int _pendingWork;
    private RouteEntryLeaseRetirement _retirementReason;
    private LeaseState _state;
    private bool _committed;
    private bool _sent;
    private bool _inFlight;
    private bool _adapterOpened;
    private bool _closed;
    private bool _blocked;
    private bool _retirementWorkTaken;

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

    /// <summary>Gets a token that is cancelled when the lease retires.</summary>
    /// <remarks>
    /// Core cancels it with <see cref="CancellationTokenSource.CancelAsync"/>, so callbacks registered before retirement
    /// run on the thread pool, never inline in a navigation; one registered after retirement runs at once, as with any
    /// token. Use <see cref="OnRetired"/> for work that must run on the host dispatcher before release.
    /// </remarks>
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

    /// <summary>Gets whether the lease's entry is in the router's committed state.</summary>
    /// <remarks>
    /// It becomes false when a commit drops the entry. A retired lease that is still committed is presented as a
    /// placeholder until a navigation removes it.
    /// </remarks>
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
    /// <remarks>
    /// Resources may be added after retirement, until the lease is closed. A failure to dispose it is a survived
    /// defect; see <see cref="Own(IAsyncDisposable, RouteEntryLeaseDisposalFailure)"/> to make it fatal instead.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The lease is closed.</exception>
    public void Own(IAsyncDisposable resource)
    {
        Own(resource, RouteEntryLeaseDisposalFailure.Defect);
    }

    /// <summary>
    /// Adds a resource that is disposed when the lease is released, and says what a failure to dispose it means.
    /// </summary>
    /// <remarks>
    /// With <see cref="RouteEntryLeaseDisposalFailure.Fatal"/>, a failure stops the release: this lease keeps the
    /// resources it has not yet disposed, every ancestor keeps all of its own, and the failure goes to the host's
    /// fatal boundary.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The lease is closed.</exception>
    public void Own(IAsyncDisposable resource, RouteEntryLeaseDisposalFailure failure)
    {
        ArgumentNullException.ThrowIfNull(resource);
        OwnCore(new OwnedResource(resource, failure));
    }

    /// <summary>Adds a release action that runs when the lease is released, in reverse order of ownership.</summary>
    /// <remarks>Pass <c>resource.Dispose</c> to own a synchronously disposable resource. A failure is a survived
    /// defect.</remarks>
    /// <exception cref="InvalidOperationException">The lease is closed.</exception>
    public void Own(Action release)
    {
        Own(release, RouteEntryLeaseDisposalFailure.Defect);
    }

    /// <summary>Adds a release action, and says what its failure means.</summary>
    /// <inheritdoc cref="Own(IAsyncDisposable, RouteEntryLeaseDisposalFailure)" path="/remarks"/>
    /// <exception cref="InvalidOperationException">The lease is closed.</exception>
    public void Own(Action release, RouteEntryLeaseDisposalFailure failure)
    {
        ArgumentNullException.ThrowIfNull(release);
        OwnCore(new OwnedResource(release, failure));
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
            // Until the retirement notification has taken its work, new work joins it, so it runs after the token is
            // cancelled; retirement already holds the lease until then.
            runNow = _retirementWorkTaken;
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

    // A commit dropped the entry: it is no longer in the router's committed state.
    internal void ClearCommittedLocked() => _committed = false;

    // Opened by an adapter during a presentation; the operation that opened it commits or abandons it.
    internal bool IsAdapterOpenedLocked => _adapterOpened;

    internal void MarkAdapterOpenedLocked() => _adapterOpened = true;

    // For tests: completes once this lease's retirement notification and its retirement work have run.
    internal Task RetirementNotified => _retirementNotified.Task;

    // For tests: whether release has started, decided synchronously by whichever call met its last condition.
    internal bool IsReleaseStarted
    {
        get
        {
            lock (_registry.Gate)
            {
                return _state is LeaseState.Releasing or LeaseState.Released or LeaseState.Faulted;
            }
        }
    }

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
            _retirementWorkTaken = true;
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
        OwnedResource[] resources;
        lock (_registry.Gate)
        {
            resources = _resources.ToArray();
            _resources.Clear();
        }

        for (int index = resources.Length - 1; index >= 0; index--)
        {
            OwnedResource resource = resources[index];
            // Nothing this release disposed stays reachable through it, whatever still references the release.
            resources[index] = default;
            try
            {
                switch (resource.Value)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync();
                        break;
                    case Action release:
                        release();
                        break;
                }
            }
            catch (Exception ex) when (resource.Failure == RouteEntryLeaseDisposalFailure.Fatal)
            {
                // The release cannot be shown safe: keep what has not been disposed, and let the registry fault this
                // lease and its ancestors.
                lock (_registry.Gate)
                {
                    _resources.AddRange(resources.Take(index));
                }

                throw new RouteEntryLeaseFatalDisposalException(ex);
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
        _retirementNotified.TrySetResult();
    }

    private void OwnCore(OwnedResource resource)
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

    private readonly record struct OwnedResource(object Value, RouteEntryLeaseDisposalFailure Failure);

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

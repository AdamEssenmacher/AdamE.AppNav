namespace AdamE.AppNav.Requests;

public sealed class InMemoryDeferredNavigationRequestStore : IDeferredNavigationRequestStore
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    private readonly List<StoredRequest> _requests = [];
    private readonly HashSet<RouterNavigationRequest> _deduped = new(NavigationRequestEquivalenceComparer.Instance);
    private readonly HashSet<Guid> _activeReplayRequestIds = [];

    public ValueTask<bool> HasDeferredRequestsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(_requests.Count > 0);
        }
    }

    public ValueTask EnqueueAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_deduped.Contains(request))
            {
                int existingIndex = _requests.FindIndex(entry =>
                    NavigationRequestEquivalenceComparer.Instance.Equals(entry.Request, request));
                System.Diagnostics.Debug.Assert(existingIndex >= 0);
                StoredRequest existing = _requests[existingIndex];
                if (_activeReplayRequestIds.Contains(existing.Id))
                {
                    _deduped.Remove(existing.Request);
                    _requests[existingIndex] = new StoredRequest(Guid.NewGuid(), request);
                    _deduped.Add(request);
                }

                return ValueTask.CompletedTask;
            }

            bool added = _deduped.Add(request);
            System.Diagnostics.Debug.Assert(added);
            _requests.Add(new StoredRequest(Guid.NewGuid(), request));
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<IDeferredNavigationRequestLease> AcquireReplayLeaseAsync(
        CancellationToken cancellationToken = default)
    {
        await _replayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredRequest[] snapshot;
            lock (_gate)
            {
                snapshot = _requests.ToArray();
                var lease = new ReplayLease(this, snapshot);
                _activeReplayRequestIds.UnionWith(snapshot.Select(static entry => entry.Id));
                return lease;
            }
        }
        catch
        {
            _replayGate.Release();
            throw;
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await _replayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _requests.Clear();
                _deduped.Clear();
            }
        }
        finally
        {
            _replayGate.Release();
        }
    }

    private ValueTask AcknowledgeAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            int index = _requests.FindIndex(entry => entry.Id == id);
            if (index < 0)
            {
                // An equivalent enqueue can renew a request while its prior identity is leased.
                // Acknowledging that snapshot must not remove the renewed request.
                if (_activeReplayRequestIds.Contains(id))
                    return ValueTask.CompletedTask;

                throw new InvalidOperationException("The deferred request is no longer present in the store.");
            }

            RouterNavigationRequest request = _requests[index].Request;
            _requests.RemoveAt(index);
            _deduped.Remove(request);
        }

        return ValueTask.CompletedTask;
    }

    private void ReleaseReplayLease()
    {
        lock (_gate)
        {
            _activeReplayRequestIds.Clear();
        }

        _replayGate.Release();
    }

    private sealed record StoredRequest(Guid Id, RouterNavigationRequest Request);

    private sealed class ReplayLease : IDeferredNavigationRequestLease
    {
        private readonly InMemoryDeferredNavigationRequestStore _owner;
        private readonly StoredRequest[] _entries;
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private readonly bool[] _acknowledged;
        private int _disposeStarted;

        public ReplayLease(InMemoryDeferredNavigationRequestStore owner, StoredRequest[] entries)
        {
            _owner = owner;
            _entries = entries;
            _acknowledged = new bool[entries.Length];
            Requests = Array.AsReadOnly(entries.Select(static entry => entry.Request).ToArray());
        }

        public IReadOnlyList<RouterNavigationRequest> Requests { get; }

        public async ValueTask AcknowledgeAsync(
            int requestIndex,
            CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
                if ((uint)requestIndex >= (uint)_entries.Length)
                    throw new ArgumentOutOfRangeException(nameof(requestIndex));
                if (_acknowledged[requestIndex])
                    throw new InvalidOperationException("The deferred request has already been acknowledged.");

                await _owner.AcknowledgeAsync(_entries[requestIndex].Id, cancellationToken).ConfigureAwait(false);
                _acknowledged[requestIndex] = true;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                return;

            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _owner.ReleaseReplayLease();
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }
}

namespace AdamE.MauiRouter.Requests;

public sealed class InMemoryDeferredNavigationRequestStore : IDeferredNavigationRequestStore
{
    private readonly Lock _gate = new();
    private readonly Queue<RouterNavigationRequest> _requests = new();
    private readonly HashSet<RouterNavigationRequest> _deduped = new(NavigationRequestEquivalenceComparer.Instance);

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
            if (!_deduped.Add(request))
            {
                return ValueTask.CompletedTask;
            }

            _requests.Enqueue(request);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<RouterNavigationRequest?> TryDequeueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_requests.Count == 0)
            {
                return ValueTask.FromResult<RouterNavigationRequest?>(null);
            }

            var request = _requests.Dequeue();
            _deduped.Remove(request);
            return ValueTask.FromResult<RouterNavigationRequest?>(request);
        }
    }

    public ValueTask<IReadOnlyList<RouterNavigationRequest>> DrainAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_requests.Count == 0)
            {
                return ValueTask.FromResult<IReadOnlyList<RouterNavigationRequest>>([]);
            }

            var drained = _requests.ToArray();
            _requests.Clear();
            _deduped.Clear();
            return ValueTask.FromResult<IReadOnlyList<RouterNavigationRequest>>(drained);
        }
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _requests.Clear();
            _deduped.Clear();
        }

        return ValueTask.CompletedTask;
    }
}

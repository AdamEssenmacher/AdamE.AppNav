namespace AdamE.AppNav.Requests;

public interface IDeferredNavigationRequestStore
{
    ValueTask<bool> HasDeferredRequestsAsync(CancellationToken cancellationToken = default);

    ValueTask EnqueueAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires exclusive replay ownership and snapshots the requests currently in the store.
    /// </summary>
    /// <remarks>
    /// Acquiring a lease does not remove requests. The call waits cancellably for an existing replay lease to be
    /// released. Requests enqueued while the lease is active are appended to the store but are not added to the
    /// lease snapshot.
    /// </remarks>
    ValueTask<IDeferredNavigationRequestLease> AcquireReplayLeaseAsync(
        CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}

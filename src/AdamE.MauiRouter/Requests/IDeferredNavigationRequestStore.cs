namespace AdamE.MauiRouter.Requests;

public interface IDeferredNavigationRequestStore
{
    ValueTask<bool> HasDeferredRequestsAsync(CancellationToken cancellationToken = default);

    ValueTask EnqueueAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default);

    // ReSharper disable once UnusedMemberInSuper.Global
    ValueTask<RouterNavigationRequest?> TryDequeueAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<RouterNavigationRequest>> DrainAsync(CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}

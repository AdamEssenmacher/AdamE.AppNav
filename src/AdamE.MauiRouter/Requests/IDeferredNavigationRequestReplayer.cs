namespace AdamE.MauiRouter.Requests;

public interface IDeferredNavigationRequestReplayer
{
    /// <summary>
    /// Replays queued deferred requests in FIFO order.
    /// </summary>
    /// <remarks>
    /// Requests that fail to navigate remain queued for a later retry. Result counts describe only the current replay pass.
    /// </remarks>
    ValueTask<DeferredNavigationReplayResult> ReplayAsync(CancellationToken cancellationToken = default);
}

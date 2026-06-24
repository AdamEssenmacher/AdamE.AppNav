namespace AdamE.MauiRouter.Requests;

public interface IDeferredNavigationRequestReplayer
{
    ValueTask<DeferredNavigationReplayResult> ReplayAsync(CancellationToken cancellationToken = default);
}

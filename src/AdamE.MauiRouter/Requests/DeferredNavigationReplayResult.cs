namespace AdamE.MauiRouter.Requests;

public sealed record DeferredNavigationReplayResult(
    int AttemptedCount,
    int ReplayedCount,
    int FailedCount)
{
    public bool HadDeferredRequests => AttemptedCount > 0;

    public bool ReplayedAny => ReplayedCount > 0;
}

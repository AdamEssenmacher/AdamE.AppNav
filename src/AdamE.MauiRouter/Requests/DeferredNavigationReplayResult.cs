namespace AdamE.MauiRouter.Requests;

public readonly record struct DeferredNavigationReplayResult(
    int AttemptedCount,
    int ReplayedCount,
    int FailedCount)
{
    public bool HadDeferredRequests => AttemptedCount > 0;

    public bool ReplayedAny => ReplayedCount > 0;
}

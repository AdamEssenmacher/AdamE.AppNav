namespace AdamE.AppNav.Maui.AppLinks;

internal enum MauiExternalNavigationPendingEpochOutcome
{
    Navigated,
    Exhausted
}

internal sealed class MauiExternalNavigationPendingEpoch
{
    private readonly TaskCompletionSource<MauiExternalNavigationPendingEpochOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<MauiExternalNavigationPendingEpochOutcome> Completion => _completion.Task;

    public bool TryComplete(MauiExternalNavigationPendingEpochOutcome outcome)
    {
        return _completion.TrySetResult(outcome);
    }
}

using AdamE.AppNav.Navigation;
using Microsoft.Extensions.Logging;

namespace AdamE.AppNav.Requests;

public sealed class DeferredNavigationRequestReplayer(
    IDeferredNavigationRequestStore deferredRequests,
    IRouterNavigator navigator,
    ILogger<DeferredNavigationRequestReplayer>? logger = null)
    : IDeferredNavigationRequestReplayer
{
    public async ValueTask<DeferredNavigationReplayResult> ReplayAsync(CancellationToken cancellationToken = default)
    {
        var attempted = 0;
        var replayed = 0;
        var failed = 0;

        IReadOnlyList<RouterNavigationRequest> drained =
            await deferredRequests.DrainAsync(cancellationToken).ConfigureAwait(false);
        var failedRequests = new List<RouterNavigationRequest>();

        var currentIndex = 0;
        try
        {
            for (; currentIndex < drained.Count; currentIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                RouterNavigationRequest request = drained[currentIndex];
                attempted++;

                try
                {
                    await navigator.NavigateAsync(request, cancellationToken).ConfigureAwait(false);
                    replayed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException ||
                                           !cancellationToken.IsCancellationRequested)
                {
                    failed++;
                    failedRequests.Add(request);
                    logger?.LogError(
                        ex,
                        "Failed to replay deferred navigation request for route {Route} from source {Source}.",
                        request.Route,
                        request.Source);
                }
            }
        }
        catch
        {
            await RequeueAsync(
                failedRequests.Concat(drained.Skip(currentIndex)),
                CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        await RequeueAsync(failedRequests, CancellationToken.None).ConfigureAwait(false);

        return new DeferredNavigationReplayResult(attempted, replayed, failed);
    }

    private async ValueTask RequeueAsync(
        IEnumerable<RouterNavigationRequest> requests,
        CancellationToken cancellationToken)
    {
        foreach (RouterNavigationRequest request in requests)
            await deferredRequests.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

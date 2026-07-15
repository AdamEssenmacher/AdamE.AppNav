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

        await using IDeferredNavigationRequestLease lease =
            await deferredRequests.AcquireReplayLeaseAsync(cancellationToken).ConfigureAwait(false);
        for (var currentIndex = 0; currentIndex < lease.Requests.Count; currentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RouterNavigationRequest request = lease.Requests[currentIndex];
            attempted++;

            try
            {
                await navigator.NavigateAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException ||
                                       !cancellationToken.IsCancellationRequested)
            {
                failed++;
                logger?.LogError(
                    ex,
                    "Failed to replay deferred navigation request for route {Route} from source {Source}.",
                    request.Route,
                    request.Source);
                continue;
            }

            await lease.AcknowledgeAsync(currentIndex, cancellationToken).ConfigureAwait(false);
            replayed++;
        }

        return new DeferredNavigationReplayResult(attempted, replayed, failed);
    }
}

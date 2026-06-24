using AdamE.MauiRouter.Navigation;
using Microsoft.Extensions.Logging;

namespace AdamE.MauiRouter.Requests;

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

        while (await deferredRequests.TryDequeueAsync(cancellationToken).ConfigureAwait(false) is { } request)
        {
            attempted++;

            try
            {
                await navigator.NavigateAsync(request, cancellationToken).ConfigureAwait(false);
                replayed++;
            }
            catch (Exception ex)
            {
                failed++;
                logger?.LogError(
                    ex,
                    "Failed to replay deferred navigation request for route {Route} from source {Source}.",
                    request.Route,
                    request.Source);
            }
        }

        return new DeferredNavigationReplayResult(attempted, replayed, failed);
    }
}

using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using Microsoft.Extensions.Logging;

namespace AdamE.AppNav.Requests;

/// <summary>
/// Replays pending navigation requests.
/// </summary>
/// <param name="deferredRequests">The store holding requests to replay.</param>
/// <param name="navigator">The navigator that attempts each request.</param>
/// <param name="logger">The logger for default Safe diagnostics when no diagnostics instance is supplied.</param>
/// <param name="diagnostics">The configured diagnostics instance for replay failures.</param>
public sealed class DeferredNavigationRequestReplayer(
    IDeferredNavigationRequestStore deferredRequests,
    IRouterNavigator navigator,
    ILogger<DeferredNavigationRequestReplayer>? logger,
    NavigationDiagnostics? diagnostics)
    : IDeferredNavigationRequestReplayer
{
    private readonly NavigationDiagnostics _diagnostics =
        diagnostics ?? (logger is null ? NavigationDiagnostics.None : new NavigationDiagnostics(logger));

#pragma warning disable RS0027 // Preserve the shipped optional logger argument.
    public DeferredNavigationRequestReplayer(
        IDeferredNavigationRequestStore deferredRequests,
        IRouterNavigator navigator,
        ILogger<DeferredNavigationRequestReplayer>? logger = null)
        : this(deferredRequests, navigator, logger, diagnostics: null)
    {
    }
#pragma warning restore RS0027

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
                var data = new Dictionary<string, object?>
                {
                    [NavigationDiagnosticDataKeys.RouteType] = request.Route?.GetType().FullName,
                    [NavigationDiagnosticDataKeys.RequestSource] = request.Source.ToString(),
                    [NavigationDiagnosticDataKeys.ExceptionType] = ex.GetType().FullName,
                    [NavigationDiagnosticDataKeys.ExceptionMessage] = ex.Message
                };
                if (request.Uri is { } uri)
                    data[NavigationDiagnosticDataKeys.Uri] = uri.ToString();

                _diagnostics.WriteWithException(
                    NavigationDiagnosticEventKind.DeferredRequestReplayFailed,
                    Guid.NewGuid().ToString("N"),
                    "Failed to replay a deferred navigation request.",
                    data,
                    ex);
                continue;
            }

            await lease.AcknowledgeAsync(currentIndex, cancellationToken).ConfigureAwait(false);
            replayed++;
        }

        return new DeferredNavigationReplayResult(attempted, replayed, failed);
    }
}

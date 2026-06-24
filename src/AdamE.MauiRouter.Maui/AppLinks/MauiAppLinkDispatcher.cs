using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Maui.AppLinks;

internal sealed class MauiAppLinkDispatcher(MauiExternalNavigationDispatcher dispatcher)
{
    public bool HasPendingRequests => dispatcher.HasPendingRequests;

    public void Dispatch(RouterNavigationRequest? request)
    {
        dispatcher.Dispatch(request);
    }

    public ValueTask<bool> WaitForPendingRequestAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return dispatcher.WaitForPendingRequestAsync(timeout, cancellationToken);
    }

    public void MarkReady()
    {
        dispatcher.MarkReady();
    }
}

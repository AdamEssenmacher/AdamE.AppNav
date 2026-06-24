using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Policies;

public sealed class AccessGateNavigationPolicy(
    INavigationAccessEvaluator evaluator,
    IDeferredNavigationRequestStore deferredRequests)
    : INavigationRequestPolicy
{
    public async ValueTask<RouterNavigationRequest> ApplyAsync(
        NavigationRequestPolicyContext context,
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var decision = await evaluator
            .EvaluateAsync(context, cancellationToken)
            .ConfigureAwait(false);

        if (decision.IsAllowed)
        {
            return request;
        }

        if (decision.DeferOriginalRequest)
        {
            await deferredRequests.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return decision.RedirectRequest! with
        {
            WindowId = decision.RedirectRequest.WindowId ?? request.WindowId
        };
    }
}

using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Policies;

public sealed class AccessGateNavigationPolicy(
    INavigationAccessEvaluator evaluator,
    IDeferredNavigationRequestStore deferredRequests)
    : INavigationRequestPolicy
{
    public async ValueTask<RouterNavigationRequest> ApplyAsync(
        NavigationRequestPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        RouterNavigationRequest request = context.Request;

        NavigationAccessDecision decision = await evaluator
            .EvaluateAsync(context, cancellationToken)
            .ConfigureAwait(false);

        if (decision.IsAllowed)
            return request;

        if (decision.DeferOriginalRequest)
            await deferredRequests.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);

        return decision.RedirectRequest! with
        {
            WindowId = decision.RedirectRequest.WindowId ?? request.WindowId
        };
    }
}

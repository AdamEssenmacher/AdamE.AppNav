using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Policies;

public interface INavigationRequestPolicy
{
    ValueTask<RouterNavigationRequest> ApplyAsync(
        NavigationRequestPolicyContext context,
        CancellationToken cancellationToken = default);
}

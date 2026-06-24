using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Policies;

public interface INavigationRequestPolicy
{
    ValueTask<RouterNavigationRequest> ApplyAsync(
        NavigationRequestPolicyContext context,
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default);
}

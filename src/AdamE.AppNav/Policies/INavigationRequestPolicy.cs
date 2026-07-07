using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Policies;

public interface INavigationRequestPolicy
{
    ValueTask<RouterNavigationRequest> ApplyAsync(
        NavigationRequestPolicyContext context,
        CancellationToken cancellationToken = default);
}

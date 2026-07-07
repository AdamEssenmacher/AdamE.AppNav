namespace AdamE.AppNav.Policies;

public interface INavigationAccessEvaluator
{
    ValueTask<NavigationAccessDecision> EvaluateAsync(
        NavigationRequestPolicyContext context,
        CancellationToken cancellationToken = default);
}

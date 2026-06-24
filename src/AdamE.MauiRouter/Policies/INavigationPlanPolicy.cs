using AdamE.MauiRouter.Plans;

namespace AdamE.MauiRouter.Policies;

public interface INavigationPlanPolicy
{
    ValueTask<NavigationPlan> ApplyAsync(
        NavigationPlanPolicyContext context,
        NavigationPlan plan,
        CancellationToken cancellationToken = default);
}

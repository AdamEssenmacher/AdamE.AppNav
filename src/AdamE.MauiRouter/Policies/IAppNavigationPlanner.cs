using AdamE.MauiRouter.Plans;

namespace AdamE.MauiRouter.Policies;

public interface IAppNavigationPlanner
{
    ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default);
}

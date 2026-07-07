using AdamE.AppNav.Plans;

namespace AdamE.AppNav.Policies;

public interface IAppNavigationPlanner
{
    ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default);
}

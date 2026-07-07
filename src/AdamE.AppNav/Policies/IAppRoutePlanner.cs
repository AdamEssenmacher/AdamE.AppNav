using AdamE.AppNav.Plans;

namespace AdamE.AppNav.Policies;

internal interface IAppRoutePlanner<TRoute>
    where TRoute : AppRoute
{
    ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext<TRoute> context,
        CancellationToken cancellationToken = default);
}

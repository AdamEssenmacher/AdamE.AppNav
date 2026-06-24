using AdamE.MauiRouter.Plans;

namespace AdamE.MauiRouter.Policies;

internal interface IAppRoutePlanner<TRoute>
    where TRoute : AppRoute
{
    ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext<TRoute> context,
        CancellationToken cancellationToken = default);
}

using AdamE.MauiRouter.Plans;

namespace AdamE.MauiRouter.Policies;

internal interface IAppRoutePlannerRegistration
{
    Type RouteType { get; }

    ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default);
}

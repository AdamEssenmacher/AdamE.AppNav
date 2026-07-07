using AdamE.AppNav.Plans;

namespace AdamE.AppNav.Policies;

internal interface IAppRoutePlannerRegistration
{
    Type RouteType { get; }

    ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default);
}

using AdamE.MauiRouter.Plans;

namespace AdamE.MauiRouter.Policies;

internal sealed class AppRoutePlannerRegistration<TRoute>(IAppRoutePlanner<TRoute> planner)
    : IAppRoutePlannerRegistration
    where TRoute : AppRoute
{
    private readonly IAppRoutePlanner<TRoute> _planner = planner ?? throw new ArgumentNullException(nameof(planner));

    public Type RouteType => typeof(TRoute);

    public ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Route is not TRoute route)
            throw new InvalidOperationException(
                $"Planner for route type '{typeof(TRoute).FullName}' cannot handle route type '{context.Route.GetType().FullName}'.");

        var typedContext = new NavigationPlanningContext<TRoute>(
            context.Request,
            route,
            context.CurrentState,
            context.OperationId);

        return _planner.CreatePlanAsync(typedContext, cancellationToken);
    }
}

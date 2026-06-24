using AdamE.MauiRouter.Plans;

namespace AdamE.MauiRouter.Policies;

internal sealed class TypedAppNavigationPlanner : IAppNavigationPlanner
{
    private readonly IReadOnlyDictionary<Type, IAppRoutePlannerRegistration> _registrations;

    public TypedAppNavigationPlanner(IEnumerable<IAppRoutePlannerRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var map = new Dictionary<Type, IAppRoutePlannerRegistration>();
        foreach (var registration in registrations)
        {
            if (!map.TryAdd(registration.RouteType, registration))
            {
                throw new InvalidOperationException(
                    $"More than one app route planner is registered for route type '{registration.RouteType.FullName}'.");
            }
        }

        _registrations = map;
    }

    public ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var routeType = context.Route.GetType();
        if (!_registrations.TryGetValue(routeType, out var registration))
        {
            throw new RoutePlannerNotFoundException(routeType);
        }

        return registration.CreatePlanAsync(context, cancellationToken);
    }
}

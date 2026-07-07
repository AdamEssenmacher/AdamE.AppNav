using AdamE.AppNav.Plans;

namespace AdamE.AppNav.Policies;

internal sealed class TypedAppNavigationPlanner : IAppNavigationPlanner
{
    private readonly Dictionary<Type, IAppRoutePlannerRegistration> _registrations;

    public TypedAppNavigationPlanner(IEnumerable<IAppRoutePlannerRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var map = new Dictionary<Type, IAppRoutePlannerRegistration>();
        foreach (IAppRoutePlannerRegistration registration in registrations)
            if (!map.TryAdd(registration.RouteType, registration))
                throw new InvalidOperationException(
                    $"More than one app route planner is registered for route type '{registration.RouteType.FullName}'.");

        _registrations = map;
    }

    public ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        Type routeType = context.Route.GetType();

        return !_registrations.TryGetValue(routeType, out IAppRoutePlannerRegistration? registration)
            ? throw new RoutePlannerNotFoundException(routeType)
            : registration.CreatePlanAsync(context, cancellationToken);
    }
}

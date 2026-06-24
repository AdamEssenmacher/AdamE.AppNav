namespace AdamE.MauiRouter.Policies;

public sealed class RoutePlannerNotFoundException : InvalidOperationException
{
    public RoutePlannerNotFoundException(Type routeType)
        : base($"No app route planner is registered for route type '{(routeType ?? throw new ArgumentNullException(nameof(routeType))).FullName}'.")
    {
        RouteType = routeType;
    }

    public Type RouteType { get; }
}

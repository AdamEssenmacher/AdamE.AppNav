namespace AdamE.AppNav.Policies;

public sealed class RoutePlannerNotFoundException(Type routeType) : InvalidOperationException(
    $"No app route planner is registered for route type '{(routeType ?? throw new ArgumentNullException(nameof(routeType))).FullName}'.")
{
    public Type RouteType { get; } = routeType;
}

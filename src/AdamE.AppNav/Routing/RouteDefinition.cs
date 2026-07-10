namespace AdamE.AppNav.Routing;

public sealed class RouteDefinition
{
    private readonly Func<RouteMatchContext, AppRoute> _createRoute;
    private readonly Func<AppRoute, IReadOnlyDictionary<string, object?>?, RouteValueCodecRegistry, string> _formatRoute;

    internal RouteDefinition(
        Type routeType,
        RouteTemplate template,
        Func<RouteMatchContext, AppRoute> createRoute,
        Func<AppRoute, IReadOnlyDictionary<string, object?>?, RouteValueCodecRegistry, string> formatRoute,
        int order)
    {
        RouteType = routeType ?? throw new ArgumentNullException(nameof(routeType));
        Template = template ?? throw new ArgumentNullException(nameof(template));
        _createRoute = createRoute ?? throw new ArgumentNullException(nameof(createRoute));
        _formatRoute = formatRoute ?? throw new ArgumentNullException(nameof(formatRoute));
        Order = order;
    }

    public Type RouteType { get; }

    public RouteTemplate Template { get; }

    public int Order { get; }

    internal (AppRoute Route, IReadOnlyDictionary<string, object?> Metadata) Create(RouteMatchContext context)
    {
        AppRoute route = _createRoute(context);
        return (route, context.Metadata);
    }

    internal string Format(
        AppRoute route,
        RouteValueCodecRegistry codecs,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        return _formatRoute(route, metadata, codecs);
    }
}

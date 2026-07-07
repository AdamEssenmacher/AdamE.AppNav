namespace AdamE.MauiRouter.Routing;

public sealed record RouteMatchResult(
    bool IsSuccess,
    AppRoute? Route,
    RouteDefinition? Definition,
    IReadOnlyDictionary<string, object?> Metadata,
    IReadOnlyList<RouteDiagnostic> Diagnostics)
{
    public static RouteMatchResult Success(
        AppRoute route,
        RouteDefinition definition,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        return new RouteMatchResult(
            true,
            route,
            definition,
            metadata ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            []);
    }

    public static RouteMatchResult Failure(params RouteDiagnostic[] diagnostics)
    {
        return new RouteMatchResult(
            false,
            null,
            null,
            new Dictionary<string, object?>(StringComparer.Ordinal),
            diagnostics);
    }
}

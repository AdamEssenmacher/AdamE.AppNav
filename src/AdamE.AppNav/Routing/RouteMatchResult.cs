namespace AdamE.AppNav.Routing;

public sealed record RouteMatchResult(
    bool IsSuccess,
    AppRoute? Route,
    RouteDefinition? Definition,
    IReadOnlyDictionary<string, object?> Metadata,
    IReadOnlyList<RouteDiagnostic> Diagnostics)
{
    internal static readonly IReadOnlyDictionary<string, object?> EmptyMetadata =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(StringComparer.Ordinal));

    public static RouteMatchResult Success(
        AppRoute route,
        RouteDefinition definition,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        return new RouteMatchResult(
            true,
            route,
            definition,
            metadata ?? EmptyMetadata,
            []);
    }

    public static RouteMatchResult Failure(params RouteDiagnostic[] diagnostics)
    {
        return new RouteMatchResult(
            false,
            null,
            null,
            EmptyMetadata,
            diagnostics);
    }
}

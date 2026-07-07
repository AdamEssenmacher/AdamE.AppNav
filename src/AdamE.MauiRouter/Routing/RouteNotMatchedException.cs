namespace AdamE.MauiRouter.Routing;

public sealed class RouteNotMatchedException(Uri uri, IReadOnlyList<RouteDiagnostic> diagnostics)
    : Exception($"No route matched '{uri}'.")
{
    public Uri Uri { get; } = uri;

    public IReadOnlyList<RouteDiagnostic> Diagnostics { get; } = diagnostics;
}

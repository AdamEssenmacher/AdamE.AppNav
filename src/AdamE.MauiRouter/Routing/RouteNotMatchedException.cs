namespace AdamE.MauiRouter.Routing;

public sealed class RouteNotMatchedException : Exception
{
    public RouteNotMatchedException(Uri uri, IReadOnlyList<RouteDiagnostic> diagnostics)
        : base($"No route matched '{uri}'.")
    {
        Uri = uri;
        Diagnostics = diagnostics;
    }

    public Uri Uri { get; }

    public IReadOnlyList<RouteDiagnostic> Diagnostics { get; }
}

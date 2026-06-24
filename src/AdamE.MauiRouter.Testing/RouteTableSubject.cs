using AdamE.MauiRouter.Routing;

namespace AdamE.MauiRouter.Testing;

public sealed class RouteTableSubject
{
    private readonly RouteTable _routeTable;

    private RouteTableSubject(RouteTable routeTable)
    {
        _routeTable = routeTable;
    }

    public RouteTable RouteTable => _routeTable;

    public static RouteTableSubject Create(Action<RouteTableBuilder> configure)
    {
        return new RouteTableSubject(RouteTable.Create(configure));
    }

    public RouteMatchResult Match(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        return _routeTable.Match(CreateUri(uri));
    }

    public TRoute MatchRoute<TRoute>(string uri)
        where TRoute : AppRoute
    {
        var result = Match(uri);
        if (!result.IsSuccess || result.Route is null)
        {
            throw new NavigationAssertionException(
                $"Expected URI '{uri}' to match route '{typeof(TRoute).FullName}', but matching failed: {DescribeDiagnostics(result)}");
        }

        if (result.Route is not TRoute route)
        {
            throw new NavigationAssertionException(
                $"Expected URI '{uri}' to match route '{typeof(TRoute).FullName}', but matched '{result.Route.GetType().FullName}'.");
        }

        return route;
    }

    public void ShouldNotMatch(string uri, string? diagnosticCode = null)
    {
        var result = Match(uri);
        if (result.IsSuccess)
        {
            throw new NavigationAssertionException(
                $"Expected URI '{uri}' not to match, but matched route '{result.Route?.GetType().FullName}'.");
        }

        if (diagnosticCode is null)
        {
            return;
        }

        if (result.Diagnostics.Any(diagnostic => StringComparer.Ordinal.Equals(diagnostic.Code, diagnosticCode)))
        {
            return;
        }

        throw new NavigationAssertionException(
            $"Expected URI '{uri}' not to match with diagnostic '{diagnosticCode}', but diagnostics were: {DescribeDiagnostics(result)}");
    }

    public string Format(AppRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return _routeTable.Format(route);
    }

    public void RoundTrips(AppRoute route, string expectedPathAndQuery)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPathAndQuery);

        var formatted = Format(route);
        if (!StringComparer.Ordinal.Equals(formatted, expectedPathAndQuery))
        {
            throw new NavigationAssertionException(
                $"Expected route '{route}' to format as '{expectedPathAndQuery}', but formatted as '{formatted}'.");
        }

        var result = Match(expectedPathAndQuery);
        if (!result.IsSuccess || result.Route is null)
        {
            throw new NavigationAssertionException(
                $"Expected formatted URI '{expectedPathAndQuery}' to match route '{route.GetType().FullName}', but matching failed: {DescribeDiagnostics(result)}");
        }

        if (!Equals(route, result.Route))
        {
            throw new NavigationAssertionException(
                $"Expected formatted URI '{expectedPathAndQuery}' to round-trip route '{route}', but matched '{result.Route}'.");
        }
    }

    private static Uri CreateUri(string uri)
    {
        return Uri.TryCreate(uri, UriKind.RelativeOrAbsolute, out var parsed)
            ? parsed
            : throw new NavigationAssertionException($"URI '{uri}' is not valid.");
    }

    private static string DescribeDiagnostics(RouteMatchResult result)
    {
        if (result.Diagnostics.Count == 0)
        {
            return "no diagnostics";
        }

        return string.Join(
            "; ",
            result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
    }
}

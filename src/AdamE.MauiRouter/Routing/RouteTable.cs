namespace AdamE.MauiRouter.Routing;

public sealed class RouteTable
{
    private readonly IReadOnlyList<RouteDefinition> _definitions;

    internal RouteTable(IReadOnlyList<RouteDefinition> definitions)
    {
        _definitions = definitions;
    }

    public IReadOnlyList<RouteDefinition> Definitions => _definitions;

    public static RouteTable Create(Action<RouteTableBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new RouteTableBuilder();
        configure(builder);
        return builder.Build();
    }

    public RouteMatchResult Match(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var (path, query) = SplitUri(uri);
        var queryValues = QueryString.Parse(query);

        foreach (var definition in _definitions)
        {
            var pathValues = definition.Template.Match(path);
            if (pathValues is null)
            {
                continue;
            }

            try
            {
                var context = new RouteMatchContext(uri, pathValues, queryValues);
                var match = definition.Create(context);
                return RouteMatchResult.Success(match.Route, definition, match.Metadata);
            }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException or NotSupportedException)
            {
                return RouteMatchResult.Failure(new RouteDiagnostic(
                    "route.value.invalid",
                    ex.Message,
                    new Dictionary<string, object?>
                    {
                        ["path"] = path,
                        ["template"] = definition.Template.Value,
                        ["routeType"] = definition.RouteType.FullName
                    }));
            }
        }

        return RouteMatchResult.Failure(new RouteDiagnostic(
            "route.not_matched",
            $"No route template matched path '{path}'.",
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["candidateCount"] = _definitions.Count
            }));
    }

    public string Format(AppRoute route)
    {
        return Format(route, metadata: null);
    }

    /// <summary>
    /// Formats an app-facing route request into a relative URI path, including any metadata used by the route definition.
    /// </summary>
    public string Format(AppRouteRequest routeRequest)
    {
        ArgumentNullException.ThrowIfNull(routeRequest);
        return Format(routeRequest.Route, routeRequest.Metadata);
    }

    public string Format(
        AppRoute route,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        ArgumentNullException.ThrowIfNull(route);

        var routeType = route.GetType();
        var definition = _definitions.FirstOrDefault(candidate => candidate.RouteType.IsAssignableFrom(routeType));
        if (definition is null)
        {
            throw new InvalidOperationException($"No route formatter is registered for {routeType.FullName}.");
        }

        return definition.Format(route, metadata);
    }

    public Uri FormatUri(AppRoute route, Uri baseUri)
    {
        return FormatUri(route, baseUri, metadata: null);
    }

    /// <summary>
    /// Formats an app-facing route request into an absolute URI using the supplied base URI.
    /// </summary>
    public Uri FormatUri(AppRouteRequest routeRequest, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(routeRequest);
        return FormatUri(routeRequest.Route, baseUri, routeRequest.Metadata);
    }

    public Uri FormatUri(
        AppRoute route,
        Uri baseUri,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        return new Uri(baseUri, Format(route, metadata));
    }

    private static (string Path, string Query) SplitUri(Uri uri)
    {
        if (uri.IsAbsoluteUri)
        {
            return (uri.AbsolutePath, uri.Query.TrimStart('?'));
        }

        var text = uri.OriginalString;
        var queryIndex = text.IndexOf('?');
        return queryIndex < 0
            ? (text, string.Empty)
            : (text[..queryIndex], text[(queryIndex + 1)..]);
    }
}

namespace AdamE.MauiRouter.Routing;

public sealed class RouteTable
{
    internal RouteTable(IReadOnlyList<RouteDefinition> definitions)
    {
        Definitions = definitions;
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public IReadOnlyList<RouteDefinition> Definitions { get; }

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

        (string path, string query) = SplitUri(uri);
        IReadOnlyDictionary<string, IReadOnlyList<string>> queryValues = QueryString.Parse(query);

        foreach (RouteDefinition definition in Definitions)
        {
            IReadOnlyDictionary<string, string>? pathValues = definition.Template.Match(path);
            if (pathValues is null)
                continue;

            try
            {
                var context = new RouteMatchContext(uri, pathValues, queryValues);
                RouteMatch match = definition.Create(context);
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
                ["candidateCount"] = Definitions.Count
            }));
    }

    public string Format(AppRoute route)
    {
        return Format(route, null);
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

        Type routeType = route.GetType();
        RouteDefinition? definition = FindDefinition(routeType);
        return definition is null
            ? throw new InvalidOperationException($"No route formatter is registered for {routeType.FullName}.")
            : definition.Format(route, metadata);
    }

    public Uri FormatUri(AppRoute route, Uri baseUri)
    {
        return FormatUri(route, baseUri, null);
    }

    /// <summary>
    /// Formats an app-facing route request into an absolute URI using the supplied base URI.
    /// </summary>
    public Uri FormatUri(AppRouteRequest routeRequest, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(routeRequest);
        return FormatUri(routeRequest.Route, baseUri, routeRequest.Metadata);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public Uri FormatUri(
        AppRoute route,
        Uri baseUri,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        return new Uri(baseUri, Format(route, metadata));
    }

    private RouteDefinition? FindDefinition(Type routeType)
    {
        for (Type? currentType = routeType;
             currentType is not null && currentType != typeof(object);
             currentType = currentType.BaseType)
        {
            RouteDefinition? definition = Definitions.FirstOrDefault(candidate => candidate.RouteType == currentType);
            if (definition is not null)
                return definition;
        }

        return null;
    }

    private static (string Path, string Query) SplitUri(Uri uri)
    {
        if (uri.IsAbsoluteUri)
            return (uri.AbsolutePath, uri.Query.TrimStart('?'));

        string text = uri.OriginalString;
        int fragmentIndex = text.IndexOf('#');
        if (fragmentIndex >= 0)
            text = text[..fragmentIndex];

        int queryIndex = text.IndexOf('?');
        return queryIndex < 0
            ? (text, string.Empty)
            : (text[..queryIndex], text[(queryIndex + 1)..]);
    }
}

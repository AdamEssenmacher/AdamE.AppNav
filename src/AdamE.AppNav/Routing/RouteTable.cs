namespace AdamE.AppNav.Routing;

public sealed class RouteTable
{
    private readonly Dictionary<Type, RouteDefinition> _definitionsByRouteType;
    private readonly RouteCandidateIndex _candidateIndex;

    internal RouteTable(IReadOnlyList<RouteDefinition> definitions)
    {
        Definitions = definitions;
        _definitionsByRouteType = BuildDefinitionIndex(definitions);
        _candidateIndex = RouteCandidateIndex.Create(definitions);
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
        string[] pathSegments = RouteTemplate.SplitPathForMatch(path);
        IReadOnlyDictionary<string, IReadOnlyList<string>> queryValues = QueryString.Parse(query);

        foreach (RouteDefinition definition in _candidateIndex.Select(pathSegments))
        {
            IReadOnlyDictionary<string, string>? pathValues = definition.Template.Match(pathSegments);
            if (pathValues is null)
                continue;

            try
            {
                var context = new RouteMatchContext(uri, pathValues, queryValues);
                (AppRoute route, IReadOnlyDictionary<string, object?> metadata) = definition.Create(context);
                return RouteMatchResult.Success(route, definition, metadata);
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
            if (_definitionsByRouteType.TryGetValue(currentType, out RouteDefinition? definition))
                return definition;
        }

        return null;
    }

    private static Dictionary<Type, RouteDefinition> BuildDefinitionIndex(IReadOnlyList<RouteDefinition> definitions)
    {
        var index = new Dictionary<Type, RouteDefinition>();
        foreach (RouteDefinition definition in definitions)
            index.TryAdd(definition.RouteType, definition);

        return index;
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

    private sealed class RouteCandidateIndex
    {
        private RouteCandidateIndex(CandidateNode root)
        {
            Root = root;
        }

        private CandidateNode Root { get; }

        public static RouteCandidateIndex Create(IReadOnlyList<RouteDefinition> definitions)
        {
            var root = new CandidateNode([]);
            foreach (RouteDefinition definition in definitions)
            {
                CandidateNode node = root;
                foreach (string literal in definition.Template.LiteralPrefix)
                    node = node.GetOrAddChild(literal);
            }

            AssignCandidates(root, definitions);
            return new RouteCandidateIndex(root);
        }

        public IReadOnlyList<RouteDefinition> Select(IReadOnlyList<string> pathSegments)
        {
            CandidateNode node = Root;
            foreach (string segment in pathSegments)
            {
                if (!node.Children.TryGetValue(segment, out CandidateNode? next))
                    break;

                node = next;
            }

            return node.Candidates;
        }

        private static void AssignCandidates(CandidateNode node, IReadOnlyList<RouteDefinition> definitions)
        {
            node.Candidates = definitions
                .Where(definition => IsPrefixMatch(definition.Template.LiteralPrefix, node.Prefix))
                .ToArray();

            foreach (CandidateNode child in node.Children.Values)
                AssignCandidates(child, definitions);
        }

        private static bool IsPrefixMatch(IReadOnlyList<string> routePrefix, IReadOnlyList<string> nodePrefix)
        {
            if (routePrefix.Count > nodePrefix.Count)
                return false;

            for (var i = 0; i < routePrefix.Count; i++)
            {
                if (!StringComparer.Ordinal.Equals(routePrefix[i], nodePrefix[i]))
                    return false;
            }

            return true;
        }

        private sealed class CandidateNode
        {
            public CandidateNode(IReadOnlyList<string> prefix)
            {
                Prefix = prefix;
            }

            public IReadOnlyList<string> Prefix { get; }

            public Dictionary<string, CandidateNode> Children { get; } = new(StringComparer.Ordinal);

            public RouteDefinition[] Candidates { get; set; } = [];

            public CandidateNode GetOrAddChild(string literal)
            {
                if (Children.TryGetValue(literal, out CandidateNode? child))
                    return child;

                string[] childPrefix = [..Prefix, literal];
                child = new CandidateNode(childPrefix);
                Children[literal] = child;
                return child;
            }
        }
    }
}

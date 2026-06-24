namespace AdamE.MauiRouter.Routing;

public sealed class RouteTableBuilder
{
    private readonly List<RouteDefinition> _definitions = new();
    private RouteConstraintRegistry _constraints = RouteConstraintRegistry.BuiltIn;
    private int _nextOrder;

    public RouteTableBuilder AddConstraint(
        string name,
        Func<string, bool> matches,
        IEnumerable<string>? disjointWith = null)
    {
        _constraints = _constraints.AddCustom(name, matches, disjointWith);
        return this;
    }

    public RouteTableBuilder Map<TRoute>(
        string template,
        Func<RouteMatchContext, TRoute> createRoute,
        Action<RouteFormatBuilder<TRoute>>? configureFormat = null)
        where TRoute : AppRoute
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentNullException.ThrowIfNull(createRoute);

        var routeTemplate = RouteTemplate.Parse(template, _constraints);
        var formatBuilder = new RouteFormatBuilder<TRoute>();
        configureFormat?.Invoke(formatBuilder);
        formatBuilder.Validate(routeTemplate);

        _definitions.Add(new RouteDefinition(
            typeof(TRoute),
            routeTemplate,
            context => createRoute(context),
            (route, metadata) => formatBuilder.Format((TRoute)route, routeTemplate, metadata),
            _nextOrder++));

        return this;
    }

    public RouteTableBuilder MapRoute<TRoute>(
        string template,
        Action<ConventionRouteBuilder<TRoute>>? configure = null)
        where TRoute : AppRoute
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var routeTemplate = RouteTemplate.Parse(template, _constraints);
        var builder = new ConventionRouteBuilder<TRoute>();
        configure?.Invoke(builder);
        var binder = ConventionRouteBinder<TRoute>.Create(routeTemplate, builder);

        _definitions.Add(new RouteDefinition(
            typeof(TRoute),
            routeTemplate,
            context =>
            {
                var route = binder.CreateRoute(context);
                binder.ApplyMetadata(context);
                return route;
            },
            (route, metadata) => binder.Format((TRoute)route, metadata),
            _nextOrder++));

        return this;
    }

    public RouteTable Build()
    {
        var duplicateTemplate = _definitions
            .GroupBy(definition => definition.Template.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateTemplate is not null)
        {
            throw new InvalidOperationException(
                $"Route template '{duplicateTemplate.Key}' is registered more than once.");
        }

        for (var i = 0; i < _definitions.Count; i++)
        {
            for (var j = i + 1; j < _definitions.Count; j++)
            {
                var left = _definitions[i];
                var right = _definitions[j];
                if (left.Template.ComparePrecedence(right.Template) == 0 &&
                    left.Template.CanOverlap(right.Template))
                {
                    throw new InvalidOperationException(
                        $"Route templates '{left.Template.Value}' and '{right.Template.Value}' are ambiguous.");
                }
            }
        }

        var sorted = _definitions
            .OrderBy(definition => definition.Template, RouteTemplatePrecedenceComparer.Instance)
            .ThenBy(definition => definition.Order)
            .ToArray();

        return new RouteTable(sorted);
    }

    private sealed class RouteTemplatePrecedenceComparer : IComparer<RouteTemplate>
    {
        public static RouteTemplatePrecedenceComparer Instance { get; } = new();

        public int Compare(RouteTemplate? x, RouteTemplate? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return 1;
            }

            if (y is null)
            {
                return -1;
            }

            return x.ComparePrecedence(y);
        }
    }
}

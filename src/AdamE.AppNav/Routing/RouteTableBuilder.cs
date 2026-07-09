using System.Diagnostics.CodeAnalysis;

namespace AdamE.AppNav.Routing;

public sealed class RouteTableBuilder
{
    private readonly List<RouteDefinition> _definitions = [];
    private RouteConstraintRegistry _constraints = RouteConstraintRegistry.BuiltIn;
    private int _nextOrder;

    /// <summary>
    /// Adds route definitions from a reusable module.
    /// </summary>
    /// <param name="module">The module that will add route definitions to this builder.</param>
    /// <returns>The same builder instance for registration chaining.</returns>
    public RouteTableBuilder AddModule(IRouteTableModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        module.MapRoutes(this);
        return this;
    }

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

        RouteTemplate routeTemplate = RouteTemplate.Parse(template, _constraints);
        var formatBuilder = new RouteFormatBuilder<TRoute>();
        configureFormat?.Invoke(formatBuilder);
        formatBuilder.Validate(routeTemplate);

        _definitions.Add(new RouteDefinition(
            typeof(TRoute),
            routeTemplate,
            createRoute,
            (route, metadata) => formatBuilder.Format((TRoute)route, routeTemplate, metadata),
            _nextOrder++));

        return this;
    }

    public RouteTableBuilder MapRoute<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
        TRoute>(
        string template,
        Action<ConventionRouteBuilder<TRoute>>? configure = null)
        where TRoute : AppRoute
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        RouteTemplate routeTemplate = RouteTemplate.Parse(template, _constraints);
        var builder = new ConventionRouteBuilder<TRoute>();
        configure?.Invoke(builder);
        ConventionRouteBinder<TRoute> binder = ConventionRouteBinder<TRoute>.Create(routeTemplate, builder);

        _definitions.Add(new RouteDefinition(
            typeof(TRoute),
            routeTemplate,
            context =>
            {
                TRoute route = binder.CreateRoute(context);
                binder.ApplyMetadata(context);
                return route;
            },
            (route, metadata) => binder.Format((TRoute)route, metadata),
            _nextOrder++));

        return this;
    }

    public RouteTable Build()
    {
        IGrouping<string, RouteDefinition>? duplicateTemplate = _definitions
            .GroupBy(definition => definition.Template.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateTemplate is not null)
            throw new InvalidOperationException(
                $"Route template '{duplicateTemplate.Key}' is registered more than once.");

        for (var i = 0; i < _definitions.Count; i++)
        for (int j = i + 1; j < _definitions.Count; j++)
        {
            RouteDefinition left = _definitions[i];
            RouteDefinition right = _definitions[j];
            if (left.Template.ComparePrecedence(right.Template) == 0 &&
                left.Template.CanOverlap(right.Template))
                throw new InvalidOperationException(
                    $"Route templates '{left.Template.Value}' and '{right.Template.Value}' are ambiguous.");
        }

        RouteDefinition[] sorted = _definitions
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
                return 0;

            if (x is null)
                return 1;

            if (y is null)
                return -1;

            return x.ComparePrecedence(y);
        }
    }
}

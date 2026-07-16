using System.Diagnostics.CodeAnalysis;
using System.ComponentModel;
using AdamE.AppNav.Policies;

namespace AdamE.AppNav.Routing;

public sealed class RouteTableBuilder
{
    private readonly List<RouteDefinition> _definitions = [];
    private readonly RouteValueCodecCollection _valueCodecs = new();
    private readonly HashSet<Type> _requiredValueCodecs = [];
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

    /// <summary>
    /// Registers the canonical parser and formatter for a route value type.
    /// </summary>
    public RouteTableBuilder AddValueCodec<TValue>(
        Func<string, TValue> parse,
        Func<TValue, string> format)
    {
        _valueCodecs.Add(parse, format);
        return this;
    }

    /// <summary>
    /// Registers a case-insensitive parser and named-value formatter for an enum route value type.
    /// </summary>
    public RouteTableBuilder AddEnumValueCodec<TEnum>()
        where TEnum : struct, Enum
    {
        _valueCodecs.AddEnum<TEnum>();
        return this;
    }

    /// <summary>
    /// Records that a route module requires a codec for the specified value type.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public RouteTableBuilder RequireValueCodec<TValue>()
    {
        RequireValueCodec(typeof(TValue));
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
            (route, metadata, codecs) => formatBuilder.Format((TRoute)route, routeTemplate, codecs, metadata),
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
        foreach (Type valueType in binder.RequiredValueTypes)
            RequireValueCodec(valueType);

        _definitions.Add(new RouteDefinition(
            typeof(TRoute),
            routeTemplate,
            context =>
            {
                TRoute route = binder.CreateRoute(context);
                binder.ApplyMetadata(context);
                return route;
            },
            (route, metadata, codecs) => binder.Format((TRoute)route, codecs, metadata),
            _nextOrder++));

        return this;
    }

    public RouteTable Build()
    {
        Type? missingCodec = _requiredValueCodecs.FirstOrDefault(type => !_valueCodecs.Contains(type));
        if (missingCodec is not null)
            throw new InvalidOperationException(
                $"Route value type '{missingCodec.FullName}' requires a registered codec. " +
                $"Call {nameof(AddValueCodec)} before building the route table.");

        IGrouping<string, RouteDefinition>? duplicateTemplate = _definitions
            .GroupBy(definition => definition.Template.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateTemplate is not null)
            throw new InvalidOperationException(
                $"Route template '{duplicateTemplate.Key}' is registered more than once.");

        IGrouping<Type, RouteDefinition>? duplicateRouteType = _definitions
            .GroupBy(definition => definition.RouteType)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key.FullName, StringComparer.Ordinal)
            .FirstOrDefault();

        if (duplicateRouteType is not null)
        {
            string routeTypeName = duplicateRouteType.Key.FullName ?? duplicateRouteType.Key.Name;
            string templates = string.Join(
                "', '",
                duplicateRouteType
                    .Select(definition => definition.Template.Value)
                    .OrderBy(template => template, StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"Route type '{routeTypeName}' is registered with multiple canonical templates: '{templates}'. " +
                $"Register one template per exact route type and normalize aliases with {nameof(INavigationRequestTransformer)}.");
        }

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

        return new RouteTable(sorted, _valueCodecs.Build());
    }

    private void RequireValueCodec(Type type)
    {
        _requiredValueCodecs.Add(RouteValueCodecRegistry.Normalize(type));
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

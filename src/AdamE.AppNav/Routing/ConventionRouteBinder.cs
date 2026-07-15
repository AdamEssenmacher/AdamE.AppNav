using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace AdamE.AppNav.Routing;

internal sealed class ConventionRouteBinder<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)]
TRoute>
    where TRoute : AppRoute
{
    private readonly RouteTemplate _template;
    private readonly ConstructorInfo _constructor;
    private readonly IReadOnlyList<ConstructorArgument> _arguments;
    private readonly IReadOnlyDictionary<string, PropertyInfo> _pathProperties;
    private readonly IReadOnlyList<ConventionQueryBinding> _queryBindings;
    private readonly IReadOnlyDictionary<string, ConventionQueryCollectionShape?> _queryCollections;
    private readonly IReadOnlyList<ConventionMetadataQueryBinding> _metadataQueryBindings;

    private ConventionRouteBinder(
        RouteTemplate template,
        ConstructorInfo constructor,
        IReadOnlyList<ConstructorArgument> arguments,
        IReadOnlyDictionary<string, PropertyInfo> pathProperties,
        IReadOnlyList<ConventionQueryBinding> queryBindings,
        IReadOnlyDictionary<string, ConventionQueryCollectionShape?> queryCollections,
        IReadOnlyList<ConventionMetadataQueryBinding> metadataQueryBindings)
    {
        _template = template;
        _constructor = constructor;
        _arguments = arguments;
        _pathProperties = pathProperties;
        _queryBindings = queryBindings;
        _queryCollections = queryCollections;
        _metadataQueryBindings = metadataQueryBindings;
    }

    public static ConventionRouteBinder<TRoute> Create(
        RouteTemplate template,
        ConventionRouteBuilder<TRoute> builder)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(builder);

        IReadOnlyDictionary<string, PropertyInfo> properties = GetPublicProperties();
        IReadOnlyDictionary<string, PropertyInfo> pathProperties = BindPathProperties(template, properties);
        ValidateQueryBindings(builder.QueryBindings, properties, pathProperties);
        ConstructorInfo constructor = SelectConstructor(template, builder.QueryBindings);
        ValidateQueryBoundConstructorParameters(constructor, builder.QueryBindings);
        ValidateOptionalPathConstructorParameters(constructor, template);
        IReadOnlyDictionary<string, ConventionQueryCollectionShape?> queryCollections =
            ClassifyQueryCollections(builder.QueryBindings);
        IReadOnlyList<ConstructorArgument> arguments =
            BindConstructorArguments(constructor, template, builder.QueryBindings, queryCollections);

        return new ConventionRouteBinder<TRoute>(
            template,
            constructor,
            arguments,
            pathProperties,
            builder.QueryBindings.ToArray(),
            queryCollections,
            builder.MetadataQueryBindings.ToArray());
    }

    public IReadOnlyCollection<Type> RequiredValueTypes
    {
        get
        {
            var types = new HashSet<Type>();
            foreach (ConstructorArgument argument in _arguments)
                if (argument.Kind == ConstructorArgumentKind.Path)
                    types.Add(RouteValueCodecRegistry.Normalize(argument.ParameterType));
                else if (argument.Kind == ConstructorArgumentKind.Query)
                    AddValueType(types, argument.ParameterType, argument.QueryCollection);

            foreach (PropertyInfo property in _pathProperties.Values)
                types.Add(RouteValueCodecRegistry.Normalize(property.PropertyType));

            foreach (ConventionQueryBinding binding in _queryBindings)
                AddValueType(types, binding.Property.PropertyType, _queryCollections[binding.Property.Name]);

            foreach (ConventionMetadataQueryBinding binding in _metadataQueryBindings)
                types.Add(RouteValueCodecRegistry.Normalize(binding.ValueType));

            return types;
        }
    }

    public TRoute CreateRoute(RouteMatchContext context)
    {
        var values = new object?[_arguments.Count];
        for (var i = 0; i < _arguments.Count; i++)
            values[i] = _arguments[i].Resolve(context);

        return (TRoute)_constructor.Invoke(values);
    }

    public void ApplyMetadata(RouteMatchContext context)
    {
        foreach (ConventionMetadataQueryBinding binding in _metadataQueryBindings)
        {
            if (!context.QueryValues.TryGetValue(binding.QueryName, out string? value))
            {
                if (!binding.OmitWhenNull)
                    context.AddMetadata(binding.MetadataName, null, false);

                continue;
            }

            context.AddMetadata(
                binding.MetadataName,
                context.ConvertValue(value, binding.ValueType, binding.QueryName),
                binding.OmitWhenNull);
        }
    }

    public string Format(
        TRoute route,
        RouteValueCodecRegistry codecs,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var pathValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string parameter in _template.ParameterNames)
        {
            string? value = RouteValueFormatting.Format(_pathProperties[parameter].GetValue(route), parameter, codecs);
            if (!string.IsNullOrEmpty(value))
                pathValues[parameter] = value;
        }

        string path = _template.Format(pathValues);
        var query = new List<string>();
        foreach (ConventionQueryBinding binding in _queryBindings)
            query.AddRange(from value in RouteValueFormatting.FormatMany(
                    binding.Property.GetValue(route),
                    binding.QueryName,
                    codecs,
                    _queryCollections[binding.Property.Name]?.ElementType)
                           where value is not null || !binding.OmitWhenNull
                           select $"{Uri.EscapeDataString(binding.QueryName)}={Uri.EscapeDataString(value ?? string.Empty)}");

        foreach (ConventionMetadataQueryBinding binding in _metadataQueryBindings)
        {
            object? metadataValue = null;
            metadata?.TryGetValue(binding.MetadataName, out metadataValue);
            query.AddRange(from value in RouteValueFormatting.FormatMany(
                    metadataValue, binding.QueryName, codecs)
                           where value is not null || !binding.OmitWhenNull
                           select $"{Uri.EscapeDataString(binding.QueryName)}={Uri.EscapeDataString(value ?? string.Empty)}");
        }

        return query.Count == 0 ? path : $"{path}?{string.Join("&", query)}";
    }

    private static Dictionary<string, PropertyInfo> GetPublicProperties()
    {
        PropertyInfo[] properties = typeof(TRoute)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property =>
                property.GetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0)
            .ToArray();

        IGrouping<string, PropertyInfo>? duplicate = properties
            .GroupBy(static property => property.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Route type '{typeof(TRoute).FullName}' exposes multiple public properties named '{duplicate.Key}'.");

        return properties.ToDictionary(static property => property.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, PropertyInfo> BindPathProperties(
        RouteTemplate template,
        IReadOnlyDictionary<string, PropertyInfo> properties)
    {
        var pathProperties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (string parameter in template.ParameterNames)
        {
            if (!properties.TryGetValue(parameter, out PropertyInfo? property))
                throw new InvalidOperationException(
                    $"Route template '{template.Value}' requires route type '{typeof(TRoute).FullName}' to expose public property '{parameter}'.");

            pathProperties[parameter] = property;
        }

        return pathProperties;
    }

    private static void ValidateQueryBindings(
        IReadOnlyList<ConventionQueryBinding> queryBindings,
        IReadOnlyDictionary<string, PropertyInfo> properties,
        IReadOnlyDictionary<string, PropertyInfo> pathProperties)
    {
        foreach (ConventionQueryBinding binding in queryBindings)
        {
            if (!properties.ContainsKey(binding.Property.Name))
                throw new InvalidOperationException(
                    $"Query binding member '{binding.Property.Name}' was not found on route type '{typeof(TRoute).FullName}'.");

            if (pathProperties.ContainsKey(binding.Property.Name))
                throw new InvalidOperationException(
                    $"Route member '{typeof(TRoute).FullName}.{binding.Property.Name}' is already bound by the route path.");
        }
    }

    private static ConstructorInfo SelectConstructor(
        RouteTemplate template,
        IReadOnlyList<ConventionQueryBinding> queryBindings)
    {
        IReadOnlySet<string> requiredNames = GetRequiredConstructorNames(template, queryBindings);
        var candidates = typeof(TRoute)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Select(constructor => new
            {
                Constructor = constructor,
                Parameters = constructor.GetParameters()
            })
            .Where(candidate => IsUsableConstructor(candidate.Parameters, requiredNames))
            .OrderByDescending(candidate => candidate.Parameters.Length)
            .ToArray();

        return candidates.Length switch
        {
            0 => throw new InvalidOperationException(
                $"Route type '{typeof(TRoute).FullName}' does not expose a public constructor compatible with route template '{template.Value}'."),
            > 1 when candidates[0].Parameters.Length == candidates[1].Parameters.Length => throw
                new InvalidOperationException(
                    $"Route type '{typeof(TRoute).FullName}' has multiple public constructors compatible with route template '{template.Value}'."),
            _ => candidates[0].Constructor
        };
    }

    private static void ValidateQueryBoundConstructorParameters(
        ConstructorInfo constructor,
        IReadOnlyList<ConventionQueryBinding> queryBindings)
    {
        if (queryBindings.Count == 0)
            return;

        Dictionary<string, ConventionQueryBinding> queryBindingsByPropertyName = queryBindings.ToDictionary(
            static binding => binding.Property.Name,
            StringComparer.OrdinalIgnoreCase);

        foreach (ParameterInfo parameter in constructor.GetParameters())
        {
            if (!queryBindingsByPropertyName.TryGetValue(parameter.Name!, out ConventionQueryBinding? queryBinding))
                continue;

            if (parameter.ParameterType != queryBinding.Property.PropertyType)
                throw new InvalidOperationException(
                    $"Convention query binding '{queryBinding.QueryName}' on route type '{typeof(TRoute).FullName}' " +
                    $"uses property type '{queryBinding.Property.PropertyType.FullName}', but constructor parameter " +
                    $"'{parameter.Name}' uses '{parameter.ParameterType.FullName}'. The types must match.");

            if (IsMissingSafeQueryParameter(parameter))
                continue;

            throw new InvalidOperationException(
                $"Convention query binding '{queryBinding.QueryName}' on route type '{typeof(TRoute).FullName}' " +
                $"targets constructor parameter '{parameter.Name}', but query values are always optional. " +
                "Make the parameter nullable or provide a default value.");
        }
    }

    private static void ValidateOptionalPathConstructorParameters(
        ConstructorInfo constructor,
        RouteTemplate template)
    {
        Dictionary<string, ParameterInfo> parameters = constructor.GetParameters()
            .ToDictionary(static parameter => parameter.Name!, StringComparer.OrdinalIgnoreCase);

        foreach (string parameterName in template.ParameterNames)
        {
            if (!template.IsOptionalParameter(parameterName) && !template.IsCatchAllParameter(parameterName))
                continue;

            ParameterInfo parameter = parameters[parameterName];
            if (IsMissingSafeParameter(parameter))
                continue;

            throw new InvalidOperationException(
                $"Optional path binding '{parameterName}' on route type '{typeof(TRoute).FullName}' " +
                $"targets constructor parameter '{parameter.Name}', but the path value may be absent. " +
                "Make the parameter nullable or provide a default value.");
        }
    }

    private static HashSet<string> GetRequiredConstructorNames(
        RouteTemplate template,
        IReadOnlyList<ConventionQueryBinding> queryBindings)
    {
        var names = new HashSet<string>(template.ParameterNames, StringComparer.OrdinalIgnoreCase);
        foreach (ConventionQueryBinding binding in queryBindings)
            names.Add(binding.Property.Name);

        return names;
    }

    private static bool IsUsableConstructor(
        IReadOnlyList<ParameterInfo> parameters,
        IReadOnlySet<string> requiredNames)
    {
        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (parameters.Any(parameter => parameter.Name is null || !parameterNames.Add(parameter.Name)))
            return false;

        if (requiredNames.Any(requiredName => !parameterNames.Contains(requiredName)))
            return false;

        return parameters.All(parameter =>
            requiredNames.Contains(parameter.Name!) ||
            parameter.HasDefaultValue ||
            parameter.IsOptional);
    }

    private static bool IsMissingSafeQueryParameter(ParameterInfo parameter)
    {
        return IsMissingSafeParameter(parameter);
    }

    private static bool IsMissingSafeParameter(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue || parameter.IsOptional)
            return true;

        if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null)
            return true;

        return !parameter.ParameterType.IsValueType && ConventionRouteNullability.IsNullable(parameter);
    }

    private static ConstructorArgument[] BindConstructorArguments(
        ConstructorInfo constructor,
        RouteTemplate template,
        IReadOnlyList<ConventionQueryBinding> queryBindings,
        IReadOnlyDictionary<string, ConventionQueryCollectionShape?> queryCollections)
    {
        var pathNames = new HashSet<string>(template.ParameterNames, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ConventionQueryBinding> queryByProperty = queryBindings.ToDictionary(
            static binding => binding.Property.Name,
            StringComparer.OrdinalIgnoreCase);

        return constructor.GetParameters()
            .Select(parameter =>
            {
                if (pathNames.Contains(parameter.Name!))
                    return ConstructorArgument.Path(parameter.Name!, parameter.ParameterType, parameter.DefaultValue,
                        parameter.HasDefaultValue || parameter.IsOptional);

                if (queryByProperty.TryGetValue(parameter.Name!, out ConventionQueryBinding? query))
                    return ConstructorArgument.Query(
                        query.QueryName,
                        parameter.ParameterType,
                        parameter.DefaultValue,
                        parameter.HasDefaultValue || parameter.IsOptional,
                        queryCollections[query.Property.Name]);

                return ConstructorArgument.Default(parameter.ParameterType, parameter.DefaultValue,
                    parameter.HasDefaultValue || parameter.IsOptional);
            })
            .ToArray();
    }

    private static IReadOnlyDictionary<string, ConventionQueryCollectionShape?> ClassifyQueryCollections(
        IReadOnlyList<ConventionQueryBinding> queryBindings)
    {
        return queryBindings.ToDictionary(
            static binding => binding.Property.Name,
            binding => ConventionQueryCollectionShape.TryCreate(
                binding.Property.PropertyType,
                $"Convention query binding '{binding.QueryName}' on route type '{typeof(TRoute).FullName}'"),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddValueType(
        ISet<Type> types,
        Type type,
        ConventionQueryCollectionShape? collection)
    {
        types.Add(RouteValueCodecRegistry.Normalize(collection?.ElementType ?? type));
    }

    private sealed record ConstructorArgument(
        string? Name,
        ConstructorArgumentKind Kind,
        Type ParameterType,
        object? DefaultValue,
        bool HasDefaultValue,
        ConventionQueryCollectionShape? QueryCollection)
    {
        public static ConstructorArgument Path(string name, Type parameterType, object? defaultValue,
            bool hasDefaultValue)
        {
            return new ConstructorArgument(name, ConstructorArgumentKind.Path, parameterType, defaultValue,
                hasDefaultValue, null);
        }

        public static ConstructorArgument Query(
            string name,
            Type parameterType,
            object? defaultValue,
            bool hasDefaultValue,
            ConventionQueryCollectionShape? queryCollection)
        {
            return new ConstructorArgument(name, ConstructorArgumentKind.Query, parameterType, defaultValue,
                hasDefaultValue, queryCollection);
        }

        public static ConstructorArgument Default(Type parameterType, object? defaultValue, bool hasDefaultValue)
        {
            return new ConstructorArgument(null, ConstructorArgumentKind.Default, parameterType, defaultValue,
                hasDefaultValue, null);
        }

        public object? Resolve(RouteMatchContext context)
        {
            return Kind switch
            {
                ConstructorArgumentKind.Path => ResolvePath(context),
                ConstructorArgumentKind.Query => ResolveQuery(context),
                _ => GetDefault()
            };
        }

        private object? ResolvePath(RouteMatchContext context)
        {
            return context.PathValues.TryGetValue(Name!, out string? value)
                ? context.ConvertValue(value, ParameterType, Name!)
                : GetDefault();
        }

        private object? ResolveQuery(RouteMatchContext context)
        {
            if (QueryCollection is { } collection)
            {
                IReadOnlyList<string> values = context.QueryAll(Name!);
                return values.Count == 0
                    ? GetDefault()
                    : context.ConvertValues(
                        values,
                        collection.ElementType,
                        collection.Materialization,
                        Name!);
            }

            return context.QueryValues.TryGetValue(Name!, out string? value)
                ? context.ConvertValue(value, ParameterType, Name!)
                : GetDefault();
        }

        private object? GetDefault()
        {
            return HasDefaultValue ? DefaultValue : null;
        }
    }

    private enum ConstructorArgumentKind
    {
        Path,
        Query,
        Default
    }
}

internal sealed record ConventionQueryCollectionShape(
    Type ElementType,
    RouteQueryCollectionMaterialization Materialization)
{
    public static ConventionQueryCollectionShape? TryCreate(Type type, string description)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type == typeof(string))
            return null;

        if (type.IsArray)
        {
            if (type.GetArrayRank() != 1)
                throw Unsupported(description, type);

            return Create(type.GetElementType()!, RouteQueryCollectionMaterialization.Array, description);
        }

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            if (definition == typeof(IEnumerable<>) ||
                definition == typeof(IReadOnlyCollection<>) ||
                definition == typeof(IReadOnlyList<>) ||
                definition == typeof(ICollection<>) ||
                definition == typeof(IList<>))
                return Create(
                    type.GetGenericArguments()[0],
                    RouteQueryCollectionMaterialization.Array,
                    description);

            if (definition == typeof(List<>))
                return Create(
                    type.GetGenericArguments()[0],
                    RouteQueryCollectionMaterialization.List,
                    description);
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            throw Unsupported(description, type);

        return null;
    }

    private static ConventionQueryCollectionShape Create(
        Type elementType,
        RouteQueryCollectionMaterialization materialization,
        string description)
    {
        if (Nullable.GetUnderlyingType(elementType) is not null)
            throw new InvalidOperationException(
                $"{description} uses collection type with nullable value-type element '{elementType.FullName}', " +
                "which repeated query binding does not support.");

        if (elementType != typeof(string) &&
            typeof(System.Collections.IEnumerable).IsAssignableFrom(elementType))
            throw new InvalidOperationException(
                $"{description} uses nested collection element type '{elementType.FullName}', " +
                "which repeated query binding does not support.");

        return new ConventionQueryCollectionShape(elementType, materialization);
    }

    private static InvalidOperationException Unsupported(string description, Type type)
    {
        return new InvalidOperationException(
            $"{description} uses unsupported query collection type '{type.FullName}'. " +
            "Use a one-dimensional array, a supported generic collection interface, or List<T>.");
    }
}

internal static class ConventionRouteNullability
{
    private static readonly NullabilityInfoContext Context = new();

    public static bool IsNullable(ParameterInfo parameter)
    {
        return Context.Create(parameter).ReadState == NullabilityState.Nullable;
    }
}

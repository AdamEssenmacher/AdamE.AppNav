using System.Collections;
using System.Globalization;
using System.Reflection;

namespace AdamE.MauiRouter.Routing;

internal sealed class ConventionRouteBinder<TRoute>
    where TRoute : AppRoute
{
    private static readonly NullabilityInfoContext NullabilityContext = new();

    private readonly RouteTemplate _template;
    private readonly ConstructorInfo _constructor;
    private readonly IReadOnlyList<ConstructorArgument> _arguments;
    private readonly IReadOnlyDictionary<string, PropertyInfo> _pathProperties;
    private readonly IReadOnlyList<ConventionQueryBinding> _queryBindings;
    private readonly IReadOnlyList<ConventionMetadataQueryBinding> _metadataQueryBindings;

    private ConventionRouteBinder(
        RouteTemplate template,
        ConstructorInfo constructor,
        IReadOnlyList<ConstructorArgument> arguments,
        IReadOnlyDictionary<string, PropertyInfo> pathProperties,
        IReadOnlyList<ConventionQueryBinding> queryBindings,
        IReadOnlyList<ConventionMetadataQueryBinding> metadataQueryBindings)
    {
        _template = template;
        _constructor = constructor;
        _arguments = arguments;
        _pathProperties = pathProperties;
        _queryBindings = queryBindings;
        _metadataQueryBindings = metadataQueryBindings;
    }

    public static ConventionRouteBinder<TRoute> Create(
        RouteTemplate template,
        ConventionRouteBuilder<TRoute> builder)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(builder);

        var properties = GetPublicProperties();
        var pathProperties = BindPathProperties(template, properties);
        ValidateQueryBindings(builder.QueryBindings, properties, pathProperties);
        var constructor = SelectConstructor(template, builder.QueryBindings);
        ValidateQueryBoundConstructorParameters(constructor, builder.QueryBindings);
        var arguments = BindConstructorArguments(constructor, template, builder.QueryBindings);

        return new ConventionRouteBinder<TRoute>(
            template,
            constructor,
            arguments,
            pathProperties,
            builder.QueryBindings.ToArray(),
            builder.MetadataQueryBindings.ToArray());
    }

    public TRoute CreateRoute(RouteMatchContext context)
    {
        var values = new object?[_arguments.Count];
        for (var i = 0; i < _arguments.Count; i++)
        {
            values[i] = _arguments[i].Resolve(context);
        }

        return (TRoute)_constructor.Invoke(values);
    }

    public void ApplyMetadata(RouteMatchContext context)
    {
        foreach (var binding in _metadataQueryBindings)
        {
            if (!context.QueryValues.TryGetValue(binding.QueryName, out var value))
            {
                if (!binding.OmitWhenNull)
                {
                    context.AddMetadata(binding.MetadataName, null, omitWhenNull: false);
                }

                continue;
            }

            context.AddMetadata(
                binding.MetadataName,
                RouteValueConverter.Convert(value, binding.ValueType, binding.QueryName),
                binding.OmitWhenNull);
        }
    }

    public string Format(TRoute route, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var pathValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in _template.ParameterNames)
        {
            var value = ConvertToString(_pathProperties[parameter].GetValue(route));
            if (!string.IsNullOrEmpty(value))
            {
                pathValues[parameter] = value;
            }
        }

        var path = _template.Format(pathValues);
        var query = new List<string>();
        foreach (var binding in _queryBindings)
        {
            foreach (var value in ConvertToStrings(binding.Property.GetValue(route)))
            {
                if (value is null && binding.OmitWhenNull)
                {
                    continue;
                }

                query.Add($"{Uri.EscapeDataString(binding.QueryName)}={Uri.EscapeDataString(value ?? string.Empty)}");
            }
        }

        foreach (var binding in _metadataQueryBindings)
        {
            object? metadataValue = null;
            metadata?.TryGetValue(binding.MetadataName, out metadataValue);
            foreach (var value in ConvertToStrings(metadataValue))
            {
                if (value is null && binding.OmitWhenNull)
                {
                    continue;
                }

                query.Add($"{Uri.EscapeDataString(binding.QueryName)}={Uri.EscapeDataString(value ?? string.Empty)}");
            }
        }

        return query.Count == 0 ? path : $"{path}?{string.Join("&", query)}";
    }

    private static IReadOnlyDictionary<string, PropertyInfo> GetPublicProperties()
    {
        var properties = typeof(TRoute)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.GetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0)
            .ToArray();

        var duplicate = properties
            .GroupBy(static property => property.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Route type '{typeof(TRoute).FullName}' exposes multiple public properties named '{duplicate.Key}'.");
        }

        return properties.ToDictionary(static property => property.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, PropertyInfo> BindPathProperties(
        RouteTemplate template,
        IReadOnlyDictionary<string, PropertyInfo> properties)
    {
        var pathProperties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in template.ParameterNames)
        {
            if (!properties.TryGetValue(parameter, out var property))
            {
                throw new InvalidOperationException(
                    $"Route template '{template.Value}' requires route type '{typeof(TRoute).FullName}' to expose public property '{parameter}'.");
            }

            pathProperties[parameter] = property;
        }

        return pathProperties;
    }

    private static void ValidateQueryBindings(
        IReadOnlyList<ConventionQueryBinding> queryBindings,
        IReadOnlyDictionary<string, PropertyInfo> properties,
        IReadOnlyDictionary<string, PropertyInfo> pathProperties)
    {
        foreach (var binding in queryBindings)
        {
            if (!properties.ContainsKey(binding.Property.Name))
            {
                throw new InvalidOperationException(
                    $"Query binding member '{binding.Property.Name}' was not found on route type '{typeof(TRoute).FullName}'.");
            }

            if (pathProperties.ContainsKey(binding.Property.Name))
            {
                throw new InvalidOperationException(
                    $"Route member '{typeof(TRoute).FullName}.{binding.Property.Name}' is already bound by the route path.");
            }
        }
    }

    private static ConstructorInfo SelectConstructor(
        RouteTemplate template,
        IReadOnlyList<ConventionQueryBinding> queryBindings)
    {
        var requiredNames = GetRequiredConstructorNames(template, queryBindings);
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

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"Route type '{typeof(TRoute).FullName}' does not expose a public constructor compatible with route template '{template.Value}'.");
        }

        if (candidates.Length > 1 && candidates[0].Parameters.Length == candidates[1].Parameters.Length)
        {
            throw new InvalidOperationException(
                $"Route type '{typeof(TRoute).FullName}' has multiple public constructors compatible with route template '{template.Value}'.");
        }

        return candidates[0].Constructor;
    }

    private static void ValidateQueryBoundConstructorParameters(
        ConstructorInfo constructor,
        IReadOnlyList<ConventionQueryBinding> queryBindings)
    {
        if (queryBindings.Count == 0)
        {
            return;
        }

        var queryBindingsByPropertyName = queryBindings.ToDictionary(
            static binding => binding.Property.Name,
            StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in constructor.GetParameters())
        {
            if (!queryBindingsByPropertyName.TryGetValue(parameter.Name!, out var queryBinding) ||
                IsMissingSafeQueryParameter(parameter))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Convention query binding '{queryBinding.QueryName}' on route type '{typeof(TRoute).FullName}' " +
                $"targets constructor parameter '{parameter.Name}', but query values are always optional. " +
                "Make the parameter nullable or provide a default value.");
        }
    }

    private static IReadOnlySet<string> GetRequiredConstructorNames(
        RouteTemplate template,
        IReadOnlyList<ConventionQueryBinding> queryBindings)
    {
        var names = new HashSet<string>(template.ParameterNames, StringComparer.OrdinalIgnoreCase);
        foreach (var binding in queryBindings)
        {
            names.Add(binding.Property.Name);
        }

        return names;
    }

    private static bool IsUsableConstructor(
        IReadOnlyList<ParameterInfo> parameters,
        IReadOnlySet<string> requiredNames)
    {
        var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in parameters)
        {
            if (parameter.Name is null || !parameterNames.Add(parameter.Name))
            {
                return false;
            }
        }

        if (requiredNames.Any(requiredName => !parameterNames.Contains(requiredName)))
        {
            return false;
        }

        return parameters.All(parameter =>
            requiredNames.Contains(parameter.Name!) ||
            parameter.HasDefaultValue ||
            parameter.IsOptional);
    }

    private static bool IsMissingSafeQueryParameter(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue || parameter.IsOptional)
        {
            return true;
        }

        if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null)
        {
            return true;
        }

        if (parameter.ParameterType.IsValueType)
        {
            return false;
        }

        return NullabilityContext.Create(parameter).ReadState == NullabilityState.Nullable;
    }

    private static IReadOnlyList<ConstructorArgument> BindConstructorArguments(
        ConstructorInfo constructor,
        RouteTemplate template,
        IReadOnlyList<ConventionQueryBinding> queryBindings)
    {
        var pathNames = new HashSet<string>(template.ParameterNames, StringComparer.OrdinalIgnoreCase);
        var queryByProperty = queryBindings.ToDictionary(
            static binding => binding.Property.Name,
            StringComparer.OrdinalIgnoreCase);

        return constructor.GetParameters()
            .Select(parameter =>
            {
                if (pathNames.Contains(parameter.Name!))
                {
                    return ConstructorArgument.Path(parameter.Name!, parameter.ParameterType, parameter.DefaultValue, parameter.HasDefaultValue);
                }

                if (queryByProperty.TryGetValue(parameter.Name!, out var query))
                {
                    return ConstructorArgument.Query(
                        query.QueryName,
                        parameter.ParameterType,
                        parameter.DefaultValue,
                        parameter.HasDefaultValue);
                }

                return ConstructorArgument.Default(parameter.ParameterType, parameter.DefaultValue, parameter.HasDefaultValue);
            })
            .ToArray();
    }

    private static string? ConvertToString(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    private static IEnumerable<string?> ConvertToStrings(object? value)
    {
        if (value is null || value is string)
        {
            yield return ConvertToString(value);
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return ConvertToString(item);
            }

            yield break;
        }

        yield return ConvertToString(value);
    }

    private sealed record ConstructorArgument(
        string? Name,
        ConstructorArgumentKind Kind,
        Type ParameterType,
        object? DefaultValue,
        bool HasDefaultValue)
    {
        public static ConstructorArgument Path(string name, Type parameterType, object? defaultValue, bool hasDefaultValue)
        {
            return new ConstructorArgument(name, ConstructorArgumentKind.Path, parameterType, defaultValue, hasDefaultValue);
        }

        public static ConstructorArgument Query(string name, Type parameterType, object? defaultValue, bool hasDefaultValue)
        {
            return new ConstructorArgument(name, ConstructorArgumentKind.Query, parameterType, defaultValue, hasDefaultValue);
        }

        public static ConstructorArgument Default(Type parameterType, object? defaultValue, bool hasDefaultValue)
        {
            return new ConstructorArgument(null, ConstructorArgumentKind.Default, parameterType, defaultValue, hasDefaultValue);
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
            if (context.PathValues.TryGetValue(Name!, out var value))
            {
                return RouteValueConverter.Convert(value, ParameterType, Name!);
            }

            return GetDefault();
        }

        private object? ResolveQuery(RouteMatchContext context)
        {
            if (context.QueryValues.TryGetValue(Name!, out var value))
            {
                return RouteValueConverter.Convert(value, ParameterType, Name!);
            }

            return GetDefault();
        }

        private object? GetDefault()
        {
            return HasDefaultValue ? DefaultValue : RouteValueConverter.DefaultFor(ParameterType);
        }
    }

    private enum ConstructorArgumentKind
    {
        Path,
        Query,
        Default
    }
}

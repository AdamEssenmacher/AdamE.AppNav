using System.ComponentModel;

namespace AdamE.AppNav.Routing;

public sealed class RouteFormatBuilder<TRoute>
    where TRoute : AppRoute
{
    private readonly Dictionary<string, PathFormatter<TRoute>> _pathParams = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<QueryFormatter<TRoute>> _queryParams = [];
    private readonly List<MetadataQueryFormatter> _metadataQueryParams = [];
    private readonly HashSet<string> _queryNames = new(StringComparer.OrdinalIgnoreCase);

    public RouteFormatBuilder<TRoute> PathParam(string name, Func<TRoute, object?> value)
    {
        return AddPathParam(name, value, null);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public RouteFormatBuilder<TRoute> PathParam<TValue>(string name, Func<TRoute, TValue> value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        return AddPathParam(name, route => value(route), typeof(TValue));
    }

#pragma warning disable RS0026 // The typed generator-facing overload preserves declared route value types.
    public RouteFormatBuilder<TRoute> QueryParam(string name, Func<TRoute, object?> value, bool omitWhenNull = true)
    {
        return AddQueryParam(name, value, omitWhenNull, null, null);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public RouteFormatBuilder<TRoute> QueryParam<TValue>(
        string name,
        Func<TRoute, TValue> value,
        bool omitWhenNull = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        Type valueType = typeof(TValue);
        Type? collectionElementType = GetCollectionElementType(valueType);
        Type? declaredType = valueType != typeof(string) &&
                             typeof(System.Collections.IEnumerable).IsAssignableFrom(valueType) &&
                             collectionElementType is null
            ? null
            : valueType;
        return AddQueryParam(
            name,
            route => value(route),
            omitWhenNull,
            declaredType,
            collectionElementType);
    }
#pragma warning restore RS0026

    public RouteFormatBuilder<TRoute> QueryMetadata<TValue>(
        RouteMetadataKey<TValue> key,
        bool omitWhenNull = true)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!_queryNames.Add(key.Name))
            throw new InvalidOperationException(
                $"Query binding for query parameter '{key.Name}' is already registered for route type '{typeof(TRoute).FullName}'.");

        _metadataQueryParams.Add(
            new MetadataQueryFormatter(key.Name, key.Name, RouteMetadataKey<TValue>.ValueType, omitWhenNull));
        return this;
    }

    internal string Format(
        TRoute route,
        RouteTemplate template,
        RouteValueCodecRegistry codecs,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        string path = template.Format(BuildPathValues(route, template, codecs));
        var query = new List<string>();
        foreach (QueryFormatter<TRoute> queryParam in _queryParams)
        {
            AppendQueryValues(
                query,
                queryParam.Name,
                FormatQueryValues(queryParam, route, codecs),
                queryParam.OmitWhenNull);
        }

        foreach (MetadataQueryFormatter queryParam in _metadataQueryParams)
            AppendMetadataQueryValue(query, queryParam, metadata, codecs);

        return query.Count == 0 ? path : $"{path}?{string.Join("&", query)}";
    }

    private Dictionary<string, string> BuildPathValues(
        TRoute route,
        RouteTemplate template,
        RouteValueCodecRegistry codecs)
    {
        var pathValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string parameter in template.ParameterNames)
        {
            if (!_pathParams.TryGetValue(parameter, out PathFormatter<TRoute>? formatter))
                throw new InvalidOperationException($"No formatter was registered for path parameter '{parameter}'.");

            object? routeValue = formatter.Value(route);
            Type? formattingType = formatter.DeclaredType is not null && codecs.Contains(formatter.DeclaredType)
                ? formatter.DeclaredType
                : null;
            string? value = formattingType is null
                ? RouteValueFormatting.Format(routeValue, parameter, codecs)
                : RouteValueFormatting.Format(routeValue, formattingType, parameter, codecs);
            if (string.IsNullOrEmpty(value))
            {
                if (template.IsOptionalParameter(parameter) || template.IsCatchAllParameter(parameter))
                    continue;

                throw new InvalidOperationException($"Path parameter '{parameter}' formatted to an empty value.");
            }

            pathValues[parameter] = value;
        }

        return pathValues;
    }

    private static IEnumerable<string?> FormatQueryValues(
        QueryFormatter<TRoute> queryParam,
        TRoute route,
        RouteValueCodecRegistry codecs)
    {
        object? queryValue = queryParam.Value(route);
        if (queryParam.DeclaredType is null)
            return RouteValueFormatting.FormatMany(queryValue, queryParam.Name, codecs);

        if (queryParam.CollectionElementType is null)
        {
            Type formattingType = codecs.Contains(queryParam.DeclaredType) || queryValue is null
                ? queryParam.DeclaredType
                : queryValue.GetType();
            return RouteValueFormatting.FormatMany(
                queryValue,
                formattingType,
                queryParam.Name,
                codecs);
        }

        return codecs.Contains(queryParam.CollectionElementType)
            ? RouteValueFormatting.FormatMany(
                queryValue,
                queryParam.DeclaredType,
                queryParam.Name,
                codecs,
                queryParam.CollectionElementType)
            : FormatRuntimeCollection(queryValue, queryParam.Name, codecs);
    }

    private static void AppendQueryValues(
        ICollection<string> query,
        string name,
        IEnumerable<string?> values,
        bool omitWhenNull)
    {
        foreach (string? value in values)
        {
            if (value is null && omitWhenNull)
                continue;

            query.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value ?? string.Empty)}");
        }
    }

    private static void AppendMetadataQueryValue(
        ICollection<string> query,
        MetadataQueryFormatter queryParam,
        IReadOnlyDictionary<string, object?>? metadata,
        RouteValueCodecRegistry codecs)
    {
        object? metadataValue = null;
        metadata?.TryGetValue(queryParam.MetadataName, out metadataValue);
        string? value = RouteValueFormatting.Format(
            metadataValue,
            queryParam.ValueType,
            queryParam.QueryName,
            codecs);
        if (value is null && queryParam.OmitWhenNull)
            return;

        query.Add($"{Uri.EscapeDataString(queryParam.QueryName)}={Uri.EscapeDataString(value ?? string.Empty)}");
    }

    private static IEnumerable<string?> FormatRuntimeCollection(
        object? value,
        string name,
        RouteValueCodecRegistry codecs)
    {
        if (value is null)
        {
            yield return null;
            yield break;
        }

        if (value is not System.Collections.IEnumerable collection)
            throw new InvalidOperationException(
                $"Route query value '{name}' must be a supported collection.");

        foreach (object? item in collection)
            yield return RouteValueFormatting.Format(item, name, codecs);
    }

    internal void Validate(RouteTemplate template)
    {
        foreach (string parameter in template.ParameterNames)
            if (!_pathParams.ContainsKey(parameter))
                throw new InvalidOperationException(
                    $"Route template '{template.Value}' requires a formatter for path parameter '{parameter}'.");
    }

    private RouteFormatBuilder<TRoute> AddPathParam(
        string name,
        Func<TRoute, object?> value,
        Type? declaredType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        if (!_pathParams.TryAdd(name, new PathFormatter<TRoute>(value, declaredType)))
            throw new InvalidOperationException(
                $"Path formatter for path parameter '{name}' is already registered for route type '{typeof(TRoute).FullName}'.");

        return this;
    }

    private RouteFormatBuilder<TRoute> AddQueryParam(
        string name,
        Func<TRoute, object?> value,
        bool omitWhenNull,
        Type? declaredType,
        Type? collectionElementType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        if (!_queryNames.Add(name))
            throw new InvalidOperationException(
                $"Query binding for query parameter '{name}' is already registered for route type '{typeof(TRoute).FullName}'.");

        _queryParams.Add(
            new QueryFormatter<TRoute>(name, value, omitWhenNull, declaredType, collectionElementType));
        return this;
    }

    private static Type? GetCollectionElementType(Type valueType)
    {
        if (valueType == typeof(string))
            return null;

        if (valueType.IsArray)
            return valueType.GetElementType();

        if (!valueType.IsGenericType)
            return null;

        Type definition = valueType.GetGenericTypeDefinition();
        return definition == typeof(IEnumerable<>) ||
               definition == typeof(IReadOnlyCollection<>) ||
               definition == typeof(IReadOnlyList<>) ||
               definition == typeof(ICollection<>) ||
               definition == typeof(IList<>) ||
               definition == typeof(List<>)
            ? valueType.GetGenericArguments()[0]
            : null;
    }

    private sealed record PathFormatter<T>(
        Func<T, object?> Value,
        Type? DeclaredType);

    private sealed record QueryFormatter<T>(
        string Name,
        Func<T, object?> Value,
        bool OmitWhenNull,
        Type? DeclaredType,
        Type? CollectionElementType);

    private sealed record MetadataQueryFormatter(
        string MetadataName,
        string QueryName,
        Type ValueType,
        bool OmitWhenNull);
}

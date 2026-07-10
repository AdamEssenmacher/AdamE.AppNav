namespace AdamE.AppNav.Routing;

public sealed class RouteFormatBuilder<TRoute>
    where TRoute : AppRoute
{
    private readonly Dictionary<string, Func<TRoute, object?>> _pathParams = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<QueryFormatter<TRoute>> _queryParams = [];
    private readonly List<MetadataQueryFormatter> _metadataQueryParams = [];

    public RouteFormatBuilder<TRoute> PathParam(string name, Func<TRoute, object?> value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        _pathParams[name] = value;
        return this;
    }

    public RouteFormatBuilder<TRoute> QueryParam(string name, Func<TRoute, object?> value, bool omitWhenNull = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        _queryParams.Add(new QueryFormatter<TRoute>(name, value, omitWhenNull));
        return this;
    }

    public RouteFormatBuilder<TRoute> QueryMetadata<TValue>(
        RouteMetadataKey<TValue> key,
        bool omitWhenNull = true)
    {
        ArgumentNullException.ThrowIfNull(key);

        _metadataQueryParams.Add(new MetadataQueryFormatter(key.Name, key.Name, omitWhenNull));
        return this;
    }

    internal string Format(
        TRoute route,
        RouteTemplate template,
        RouteValueCodecRegistry codecs,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var pathValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string parameter in template.ParameterNames)
        {
            if (!_pathParams.TryGetValue(parameter, out Func<TRoute, object?>? formatter))
                throw new InvalidOperationException($"No formatter was registered for path parameter '{parameter}'.");

            string? value = RouteValueFormatting.Format(formatter(route), parameter, codecs);
            if (string.IsNullOrEmpty(value))
            {
                if (template.IsOptionalParameter(parameter) || template.IsCatchAllParameter(parameter))
                    continue;

                throw new InvalidOperationException($"Path parameter '{parameter}' formatted to an empty value.");
            }

            pathValues[parameter] = value;
        }

        string path = template.Format(pathValues);
        var query = new List<string>();
        foreach (QueryFormatter<TRoute> queryParam in _queryParams)
            query.AddRange(from value in RouteValueFormatting.FormatMany(queryParam.Value(route), queryParam.Name, codecs)
                           where value is not null || !queryParam.OmitWhenNull
                           select $"{Uri.EscapeDataString(queryParam.Name)}={Uri.EscapeDataString(value ?? string.Empty)}");

        foreach (MetadataQueryFormatter queryParam in _metadataQueryParams)
        {
            object? metadataValue = null;
            metadata?.TryGetValue(queryParam.MetadataName, out metadataValue);
            query.AddRange(from value in RouteValueFormatting.FormatMany(metadataValue, queryParam.QueryName, codecs)
                           where value is not null || !queryParam.OmitWhenNull
                           select $"{Uri.EscapeDataString(queryParam.QueryName)}={Uri.EscapeDataString(value ?? string.Empty)}");
        }

        return query.Count == 0 ? path : $"{path}?{string.Join("&", query)}";
    }

    internal void Validate(RouteTemplate template)
    {
        foreach (string parameter in template.ParameterNames)
            if (!_pathParams.ContainsKey(parameter))
                throw new InvalidOperationException(
                    $"Route template '{template.Value}' requires a formatter for path parameter '{parameter}'.");
    }

    private sealed record QueryFormatter<T>(
        string Name,
        Func<T, object?> Value,
        bool OmitWhenNull);

    private sealed record MetadataQueryFormatter(
        string MetadataName,
        string QueryName,
        bool OmitWhenNull);
}

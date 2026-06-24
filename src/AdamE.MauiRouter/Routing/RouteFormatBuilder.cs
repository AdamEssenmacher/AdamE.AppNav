using System.Collections;
using System.Globalization;

namespace AdamE.MauiRouter.Routing;

public sealed class RouteFormatBuilder<TRoute>
    where TRoute : AppRoute
{
    private readonly Dictionary<string, Func<TRoute, object?>> _pathParams = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<QueryFormatter<TRoute>> _queryParams = new();
    private readonly List<MetadataQueryFormatter> _metadataQueryParams = new();

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
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var pathValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in template.ParameterNames)
        {
            if (!_pathParams.TryGetValue(parameter, out var formatter))
            {
                throw new InvalidOperationException($"No formatter was registered for path parameter '{parameter}'.");
            }

            var value = ConvertToString(formatter(route));
            if (string.IsNullOrEmpty(value))
            {
                if (template.IsOptionalParameter(parameter) || template.IsCatchAllParameter(parameter))
                {
                    continue;
                }

                throw new InvalidOperationException($"Path parameter '{parameter}' formatted to an empty value.");
            }

            pathValues[parameter] = value;
        }

        var path = template.Format(pathValues);
        var query = new List<string>();
        foreach (var queryParam in _queryParams)
        {
            foreach (var value in ConvertToStrings(queryParam.Value(route)))
            {
                if (value is null && queryParam.OmitWhenNull)
                {
                    continue;
                }

                query.Add($"{Uri.EscapeDataString(queryParam.Name)}={Uri.EscapeDataString(value ?? string.Empty)}");
            }
        }

        foreach (var queryParam in _metadataQueryParams)
        {
            object? metadataValue = null;
            metadata?.TryGetValue(queryParam.MetadataName, out metadataValue);
            foreach (var value in ConvertToStrings(metadataValue))
            {
                if (value is null && queryParam.OmitWhenNull)
                {
                    continue;
                }

                query.Add($"{Uri.EscapeDataString(queryParam.QueryName)}={Uri.EscapeDataString(value ?? string.Empty)}");
            }
        }

        return query.Count == 0 ? path : $"{path}?{string.Join("&", query)}";
    }

    internal void Validate(RouteTemplate template)
    {
        foreach (var parameter in template.ParameterNames)
        {
            if (!_pathParams.ContainsKey(parameter))
            {
                throw new InvalidOperationException(
                    $"Route template '{template.Value}' requires a formatter for path parameter '{parameter}'.");
            }
        }
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

    private sealed record QueryFormatter<T>(
        string Name,
        Func<T, object?> Value,
        bool OmitWhenNull);

    private sealed record MetadataQueryFormatter(
        string MetadataName,
        string QueryName,
        bool OmitWhenNull);
}

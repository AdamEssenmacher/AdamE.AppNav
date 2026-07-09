using JetBrains.Annotations;

namespace AdamE.AppNav.Routing;

public sealed class RouteMatchContext
{
    private Dictionary<string, object?>? _metadata;

    internal RouteMatchContext(
        Uri sourceUri,
        IReadOnlyDictionary<string, string> path,
        IReadOnlyDictionary<string, IReadOnlyList<string>> query)
    {
        SourceUri = sourceUri;
        PathValues = path;
        QueryValueLists = query;
        QueryValues = query.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Count == 0 ? string.Empty : pair.Value[^1],
            StringComparer.OrdinalIgnoreCase);
    }

    public Uri SourceUri { [UsedImplicitly] get; }

    public IReadOnlyDictionary<string, string> PathValues { get; }

    public IReadOnlyDictionary<string, string> QueryValues { get; }

    // ReSharper disable once MemberCanBePrivate.Global
    public IReadOnlyDictionary<string, IReadOnlyList<string>> QueryValueLists { get; }

    internal IReadOnlyDictionary<string, object?> Metadata =>
        _metadata is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(_metadata, StringComparer.Ordinal);

    public string Path(string name)
    {
        return !PathValues.TryGetValue(name, out string? value)
            ? throw new KeyNotFoundException($"Path parameter '{name}' was not present in the matched route.")
            : value;
    }

    public T Path<T>(string name)
    {
        return RouteValueConverter.Convert<T>(Path(name), name);
    }

    public string? PathOptional(string name)
    {
        return PathValues.GetValueOrDefault(name);
    }

    public T? PathOptional<T>(string name)
        where T : struct
    {
        string? value = PathOptional(name);
        return value is null ? null : RouteValueConverter.Convert<T>(value, name);
    }

    public string? Query(string name)
    {
        return QueryValues.GetValueOrDefault(name);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public T? Query<T>(string name)
    {
        string? value = Query(name);
        return value is null ? default : RouteValueConverter.Convert<T>(value, name);
    }

    public IReadOnlyList<string> QueryAll(string name)
    {
        return QueryValueLists.TryGetValue(name, out IReadOnlyList<string>? values) ? values : [];
    }

    // ReSharper disable once UnusedMember.Global
    public IReadOnlyList<T> QueryAll<T>(string name)
    {
        return QueryAll(name)
            .Select(value => RouteValueConverter.Convert<T>(value, name))
            .ToArray();
    }

    // ReSharper disable once UnusedMethodReturnValue.Global
    public T? QueryMetadata<T>(RouteMetadataKey<T> key, bool omitWhenNull = true)
    {
        ArgumentNullException.ThrowIfNull(key);

        var value = Query<T>(key.Name);
        AddMetadata(key, value, omitWhenNull);
        return value;
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public void AddMetadata<T>(RouteMetadataKey<T> key, T? value, bool omitWhenNull = true)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (value is null && omitWhenNull)
            return;

        _metadata ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        _metadata[key.Name] = value;
    }

    public void AddMetadata(string name, object? value, bool omitWhenNull = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (value is null && omitWhenNull)
            return;

        _metadata ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        _metadata[name] = value;
    }
}

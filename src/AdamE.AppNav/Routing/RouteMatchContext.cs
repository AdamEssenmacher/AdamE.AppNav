using JetBrains.Annotations;
using System.ComponentModel;

namespace AdamE.AppNav.Routing;

public sealed class RouteMatchContext
{
    private static readonly IReadOnlyDictionary<string, string> EmptyQueryValues =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private Dictionary<string, object?>? _metadata;
    private readonly RouteValueCodecRegistry _valueCodecs;

    internal RouteMatchContext(
        Uri sourceUri,
        IReadOnlyDictionary<string, string> path,
        IReadOnlyDictionary<string, IReadOnlyList<string>> query,
        RouteValueCodecRegistry valueCodecs)
    {
        _valueCodecs = valueCodecs;
        SourceUri = sourceUri;
        PathValues = path;
        QueryValueLists = query;
        QueryValues = query.Count == 0
            ? EmptyQueryValues
            : query.ToDictionary(
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
            ? RouteMatchResult.EmptyMetadata
            : new Dictionary<string, object?>(_metadata, StringComparer.Ordinal);

    public string Path(string name)
    {
        return !PathValues.TryGetValue(name, out string? value)
            ? throw new KeyNotFoundException($"Path parameter '{name}' was not present in the matched route.")
            : value;
    }

    public T Path<T>(string name)
    {
        return ConvertValue<T>(Path(name), name);
    }

    public string? PathOptional(string name)
    {
        return PathValues.GetValueOrDefault(name);
    }

    public T? PathOptional<T>(string name)
        where T : struct
    {
        string? value = PathOptional(name);
        return value is null ? null : ConvertValue<T>(value, name);
    }

    public string? Query(string name)
    {
        return QueryValues.GetValueOrDefault(name);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public T? Query<T>(string name)
    {
        string? value = Query(name);
        return value is null ? default : ConvertValue<T>(value, name);
    }

    public IReadOnlyList<string> QueryAll(string name)
    {
        return QueryValueLists.TryGetValue(name, out IReadOnlyList<string>? values) ? values : [];
    }

    // ReSharper disable once UnusedMember.Global
    public IReadOnlyList<T> QueryAll<T>(string name)
    {
        return QueryAll(name)
            .Select(value => ConvertValue<T>(value, name))
            .ToArray();
    }

    /// <summary>
    /// Converts a raw route value with the codec registered for <typeparamref name="TValue"/>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public TValue ConvertValue<TValue>(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _valueCodecs.Convert<TValue>(value, name);
    }

    internal object? ConvertValue(string value, Type targetType, string name)
    {
        return _valueCodecs.Convert(value, targetType, name);
    }

    internal object ConvertValues(
        IReadOnlyList<string> values,
        Type elementType,
        RouteQueryCollectionMaterialization materialization,
        string name)
    {
        return _valueCodecs.ConvertMany(values, elementType, materialization, name);
    }

    // ReSharper disable once UnusedMethodReturnValue.Global
    public T? QueryMetadata<T>(RouteMetadataKey<T> key, bool omitWhenNull = true)
    {
        ArgumentNullException.ThrowIfNull(key);

        var value = Query<T>(key.Name);
        AddMetadata(key, value, omitWhenNull);
        return value;
    }

#pragma warning disable RS0026 // Typed and untyped metadata insertion are intentionally paired public operations.
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
#pragma warning restore RS0026
}

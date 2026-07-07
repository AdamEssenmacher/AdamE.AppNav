using System.ComponentModel;
using System.Globalization;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Requests;

public sealed class DeferredNavigationRequestSerializer(
    RouteTable routes,
    DeferredNavigationRequestPersistenceOptions? options = null)
{
    private readonly RouteTable _routes = routes ?? throw new ArgumentNullException(nameof(routes));

    private readonly DeferredNavigationRequestPersistenceOptions _options =
        options ?? new DeferredNavigationRequestPersistenceOptions();

    public DeferredNavigationRequestStoreSnapshot CreateSnapshot(IReadOnlyList<RouterNavigationRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);

        return new DeferredNavigationRequestStoreSnapshot
        {
            SchemaVersion = DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            Requests = requests.Select(CreateRequestSnapshot).ToArray()
        };
    }

    public IReadOnlyList<RouterNavigationRequest> Restore(DeferredNavigationRequestStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.SchemaVersion != DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Deferred navigation request snapshot schema version {snapshot.SchemaVersion} is not supported.");

        if (snapshot.Requests.Count == 0)
            return [];

        var restored = new List<RouterNavigationRequest>(snapshot.Requests.Count);
        foreach (NavigationRequestSnapshot requestSnapshot in snapshot.Requests)
            if (TryRestoreRequest(requestSnapshot, out RouterNavigationRequest? request))
                restored.Add(request!);

        return restored;
    }

    private NavigationRequestSnapshot CreateRequestSnapshot(RouterNavigationRequest request)
    {
        (AppRoute route, IReadOnlyDictionary<string, object?>? routeMetadata) = ResolveRequestRoute(request);
        IReadOnlyDictionary<string, object?>? effectiveMetadata = MergeMetadata(routeMetadata, request.Metadata);
        return new NavigationRequestSnapshot(
            request.Uri?.ToString(),
            FormatCanonicalRouteUri(route, effectiveMetadata),
            request.Source,
            request.WindowId,
            SerializeMetadata(effectiveMetadata ?? new Dictionary<string, object?>(StringComparer.Ordinal)),
            request.Timestamp,
            request.Disposition,
            NavigationRequestProvenanceSnapshotMapper.Create(request.Provenance));
    }

    private bool TryRestoreRequest(
        NavigationRequestSnapshot snapshot,
        out RouterNavigationRequest? request)
    {
        request = null;

        if (!TryRestoreRoute(snapshot.RouteUri, out AppRoute? route,
                out IReadOnlyDictionary<string, object?>? routeMetadata))
            return false;

        IReadOnlyDictionary<string, object?>? persistedMetadata;
        try
        {
            persistedMetadata = DeserializeMetadata(snapshot.Metadata);
        }
        catch
        {
            return false;
        }

        Uri? requestUri = null;
        if (!string.IsNullOrWhiteSpace(snapshot.Uri) &&
            !Uri.TryCreate(snapshot.Uri, UriKind.RelativeOrAbsolute, out requestUri))
            return false;

        request = new RouterNavigationRequest(
            requestUri,
            route,
            snapshot.Source,
            snapshot.WindowId,
            MergeMetadata(routeMetadata, persistedMetadata),
            snapshot.Timestamp,
            snapshot.Disposition,
            NavigationRequestProvenanceSnapshotMapper.Restore(snapshot.Provenance));

        return true;
    }

    private (AppRoute Route, IReadOnlyDictionary<string, object?>? Metadata) ResolveRequestRoute(
        RouterNavigationRequest request)
    {
        if (request.Route is not null)
            return (request.Route, null);

        if (request.Uri is null)
            throw new InvalidOperationException("Deferred navigation requests must provide a route or a URI.");

        RouteMatchResult match = _routes.Match(request.Uri);
        if (!match.IsSuccess || match.Route is null)
            throw new InvalidOperationException(
                $"Deferred navigation request URI '{request.Uri}' does not match a registered route.");

        return (match.Route, match.Metadata);
    }

    private bool TryRestoreRoute(
        string routeUri,
        out AppRoute? route,
        out IReadOnlyDictionary<string, object?>? metadata)
    {
        metadata = null;

        if (!Uri.TryCreate(routeUri, UriKind.RelativeOrAbsolute, out Uri? uri))
        {
            route = null;
            return false;
        }

        RouteMatchResult match = _routes.Match(uri);
        if (!match.IsSuccess || match.Route is null)
        {
            route = null;
            return false;
        }

        route = match.Route;
        metadata = match.Metadata;

        return true;
    }

    private Dictionary<string, NavigationMetadataValueSnapshot>? SerializeMetadata(
        IReadOnlyDictionary<string, object?> metadata)
    {
        if (metadata.Count == 0)
            return null;

        Dictionary<string, NavigationMetadataValueSnapshot>? serialized = null;
        if (_options.RouteStateRegistry is { } routeStateRegistry)
            foreach (KeyValuePair<string, object?> pair in metadata)
            {
                if (!routeStateRegistry.TryGetRegistration(pair.Key, out RouteStateRegistration registration) ||
                    registration.Lifetime != RouteStateLifetime.Restorable)
                    continue;

                serialized ??= new Dictionary<string, NavigationMetadataValueSnapshot>(StringComparer.Ordinal);
                serialized[pair.Key] = SerializeValueSnapshot(pair.Key, pair.Value, registration.ValueType);
            }

        if (_options.MetadataSerializer is null)
            return serialized;

        IReadOnlyDictionary<string, object?>? unknownMetadata = FilterUnknownMetadata(metadata);
        if (unknownMetadata is not { Count: > 0 })
            return serialized;

        IReadOnlyDictionary<string, object?>? customMetadata =
            _options.MetadataSerializer.Serialize(unknownMetadata);
        if (customMetadata is not { Count: > 0 })
            return serialized;

        foreach (KeyValuePair<string, object?> pair in customMetadata)
        {
            serialized ??= new Dictionary<string, NavigationMetadataValueSnapshot>(StringComparer.Ordinal);
            serialized[pair.Key] = SerializeValueSnapshot(pair.Key, pair.Value);
        }

        return serialized;
    }

    private string FormatCanonicalRouteUri(AppRoute route, IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return _routes.FormatUri(route, _options.BaseUri).ToString();

        var request = new AppRouteRequest(route, metadata);
        if (_options.RouteStateRegistry is { } routeStateRegistry) request = routeStateRegistry.Canonicalize(request);

        return _routes.FormatUri(request, _options.BaseUri).ToString();
    }

    private Dictionary<string, object?>? DeserializeMetadata(
        IReadOnlyDictionary<string, NavigationMetadataValueSnapshot>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return null;

        Dictionary<string, object?>? restored = null;
        Dictionary<string, object?>? customMetadata = null;
        foreach (KeyValuePair<string, NavigationMetadataValueSnapshot> pair in metadata)
        {
            if (_options.RouteStateRegistry is { } routeStateRegistry &&
                routeStateRegistry.TryGetRegistration(pair.Key, out RouteStateRegistration registration))
            {
                if (registration.Lifetime != RouteStateLifetime.Restorable) continue;

                restored ??= new Dictionary<string, object?>(StringComparer.Ordinal);
                restored[pair.Key] = DeserializeValueSnapshot(pair.Key, pair.Value, registration.ValueType);
                continue;
            }

            if (_options.MetadataSerializer is null)
                continue;

            customMetadata ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            customMetadata[pair.Key] = DeserializeValueSnapshot(pair.Key, pair.Value);
        }

        if (customMetadata is not { Count: > 0 } || _options.MetadataSerializer is null)
            return restored;

        IReadOnlyDictionary<string, object?>? deserializedCustomMetadata =
            _options.MetadataSerializer.Deserialize(customMetadata);
        if (deserializedCustomMetadata is not { Count: > 0 })
            return restored;

        restored ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in deserializedCustomMetadata)
            restored[pair.Key] = pair.Value;

        return restored;
    }

    private IReadOnlyDictionary<string, object?>? FilterUnknownMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        if (_options.RouteStateRegistry is null) return metadata;

        Dictionary<string, object?>? unknown = null;
        foreach (KeyValuePair<string, object?> pair in metadata)
        {
            if (_options.RouteStateRegistry.TryGetRegistration(pair.Key, out _))
                continue;

            unknown ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            unknown[pair.Key] = pair.Value;
        }

        return unknown;
    }

    private static NavigationMetadataValueSnapshot SerializeValueSnapshot(
        string key,
        object? value,
        Type? declaredType = null)
    {
        if (value is null)
            return new NavigationMetadataValueSnapshot(declaredType?.AssemblyQualifiedName, null, true);

        Type valueType = declaredType ?? value.GetType();
        return new NavigationMetadataValueSnapshot(
            valueType.AssemblyQualifiedName,
            SerializeValue(key, value, valueType));
    }

    private static object? DeserializeValueSnapshot(
        string key,
        NavigationMetadataValueSnapshot snapshot,
        Type? declaredType = null)
    {
        if (snapshot.IsNull || snapshot.Value is null) return null;

        Type? valueType = declaredType ?? ResolveValueType(key, snapshot.Type);
        return valueType is null || valueType == typeof(string)
            ? snapshot.Value
            : RouteValueConverter.Convert(snapshot.Value, valueType, key);
    }

    private static Type? ResolveValueType(string key, string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;

        var valueType = Type.GetType(typeName, false);
        if (valueType is not null) return valueType;

        throw new InvalidOperationException(
            $"Navigation metadata '{key}' declared persisted type '{typeName}' could not be resolved.");
    }

    private static string SerializeValue(string key, object value, Type declaredType)
    {
        Type conversionType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        try
        {
            if (conversionType == typeof(string)) return (string)value;

            if (conversionType.IsEnum) return value.ToString()!;

            TypeConverter converter = TypeDescriptor.GetConverter(conversionType);
            if (converter.CanConvertTo(typeof(string)))
                if (converter.ConvertTo(null, CultureInfo.InvariantCulture, value, typeof(string)) is string converted)
                    return converted;

            if (value is IFormattable formattable)
            {
                var formatted = formattable.ToString(null, CultureInfo.InvariantCulture);
                return formatted;
            }

            if (value.ToString() is { } fallback) return fallback;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
        {
            throw new FormatException(
                $"Navigation metadata '{key}' could not be serialized as {conversionType.Name}.",
                ex);
        }

        throw new NotSupportedException(
            $"Navigation metadata '{key}' cannot be serialized as {conversionType.FullName}.");
    }

    private static Dictionary<string, object?>? MergeMetadata(
        IReadOnlyDictionary<string, object?>? lowerPriority,
        IReadOnlyDictionary<string, object?>? higherPriority)
    {
        if ((lowerPriority is null || lowerPriority.Count == 0) &&
            (higherPriority is null || higherPriority.Count == 0))
            return null;

        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (lowerPriority is not null)
            foreach (KeyValuePair<string, object?> pair in lowerPriority)
                merged[pair.Key] = pair.Value;

        if (higherPriority is null)
            return merged;

        foreach (KeyValuePair<string, object?> pair in higherPriority)
            merged[pair.Key] = pair.Value;

        return merged;
    }
}

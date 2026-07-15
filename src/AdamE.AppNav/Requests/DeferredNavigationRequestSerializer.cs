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

        IReadOnlyDictionary<string, object?>? metadata = MergeMetadata(routeMetadata, persistedMetadata);
        NavigationRequestProvenance? provenance =
            NavigationRequestProvenanceSnapshotMapper.Restore(snapshot.Provenance);
        request = requestUri is null
            ? RouterNavigationRequest.FromRoute(
                route!,
                snapshot.Source,
                snapshot.WindowId,
                metadata,
                snapshot.Disposition,
                provenance)
            : RouterNavigationRequest.FromUri(
                requestUri,
                snapshot.Source,
                snapshot.WindowId,
                metadata,
                snapshot.Disposition,
                provenance);
        request = request with { Timestamp = snapshot.Timestamp };

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
                serialized[pair.Key] = SerializeRegisteredValueSnapshot(
                    pair.Key,
                    pair.Value,
                    registration.ValueType);
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
            serialized[pair.Key] = SerializeCustomValueSnapshot(pair.Key, pair.Value);
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
                restored[pair.Key] = DeserializeRegisteredValueSnapshot(
                    pair.Key,
                    pair.Value,
                    registration.ValueType);
                continue;
            }

            if (_options.MetadataSerializer is null)
                continue;

            customMetadata ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            customMetadata[pair.Key] = DeserializeCustomValueSnapshot(pair.Key, pair.Value);
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

    private NavigationMetadataValueSnapshot SerializeRegisteredValueSnapshot(
        string key,
        object? value,
        Type declaredType)
    {
        if (value is null)
            return new NavigationMetadataValueSnapshot(null, null, true);

        return new NavigationMetadataValueSnapshot(
            null,
            _routes.ValueCodecs.Format(value, declaredType, key));
    }

    private object? DeserializeRegisteredValueSnapshot(
        string key,
        NavigationMetadataValueSnapshot snapshot,
        Type declaredType)
    {
        if (snapshot.IsNull || snapshot.Value is null) return null;
        return _routes.ValueCodecs.Convert(snapshot.Value, declaredType, key);
    }

    private static NavigationMetadataValueSnapshot SerializeCustomValueSnapshot(string key, object? value)
    {
        return value is null
            ? new NavigationMetadataValueSnapshot(null, null, true)
            : PersistedScalarValue.Serialize(key, value);
    }

    private static object? DeserializeCustomValueSnapshot(
        string key,
        NavigationMetadataValueSnapshot snapshot)
    {
        if (snapshot.IsNull) return null;
        if (snapshot.Value is null)
            throw new InvalidOperationException($"Navigation metadata '{key}' has no persisted value.");
        if (string.IsNullOrWhiteSpace(snapshot.Type))
            throw new InvalidOperationException($"Navigation metadata '{key}' has no persisted scalar type.");

        return PersistedScalarValue.Deserialize(key, snapshot.Type, snapshot.Value);
    }

    private static class PersistedScalarValue
    {
        public static NavigationMetadataValueSnapshot Serialize(string key, object value)
        {
            return value switch
            {
                string typed => Snapshot("string", typed),
                bool typed => Snapshot("bool", typed.ToString()),
                byte typed => Snapshot("byte", typed.ToString(CultureInfo.InvariantCulture)),
                sbyte typed => Snapshot("sbyte", typed.ToString(CultureInfo.InvariantCulture)),
                short typed => Snapshot("int16", typed.ToString(CultureInfo.InvariantCulture)),
                ushort typed => Snapshot("uint16", typed.ToString(CultureInfo.InvariantCulture)),
                int typed => Snapshot("int32", typed.ToString(CultureInfo.InvariantCulture)),
                uint typed => Snapshot("uint32", typed.ToString(CultureInfo.InvariantCulture)),
                long typed => Snapshot("int64", typed.ToString(CultureInfo.InvariantCulture)),
                ulong typed => Snapshot("uint64", typed.ToString(CultureInfo.InvariantCulture)),
                decimal typed => Snapshot("decimal", typed.ToString(CultureInfo.InvariantCulture)),
                float typed => Snapshot("single", typed.ToString("R", CultureInfo.InvariantCulture)),
                double typed => Snapshot("double", typed.ToString("R", CultureInfo.InvariantCulture)),
                Guid typed => Snapshot("guid", typed.ToString()),
                _ => throw new NotSupportedException(
                    $"Custom navigation metadata '{key}' serialized to unsupported type " +
                    $"'{value.GetType().FullName}'. Return a supported scalar value instead.")
            };
        }

        public static object Deserialize(string key, string type, string value)
        {
            try
            {
                return type switch
                {
                    "string" => value,
                    "bool" => bool.Parse(value),
                    "byte" => byte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "sbyte" => sbyte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "int16" => short.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "uint16" => ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "int32" => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "uint32" => uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "int64" => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "uint64" => ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "decimal" => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture),
                    "single" => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
                    "double" => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
                    "guid" => Guid.Parse(value),
                    _ => throw new InvalidOperationException(
                        $"Navigation metadata '{key}' uses unsupported persisted scalar type '{type}'.")
                };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new FormatException(
                    $"Navigation metadata '{key}' could not be restored as persisted scalar type '{type}'.",
                    ex);
            }
        }

        private static NavigationMetadataValueSnapshot Snapshot(string type, string value)
        {
            return new NavigationMetadataValueSnapshot(type, value);
        }
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

using AdamE.MauiRouter.Internal;
using AdamE.MauiRouter.Routing;

namespace AdamE.MauiRouter;

/// <summary>
/// Represents an application route plus route-owned metadata without runtime transport concerns.
/// </summary>
public sealed record AppRouteRequest
{
    private IReadOnlyDictionary<string, object?> _metadata = CollectionSnapshot.Dictionary(null);

    /// <summary>
    /// Creates an application route request for the supplied route and optional metadata.
    /// </summary>
    public AppRouteRequest(
        AppRoute route,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        Route = route ?? throw new ArgumentNullException(nameof(route));
        Metadata = metadata ?? CollectionSnapshot.Dictionary(null);
    }

    /// <summary>
    /// Gets the semantic application route being requested.
    /// </summary>
    public AppRoute Route { get; init; }

    /// <summary>
    /// Gets the route-owned metadata associated with this request.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metadata
    {
        get => _metadata;
        init => _metadata = CollectionSnapshot.Dictionary(value);
    }

    /// <summary>
    /// Returns a new route request with the supplied metadata value added or replaced.
    /// </summary>
    /// <remarks>
    /// Passing <see langword="null" /> removes the metadata entry.
    /// </remarks>
    public AppRouteRequest WithMetadata<TValue>(RouteMetadataKey<TValue> key, TValue? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        var metadata = new Dictionary<string, object?>(Metadata, StringComparer.Ordinal);
        if (value is null)
        {
            metadata.Remove(key.Name);
        }
        else
        {
            metadata[key.Name] = value;
        }

        return new AppRouteRequest(Route, metadata);
    }

    /// <summary>
    /// Attempts to read a typed metadata value from the request.
    /// </summary>
    public bool TryGetMetadata<TValue>(RouteMetadataKey<TValue> key, out TValue? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!Metadata.TryGetValue(key.Name, out var rawValue))
        {
            value = default;
            return false;
        }

        if (rawValue is null)
        {
            value = default;
            return true;
        }

        if (rawValue is TValue typedValue)
        {
            value = typedValue;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Creates a route request for the supplied semantic route.
    /// </summary>
    public static AppRouteRequest For(AppRoute route)
    {
        return new AppRouteRequest(route);
    }
}

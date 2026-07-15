using AdamE.AppNav.Internal;

namespace AdamE.AppNav.Requests;

/// <summary>
/// Describes one navigation intent with exactly one URI or application-route target.
/// </summary>
public sealed record RouterNavigationRequest
{
    private RouterNavigationRequest(
        Uri? uri,
        AppRoute? route,
        NavigationRequestSource source,
        string? windowId = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        DateTimeOffset? timestamp = null,
        RouterNavigationDisposition disposition = RouterNavigationDisposition.Auto,
        NavigationRequestProvenance? provenance = null)
    {
        if ((uri is null) == (route is null))
            throw new ArgumentException(
                "A navigation request must contain exactly one URI or application-route target.");

        Uri = uri;
        Route = route;
        Source = source;
        WindowId = windowId;
        Metadata = metadata ?? CollectionSnapshot.MetadataDictionary(null);
        Timestamp = timestamp ?? DateTimeOffset.UtcNow;
        Disposition = disposition;
        Provenance = provenance;
    }

    /// <summary>
    /// Gets the URI target, or <see langword="null"/> when <see cref="Route"/> is the target.
    /// </summary>
    public Uri? Uri { get; private init; }

    /// <summary>
    /// Gets the application-route target, or <see langword="null"/> when <see cref="Uri"/> is the target.
    /// </summary>
    public AppRoute? Route { get; private init; }

    public NavigationRequestSource Source { get; }

    public string? WindowId { get; init; }

    public IReadOnlyDictionary<string, object?> Metadata
    {
        get;
        init => field = CollectionSnapshot.MetadataDictionary(value);
    } = CollectionSnapshot.MetadataDictionary(null);

    public DateTimeOffset Timestamp { get; init; }

    public RouterNavigationDisposition Disposition { get; init; }

    /// <summary>
    /// Runtime context that describes how the request entered the router.
    /// </summary>
    /// <remarks>
    /// Built-in MAUI app-link ingress sets this automatically. App-owned external sources should attach
    /// provider-specific provenance before dispatching requests through the router.
    /// </remarks>
    public NavigationRequestProvenance? Provenance { get; init; }

    public static RouterNavigationRequest FromUri(
        Uri uri,
        NavigationRequestSource source,
        string? windowId = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        RouterNavigationDisposition disposition = RouterNavigationDisposition.Auto,
        NavigationRequestProvenance? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return new RouterNavigationRequest(
            uri,
            null,
            source,
            windowId,
            metadata,
            disposition: disposition,
            provenance: provenance);
    }

    public static RouterNavigationRequest FromRoute(
        AppRoute route,
        NavigationRequestSource source,
        string? windowId = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        RouterNavigationDisposition disposition = RouterNavigationDisposition.Auto,
        NavigationRequestProvenance? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new RouterNavigationRequest(
            null,
            route,
            source,
            windowId,
            metadata,
            disposition: disposition,
            provenance: provenance);
    }

    /// <summary>
    /// Converts an app-facing route request into the runtime request shape used by policies, planning, and persistence.
    /// </summary>
    /// <remarks>
    /// When both request metadata and <paramref name="extraMetadata" /> are supplied, <paramref name="extraMetadata" />
    /// wins on key collisions.
    /// </remarks>
    public static RouterNavigationRequest FromRouteRequest(
        AppRouteRequest routeRequest,
        NavigationRequestSource source,
        string? windowId = null,
        IReadOnlyDictionary<string, object?>? extraMetadata = null,
        RouterNavigationDisposition disposition = RouterNavigationDisposition.Auto,
        NavigationRequestProvenance? provenance = null)
    {
        ArgumentNullException.ThrowIfNull(routeRequest);

        return new RouterNavigationRequest(
            null,
            routeRequest.Route,
            source,
            windowId,
            MergeMetadata(routeRequest.Metadata, extraMetadata),
            disposition: disposition,
            provenance: provenance);
    }

    /// <summary>
    /// Returns a copy of this request that targets the specified URI.
    /// </summary>
    /// <remarks>
    /// All request-envelope values are preserved and any route target is removed.
    /// </remarks>
    public RouterNavigationRequest WithTarget(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return this with { Uri = uri, Route = null };
    }

    /// <summary>
    /// Returns a copy of this request that targets the specified application route.
    /// </summary>
    /// <remarks>
    /// All request-envelope values are preserved and any URI target is removed.
    /// </remarks>
    public RouterNavigationRequest WithTarget(AppRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return this with { Uri = null, Route = route };
    }

    private static IReadOnlyDictionary<string, object?> MergeMetadata(
        IReadOnlyDictionary<string, object?> routeRequestMetadata,
        IReadOnlyDictionary<string, object?>? extraMetadata)
    {
        if (extraMetadata is null || extraMetadata.Count == 0)
            return routeRequestMetadata;

        var merged = new Dictionary<string, object?>(routeRequestMetadata, StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in extraMetadata)
            merged[pair.Key] = pair.Value;

        return merged;
    }
}

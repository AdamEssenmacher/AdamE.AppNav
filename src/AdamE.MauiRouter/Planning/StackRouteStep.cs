using AdamE.MauiRouter.Internal;

namespace AdamE.MauiRouter.Planning;

/// <summary>
/// Represents a route entry that participates in canonical stack creation or contextual tail injection.
/// </summary>
public sealed record StackRouteStep<TRoute>
    where TRoute : AppRoute
{
    private IReadOnlyDictionary<string, object?>? _metadata;

    /// <summary>
    /// Creates a stack route step for the supplied route and optional route-entry metadata.
    /// </summary>
    public StackRouteStep(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        Route = route ?? throw new ArgumentNullException(nameof(route));
        Metadata = metadata;
    }

    /// <summary>
    /// Gets the route represented by this stack step.
    /// </summary>
    public TRoute Route { get; init; }

    /// <summary>
    /// Gets the route-entry metadata for this step.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata
    {
        get => _metadata;
        init => _metadata = CollectionSnapshot.NullableMetadataDictionary(value);
    }
}

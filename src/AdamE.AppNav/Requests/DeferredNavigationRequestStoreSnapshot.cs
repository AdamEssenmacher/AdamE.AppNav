namespace AdamE.AppNav.Requests;

public sealed record DeferredNavigationRequestStoreSnapshot
{
    /// <summary>
    /// The only persisted schema version accepted by this release.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<NavigationRequestSnapshot> Requests { get; init; } = [];
}

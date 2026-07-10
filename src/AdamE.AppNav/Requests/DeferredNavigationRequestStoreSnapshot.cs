namespace AdamE.AppNav.Requests;

public sealed record DeferredNavigationRequestStoreSnapshot
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<NavigationRequestSnapshot> Requests { get; init; } = [];
}

namespace AdamE.MauiRouter.Requests;

public sealed record DeferredNavigationRequestStoreSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<NavigationRequestSnapshot> Requests { get; init; } = Array.Empty<NavigationRequestSnapshot>();
}

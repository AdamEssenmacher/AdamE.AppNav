using System.Text.Json.Serialization;

namespace AdamE.AppNav.Requests;

public sealed record DeferredNavigationRequestStoreSnapshot
{
    /// <summary>
    /// The only persisted schema version accepted by this release.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    [JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonRequired]
    public IReadOnlyList<NavigationRequestSnapshot> Requests { get; init; } = [];
}

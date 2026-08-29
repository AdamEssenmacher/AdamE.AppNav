using System.Text.Json.Serialization;

namespace AdamE.AppNav.Requests;

public sealed record DeferredNavigationRequestStoreSnapshot
{
    /// <summary>
    /// The only persisted schema version accepted by this release.
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    [JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public IReadOnlyList<NavigationRequestSnapshot> Requests { get; init; } = [];
}

namespace AdamE.MauiRouter.Persistence;

public sealed class NavigationPersistenceOptions
{
    public INavigationStateStore? Store { get; set; }

    public Uri BaseUri { get; set; } = new("https://example.com/");

    public bool PersistHistory { get; set; } = true;

    public bool PersistModals { get; set; }

    public TimeSpan? MaxSnapshotAge { get; set; }

    public INavigationSnapshotMetadataSerializer? MetadataSerializer { get; set; }

    public RouteStateRegistry? RouteStateRegistry { get; set; }

    public IReadOnlyList<INavigationRestorePolicy> RestorePolicies { get; set; } = Array.Empty<INavigationRestorePolicy>();
}

public interface INavigationSnapshotMetadataSerializer
{
    IReadOnlyDictionary<string, object?>? Serialize(IReadOnlyDictionary<string, object?> metadata);

    IReadOnlyDictionary<string, object?>? Deserialize(IReadOnlyDictionary<string, object?> metadata);
}

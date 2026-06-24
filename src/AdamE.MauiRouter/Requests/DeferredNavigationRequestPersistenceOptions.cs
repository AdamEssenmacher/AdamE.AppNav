using AdamE.MauiRouter.Persistence;

namespace AdamE.MauiRouter.Requests;

public sealed class DeferredNavigationRequestPersistenceOptions
{
    public Uri BaseUri { get; set; } = new("https://example.com/");

    public INavigationSnapshotMetadataSerializer? MetadataSerializer { get; set; }

    public RouteStateRegistry? RouteStateRegistry { get; set; }
}

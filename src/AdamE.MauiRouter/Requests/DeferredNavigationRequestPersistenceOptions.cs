namespace AdamE.MauiRouter.Requests;

public sealed class DeferredNavigationRequestPersistenceOptions
{
    public Uri BaseUri { get; init; } = new("https://example.com/");

    public INavigationRequestMetadataSerializer? MetadataSerializer { get; init; }

    public RouteStateRegistry? RouteStateRegistry { get; init; }
}

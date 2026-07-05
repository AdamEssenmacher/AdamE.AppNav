namespace AdamE.MauiRouter.Requests;

public sealed class DeferredNavigationRequestPersistenceOptions
{
    public Uri BaseUri { get; set; } = new("https://example.com/");

    public INavigationRequestMetadataSerializer? MetadataSerializer { get; set; }

    public RouteStateRegistry? RouteStateRegistry { get; set; }
}

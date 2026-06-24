using AdamE.MauiRouter.Persistence;
using Microsoft.Maui.Storage;

namespace AdamE.MauiRouter.Maui.Requests;

public sealed class MauiFileDeferredNavigationRequestStoreOptions
{
    public string Path { get; set; } = System.IO.Path.Combine(
        FileSystem.AppDataDirectory,
        MauiFileDeferredNavigationRequestStore.DefaultFileName);

    public Uri BaseUri { get; set; } = new("https://example.com/");

    public INavigationSnapshotMetadataSerializer? MetadataSerializer { get; set; }

    public RouteStateRegistry? RouteStateRegistry { get; set; }
}

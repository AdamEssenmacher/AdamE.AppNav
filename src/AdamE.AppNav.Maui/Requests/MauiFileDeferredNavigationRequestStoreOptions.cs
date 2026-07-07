using AdamE.AppNav.Requests;
using Microsoft.Maui.Storage;

namespace AdamE.AppNav.Maui.Requests;

public sealed class MauiFileDeferredNavigationRequestStoreOptions
{
    public string Path { get; set; } = System.IO.Path.Combine(
        FileSystem.AppDataDirectory,
        MauiFileDeferredNavigationRequestStore.DefaultFileName);

    public Uri BaseUri { get; set; } = new("https://example.com/");

    public INavigationRequestMetadataSerializer? MetadataSerializer { get; set; }

    public RouteStateRegistry? RouteStateRegistry { get; set; }
}

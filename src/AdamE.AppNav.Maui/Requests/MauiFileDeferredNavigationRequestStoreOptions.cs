using AdamE.AppNav.Requests;
using Microsoft.Maui.Storage;

namespace AdamE.AppNav.Maui.Requests;

public sealed class MauiFileDeferredNavigationRequestStoreOptions
{
    private Uri? _baseUri;

    public string Path { get; set; } = System.IO.Path.Combine(
        FileSystem.AppDataDirectory,
        MauiFileDeferredNavigationRequestStore.DefaultFileName);

    /// <summary>
    /// Gets or sets the explicit absolute base URI used to format canonical persisted route URIs.
    /// </summary>
    /// <exception cref="InvalidOperationException">No base URI has been configured.</exception>
    public Uri BaseUri
    {
        get => _baseUri ?? throw new InvalidOperationException(
            $"{nameof(BaseUri)} must be configured explicitly for deferred navigation persistence.");
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _ = new DeferredNavigationRequestPersistenceOptions { BaseUri = value };

            _baseUri = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of deferred requests retained by the store.
    /// </summary>
    public int MaximumPendingRequests { get; set; } = 32;

    /// <summary>
    /// Gets or sets the maximum serialized store size in bytes.
    /// </summary>
    public long MaximumFileSize { get; set; } = 64 * 1024;

    /// <summary>
    /// Gets or sets the maximum age of a deferred request before it is pruned.
    /// </summary>
    public TimeSpan MaximumRequestAge { get; set; } = TimeSpan.FromDays(7);

    public INavigationRequestMetadataSerializer? MetadataSerializer { get; set; }

    public RouteStateRegistry? RouteStateRegistry { get; set; }
}

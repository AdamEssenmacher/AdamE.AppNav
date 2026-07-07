using System.Text.Json;
using AdamE.AppNav.Maui.Requests;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiFileDeferredNavigationRequestStoreTests
{
    private static readonly Uri BaseUri = new("https://example.com/");

#if IOS
    private const string IosSkipReason =
        "The iOS simulator XHarness host validates presenter behavior; deferred file-store IO coverage runs on Mac Catalyst and Android.";
#endif

#if IOS
    [Fact(Skip = IosSkipReason)]
#else
    [Fact]
#endif
    public async Task PersistedReload_RestoresCanonicalAndRestorableStateButDropsEphemeral()
    {
        var missionId = new RouteMetadataKey<string>("missionId");
        var draftId = new RouteMetadataKey<string>("coverImageDraftId");
        var replyId = new RouteMetadataKey<string>("replyCommentId");
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>(
            "/stores/{id}",
            route => route.QueryMetadata(missionId)));
        var registry = RouteStateRegistry.Create(builder => builder
            .Canonical(missionId)
            .Restorable(draftId)
            .Ephemeral(replyId));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            var initial = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri,
                    MetadataSerializer = new PassThroughMetadataSerializer(),
                    RouteStateRegistry = registry
                });
            await initial.EnqueueAsync(RouterNavigationRequest.FromRoute(
                new TestRoute("northwind"),
                NavigationRequestSource.AppLink,
                metadata: new Dictionary<string, object?>
                {
                    [missionId.Name] = "mission-1",
                    [draftId.Name] = "draft-1",
                    [replyId.Name] = "reply-1",
                    ["request-id"] = "abc-123"
                }));

            var reloaded = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri,
                    MetadataSerializer = new PassThroughMetadataSerializer(),
                    RouteStateRegistry = registry
                });
            var restored = await reloaded.TryDequeueAsync();

            Assert.NotNull(restored);
            Assert.Equal("mission-1", restored.Metadata[missionId.Name]);
            Assert.Equal("draft-1", restored.Metadata[draftId.Name]);
            Assert.False(restored.Metadata.ContainsKey(replyId.Name));
            Assert.Equal("abc-123", restored.Metadata["request-id"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

#if IOS
    [Fact(Skip = IosSkipReason)]
#else
    [Fact]
#endif
    public async Task HasDeferredRequestsAsync_MalformedJson_ThrowsJsonException()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            await File.WriteAllTextAsync(path, "{not-json");
            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri
                });

            await Assert.ThrowsAsync<JsonException>(() => store.HasDeferredRequestsAsync().AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

#if IOS
    [Fact(Skip = IosSkipReason)]
#else
    [Fact]
#endif
    public async Task ClearAsync_MalformedJson_RemovesCorruptFileAndAllowsReuseOnSameInstance()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            await File.WriteAllTextAsync(path, "{not-json");
            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri
                });

            await store.ClearAsync();

            Assert.False(File.Exists(path));
            Assert.False(await store.HasDeferredRequestsAsync());

            var request = RouterNavigationRequest.FromRoute(new TestRoute("northwind"), NavigationRequestSource.AppLink);
            await store.EnqueueAsync(request);

            Assert.True(File.Exists(path));
            var restored = await store.TryDequeueAsync();
            Assert.NotNull(restored);
            Assert.Equal(request.Route, restored.Route);
            Assert.Equal(request.Source, restored.Source);
            Assert.Equal(request.WindowId, restored.WindowId);
            Assert.Equal(request.Disposition, restored.Disposition);
            Assert.Equal(request.Uri, restored.Uri);
            Assert.Empty(restored.Metadata);
            Assert.False(await store.HasDeferredRequestsAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

#if IOS
    [Fact(Skip = IosSkipReason)]
#else
    [Fact]
#endif
    public async Task HasDeferredRequestsAsync_RetriesLoadAfterMalformedJsonIsCorrectedOnSameInstance()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            await File.WriteAllTextAsync(path, "{not-json");
            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri
                });

            await Assert.ThrowsAsync<JsonException>(() => store.HasDeferredRequestsAsync().AsTask());

            var serializer = new DeferredNavigationRequestSerializer(
                routes,
                new DeferredNavigationRequestPersistenceOptions
                {
                    BaseUri = BaseUri
                });
            var request = RouterNavigationRequest.FromRoute(new TestRoute("northwind"), NavigationRequestSource.AppLink);
            var snapshot = serializer.CreateSnapshot([request]);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot));

            Assert.True(await store.HasDeferredRequestsAsync());
            var restored = await store.TryDequeueAsync();
            Assert.NotNull(restored);
            Assert.Equal(request.Route, restored.Route);
            Assert.Equal(request.Source, restored.Source);
            Assert.Equal(request.WindowId, restored.WindowId);
            Assert.Equal(request.Disposition, restored.Disposition);
            Assert.Equal(request.Uri, restored.Uri);
            Assert.Empty(restored.Metadata);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateStoreDirectory()
    {
#if IOS || MACCATALYST || ANDROID
        var root = Microsoft.Maui.Storage.FileSystem.CacheDirectory;
#else
        var root = Path.GetTempPath();
#endif
        return Path.Combine(root, $"appnav-deferred-{Guid.NewGuid():N}");
    }

    private sealed record TestRoute(string Id) : AppRoute;

    private sealed class PassThroughMetadataSerializer : INavigationRequestMetadataSerializer
    {
        public IReadOnlyDictionary<string, object?>? Serialize(IReadOnlyDictionary<string, object?> metadata) => metadata;

        public IReadOnlyDictionary<string, object?>? Deserialize(IReadOnlyDictionary<string, object?> metadata) => metadata;
    }
}

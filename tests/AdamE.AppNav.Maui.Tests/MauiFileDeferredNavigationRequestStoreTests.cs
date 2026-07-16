using System.Text.Json;
using AdamE.AppNav.Diagnostics;
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
            await using var lease = await reloaded.AcquireReplayLeaseAsync();
            var restored = Assert.Single(lease.Requests);

            Assert.Equal("mission-1", restored.Metadata[missionId.Name]);
            Assert.Equal("draft-1", restored.Metadata[draftId.Name]);
            Assert.False(restored.Metadata.ContainsKey(replyId.Name));
            Assert.Equal("abc-123", restored.Metadata["request-id"]);
            await lease.AcknowledgeAsync(0);
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
    public async Task HasDeferredRequestsAsync_MalformedJson_QuarantinesOriginalBytesAndContinuesEmpty()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            byte[] originalBytes = "{not-json"u8.ToArray();
            await File.WriteAllBytesAsync(path, originalBytes);
            var diagnostics = new NavigationDiagnostics();
            var events = new List<NavigationDiagnosticEvent>();
            diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri
                },
                diagnostics);

            Assert.False(await store.HasDeferredRequestsAsync());
            Assert.False(File.Exists(path));
            string quarantinePath = Assert.Single(Directory.GetFiles(
                directory,
                "deferred-requests.json.invalid-*"));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(quarantinePath));
            NavigationDiagnosticEvent quarantined = Assert.Single(events, diagnosticEvent =>
                diagnosticEvent.Kind == NavigationDiagnosticEventKind.DeferredRequestStoreQuarantined);
            Assert.Equal(NavigationDiagnosticPhase.Persistence, quarantined.Phase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

#if IOS
    [Theory(Skip = IosSkipReason)]
#else
    [Theory]
#endif
    [InlineData(1)]
    [InlineData(3)]
    public async Task UnsupportedSchemaVersionIsRejectedWithoutQuarantiningOtherSchemaShapes(int schemaVersion)
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            byte[] originalBytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion });
            await File.WriteAllBytesAsync(path, originalBytes);
            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri
                });

            UnsupportedDeferredNavigationRequestSchemaException exception =
                await Assert.ThrowsAsync<UnsupportedDeferredNavigationRequestSchemaException>(
                    () => store.HasDeferredRequestsAsync().AsTask());

            Assert.Equal(schemaVersion, exception.ActualVersion);
            Assert.Equal(DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion, exception.SupportedVersion);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(directory, "deferred-requests.json.invalid-*"));
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
    public async Task SchemaTwoWithMissingRequiredFieldsIsQuarantined()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            byte[] originalBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion,
                createdAt = DateTimeOffset.UtcNow
            });
            await File.WriteAllBytesAsync(path, originalBytes);
            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri
                });

            Assert.False(await store.HasDeferredRequestsAsync());
            Assert.False(File.Exists(path));
            string quarantinePath = Assert.Single(Directory.GetFiles(
                directory,
                "deferred-requests.json.invalid-*"));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(quarantinePath));
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
    public async Task InvalidSchemaTwoRecordQuarantinesWholeSnapshotWithoutPartialReplay()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            var serializer = new DeferredNavigationRequestSerializer(
                routes,
                new DeferredNavigationRequestPersistenceOptions { BaseUri = BaseUri });
            DeferredNavigationRequestStoreSnapshot snapshot = serializer.CreateSnapshot(
                [
                    RouterNavigationRequest.FromRoute(new TestRoute("first"), NavigationRequestSource.AppLink),
                    RouterNavigationRequest.FromRoute(new TestRoute("second"), NavigationRequestSource.AppLink)
                ]);
            snapshot = snapshot with
            {
                Requests =
                [
                    snapshot.Requests[0],
                    snapshot.Requests[1] with { RouteUri = "http://[::1" }
                ]
            };
            byte[] originalBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            await File.WriteAllBytesAsync(path, originalBytes);
            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri
                });

            Assert.False(await store.HasDeferredRequestsAsync());
            await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();
            Assert.Empty(lease.Requests);
            string quarantinePath = Assert.Single(Directory.GetFiles(
                directory,
                "deferred-requests.json.invalid-*"));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(quarantinePath));
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
    public async Task QuarantineFailurePreservesOriginalAndFailsLoad()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");
        byte[] originalBytes = "{not-json"u8.ToArray();

        try
        {
            await File.WriteAllBytesAsync(path, originalBytes);
            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri
                },
                fileOperations: new ThrowingMoveOperations());

            await Assert.ThrowsAsync<IOException>(() => store.HasDeferredRequestsAsync().AsTask());

            Assert.True(File.Exists(path));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(directory, "deferred-requests.json.invalid-*"));
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
            await using var lease = await store.AcquireReplayLeaseAsync();
            var restored = Assert.Single(lease.Requests);
            Assert.Equal(request.Route, restored.Route);
            Assert.Equal(request.Source, restored.Source);
            Assert.Equal(request.WindowId, restored.WindowId);
            Assert.Equal(request.Disposition, restored.Disposition);
            Assert.Equal(request.Uri, restored.Uri);
            Assert.Empty(restored.Metadata);
            await lease.AcknowledgeAsync(0);
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
    public async Task QuarantinedStoreCanPersistAndReplayNewRequestsOnSameInstance()
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

            Assert.False(await store.HasDeferredRequestsAsync());
            var request = RouterNavigationRequest.FromRoute(new TestRoute("northwind"), NavigationRequestSource.AppLink);
            await store.EnqueueAsync(request);

            Assert.True(await store.HasDeferredRequestsAsync());
            await using var lease = await store.AcquireReplayLeaseAsync();
            var restored = Assert.Single(lease.Requests);
            Assert.Equal(request.Route, restored.Route);
            Assert.Equal(request.Source, restored.Source);
            Assert.Equal(request.WindowId, restored.WindowId);
            Assert.Equal(request.Disposition, restored.Disposition);
            Assert.Equal(request.Uri, restored.Uri);
            Assert.Empty(restored.Metadata);
            await lease.AcknowledgeAsync(0);
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
    public async Task ReplayLeaseIsCrashDurableAndExcludesConcurrentEnqueuesFromItsSnapshot()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");
        var options = new MauiFileDeferredNavigationRequestStoreOptions
        {
            Path = path,
            BaseUri = BaseUri
        };
        var first = RouterNavigationRequest.FromRoute(new TestRoute("first"), NavigationRequestSource.AppLink);
        var second = RouterNavigationRequest.FromRoute(new TestRoute("second"), NavigationRequestSource.AppLink);
        var third = RouterNavigationRequest.FromRoute(new TestRoute("third"), NavigationRequestSource.AppLink);

        try
        {
            var store = new MauiFileDeferredNavigationRequestStore(routes, options);
            await store.EnqueueAsync(first);
            await store.EnqueueAsync(second);
            await using (IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync())
            {
                await store.EnqueueAsync(third);

                Assert.Equal(
                    ["first", "second"],
                    lease.Requests.Select(static request => Assert.IsType<TestRoute>(request.Route).Id).ToArray());
            }

            var reopenedBeforeAcknowledgement = new MauiFileDeferredNavigationRequestStore(routes, options);
            await using (IDeferredNavigationRequestLease reopenedLease =
                         await reopenedBeforeAcknowledgement.AcquireReplayLeaseAsync())
            {
                Assert.Equal(
                    ["first", "second", "third"],
                    reopenedLease.Requests
                        .Select(static request => Assert.IsType<TestRoute>(request.Route).Id)
                        .ToArray());
                await reopenedLease.AcknowledgeAsync(1);
            }

            var reopenedAfterAcknowledgement = new MauiFileDeferredNavigationRequestStore(routes, options);
            await using IDeferredNavigationRequestLease durableLease =
                await reopenedAfterAcknowledgement.AcquireReplayLeaseAsync();
            Assert.Equal(
                ["first", "third"],
                durableLease.Requests
                    .Select(static request => Assert.IsType<TestRoute>(request.Route).Id)
                    .ToArray());
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

    private sealed class ThrowingMoveOperations : IMauiDeferredNavigationFileOperations
    {
        public void Move(string sourcePath, string destinationPath) =>
            throw new IOException("Injected quarantine failure.");
    }
}

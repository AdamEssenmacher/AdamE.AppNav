using System.Text.Json;
using System.Text.Json.Nodes;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Maui.Requests;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiFileDeferredNavigationRequestStoreTests
{
    private static readonly Uri BaseUri = new("https://example.com/");

    [Fact]
    public void Options_UseBoundedDefaultsAndRequireExplicitBaseUri()
    {
        var options = new MauiFileDeferredNavigationRequestStoreOptions();

        Assert.Equal(32, options.MaximumPendingRequests);
        Assert.Equal(64L * 1024, options.MaximumFileSize);
        Assert.Equal(TimeSpan.FromDays(7), options.MaximumRequestAge);
        Assert.Throws<InvalidOperationException>(() => options.BaseUri);
        Assert.Throws<ArgumentException>(() => options.BaseUri = new Uri("https://user:secret@example.com/"));
        Assert.Throws<ArgumentException>(() => options.BaseUri = new Uri("https://@example.com/"));
        Assert.Throws<ArgumentException>(() => options.BaseUri = new Uri("https://example.com/base/"));
        Assert.Throws<ArgumentException>(() => options.BaseUri = new Uri("https://example.com/#fragment"));
        Assert.Throws<ArgumentException>(() => options.BaseUri = new Uri("https://example.com/?"));
        Assert.Throws<ArgumentException>(() => options.BaseUri = new Uri("https://example.com/#"));
    }

    [Fact]
    public async Task Load_RewritesValidSchemaThreeToRemoveUnknownSecretFields()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var serializer = new DeferredNavigationRequestSerializer(
            routes,
            new DeferredNavigationRequestPersistenceOptions { BaseUri = BaseUri });
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            DeferredNavigationRequestStoreSnapshot snapshot = serializer.CreateSnapshot(
                [RouterNavigationRequest.FromRoute(
                    new TestRoute("safe"),
                    NavigationRequestSource.AppLink)]);
            JsonObject document = JsonNode.Parse(JsonSerializer.Serialize(
                snapshot,
                AppNavJsonSerializerContext.Default.DeferredNavigationRequestStoreSnapshot))!.AsObject();
            document["rawRequestUri"] = "https://example.com/?token=root-secret";
            document["requests"]!.AsArray()[0]!.AsObject()["correlationId"] = "request-secret";
            await File.WriteAllTextAsync(path, document.ToJsonString());

            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri
                });

            Assert.True(await store.HasDeferredRequestsAsync());

            string rewritten = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("root-secret", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("request-secret", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("rawRequestUri", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("correlationId", rewritten, StringComparison.Ordinal);
            Assert.Contains("https://example.com/stores/safe", rewritten, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
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

    [Fact]
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

    [Theory]
    [InlineData(1)]
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

    [Fact]
    public async Task SchemaThreeWithMissingRequiredFieldsIsQuarantined()
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

    [Fact]
    public async Task InvalidSchemaThreeRecordQuarantinesWholeSnapshotWithoutPartialReplay()
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
            byte[] originalBytes = JsonSerializer.SerializeToUtf8Bytes(
                snapshot,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task SchemaTwo_IsDeletedOnceAndRecordedAsPreviewReset()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":2,\"legacySecret\":\"do-not-replay\"}");
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
            Assert.False(await store.HasDeferredRequestsAsync());

            Assert.False(File.Exists(path));
            NavigationDiagnosticEvent reset = Assert.Single(events, diagnosticEvent =>
                diagnosticEvent.Kind == NavigationDiagnosticEventKind.DeferredRequestStoreReset);
            Assert.Equal(2, reset.Data[NavigationDiagnosticDataKeys.SchemaVersion]);
            Assert.Equal("schema-2-preview-reset", reset.Data[NavigationDiagnosticDataKeys.Reason]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FutureSchema_IsQuarantinedWithoutChangingItsBytes()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");
        byte[] futureBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion + 1,
            futureSecret = "preserve-exactly"
        });

        try
        {
            await File.WriteAllBytesAsync(path, futureBytes);
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
                "deferred-requests.json.future-*"));
            Assert.Equal(futureBytes, await File.ReadAllBytesAsync(quarantinePath));
            NavigationDiagnosticEvent quarantined = Assert.Single(events, diagnosticEvent =>
                diagnosticEvent.Kind == NavigationDiagnosticEventKind.DeferredRequestStoreQuarantined);
            Assert.Equal("future-schema", quarantined.Data[NavigationDiagnosticDataKeys.Reason]);
            Assert.DoesNotContain("preserve-exactly", quarantined.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                quarantined.Data.Values,
                static value => string.Equals(value?.ToString(), "preserve-exactly", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Enqueue_DropsOldestWhenRequestCountExceedsBound()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            var diagnostics = new NavigationDiagnostics();
            var events = new List<NavigationDiagnosticEvent>();
            diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri,
                    MaximumPendingRequests = 3
                },
                diagnostics);

            for (var index = 1; index <= 5; index++)
            {
                await store.EnqueueAsync(RouterNavigationRequest.FromRoute(
                    new TestRoute(index.ToString()),
                    NavigationRequestSource.AppLink));
            }

            await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();
            Assert.Equal(
                ["3", "4", "5"],
                lease.Requests.Select(static request => Assert.IsType<TestRoute>(request.Route).Id).ToArray());
            Assert.Equal(2, events.Count(diagnosticEvent =>
                diagnosticEvent.Kind == NavigationDiagnosticEventKind.DeferredRequestStoreOverflowed));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_PrunesExpiredRequestsAndRewritesRemainingSnapshot()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var serializer = new DeferredNavigationRequestSerializer(
            routes,
            new DeferredNavigationRequestPersistenceOptions { BaseUri = BaseUri });
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            DeferredNavigationRequestStoreSnapshot snapshot = serializer.CreateSnapshot(
            [
                RouterNavigationRequest.FromRoute(
                    new TestRoute("expired"),
                    NavigationRequestSource.AppLink) with
                {
                    Timestamp = DateTimeOffset.UtcNow - TimeSpan.FromDays(8)
                },
                RouterNavigationRequest.FromRoute(
                    new TestRoute("fresh"),
                    NavigationRequestSource.AppLink)
            ]);
            await File.WriteAllBytesAsync(
                path,
                JsonSerializer.SerializeToUtf8Bytes(
                    snapshot,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
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

            await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();

            RouterNavigationRequest fresh = Assert.Single(lease.Requests);
            Assert.Equal("fresh", Assert.IsType<TestRoute>(fresh.Route).Id);
            NavigationDiagnosticEvent pruned = Assert.Single(events, diagnosticEvent =>
                diagnosticEvent.Kind == NavigationDiagnosticEventKind.DeferredRequestStorePruned);
            Assert.Equal(1, pruned.Data[NavigationDiagnosticDataKeys.Count]);
            string persistedJson = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("expired", persistedJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Persistence_IsBoundedByFileSizeAndLeavesNoPartialTarget()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            var diagnostics = new NavigationDiagnostics();
            var events = new List<NavigationDiagnosticEvent>();
            diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri,
                    MaximumFileSize = 128
                },
                diagnostics);

            await store.EnqueueAsync(RouterNavigationRequest.FromRoute(
                new TestRoute(new string('x', 512)),
                NavigationRequestSource.AppLink));

            Assert.False(await store.HasDeferredRequestsAsync());
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.Contains(events, diagnosticEvent =>
                diagnosticEvent.Kind == NavigationDiagnosticEventKind.DeferredRequestStoreOverflowed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedNewestRequest_DoesNotEraseOlderPersistableRequests()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            var store = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri,
                    MaximumFileSize = 1024
                });
            await store.EnqueueAsync(RouterNavigationRequest.FromRoute(
                new TestRoute("survivor"),
                NavigationRequestSource.AppLink));

            await store.EnqueueAsync(RouterNavigationRequest.FromRoute(
                new TestRoute(new string('x', 2048)),
                NavigationRequestSource.AppLink));

            await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();
            Assert.Equal("survivor", Assert.IsType<TestRoute>(Assert.Single(lease.Requests).Route).Id);
            Assert.InRange(new FileInfo(path).Length, 1, 1024);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledEnqueue_PreservesPreviousAtomicSnapshot()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            var options = new MauiFileDeferredNavigationRequestStoreOptions
            {
                Path = path,
                BaseUri = BaseUri
            };
            var store = new MauiFileDeferredNavigationRequestStore(routes, options);
            await store.EnqueueAsync(RouterNavigationRequest.FromRoute(
                new TestRoute("first"),
                NavigationRequestSource.AppLink));
            byte[] originalBytes = await File.ReadAllBytesAsync(path);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EnqueueAsync(
                RouterNavigationRequest.FromRoute(new TestRoute("second"), NavigationRequestSource.AppLink),
                cancellation.Token).AsTask());

            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            var reloaded = new MauiFileDeferredNavigationRequestStore(routes, options);
            await using IDeferredNavigationRequestLease lease = await reloaded.AcquireReplayLeaseAsync();
            Assert.Equal("first", Assert.IsType<TestRoute>(Assert.Single(lease.Requests).Route).Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_IgnoresOrphanedPartialWriteAndPreservesCommittedSnapshot()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");

        try
        {
            var options = new MauiFileDeferredNavigationRequestStoreOptions
            {
                Path = path,
                BaseUri = BaseUri
            };
            var store = new MauiFileDeferredNavigationRequestStore(routes, options);
            await store.EnqueueAsync(RouterNavigationRequest.FromRoute(
                new TestRoute("committed"),
                NavigationRequestSource.AppLink));
            byte[] committedBytes = await File.ReadAllBytesAsync(path);
            string orphanedTempPath = $"{path}.interrupted.tmp";
            await File.WriteAllTextAsync(orphanedTempPath, "{\"schemaVersion\":3");

            var reloaded = new MauiFileDeferredNavigationRequestStore(routes, options);
            await using IDeferredNavigationRequestLease lease = await reloaded.AcquireReplayLeaseAsync();

            Assert.Equal("committed", Assert.IsType<TestRoute>(Assert.Single(lease.Requests).Route).Id);
            Assert.Equal(committedBytes, await File.ReadAllBytesAsync(path));
            Assert.True(File.Exists(orphanedTempPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
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

    [Fact]
    public async Task ReplayLeaseAcknowledgement_IsIdempotentAfterConcurrentBoundedEviction()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "deferred-requests.json");
        var options = new MauiFileDeferredNavigationRequestStoreOptions
        {
            Path = path,
            BaseUri = BaseUri,
            MaximumPendingRequests = 2
        };

        try
        {
            var store = new MauiFileDeferredNavigationRequestStore(routes, options);
            await store.EnqueueAsync(RouterNavigationRequest.FromRoute(
                new TestRoute("first"),
                NavigationRequestSource.AppLink));
            await store.EnqueueAsync(RouterNavigationRequest.FromRoute(
                new TestRoute("second"),
                NavigationRequestSource.AppLink));

            await using (IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync())
            {
                await store.EnqueueAsync(RouterNavigationRequest.FromRoute(
                    new TestRoute("third"),
                    NavigationRequestSource.AppLink));

                await lease.AcknowledgeAsync(0);
                await lease.AcknowledgeAsync(1);
            }

            var reopened = new MauiFileDeferredNavigationRequestStore(routes, options);
            await using IDeferredNavigationRequestLease remaining = await reopened.AcquireReplayLeaseAsync();
            Assert.Equal("third", Assert.IsType<TestRoute>(Assert.Single(remaining.Requests).Route).Id);
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

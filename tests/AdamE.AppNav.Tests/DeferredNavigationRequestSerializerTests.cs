using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Tests;

public sealed class DeferredNavigationRequestSerializerTests
{
    private static readonly Uri BaseUri = new("https://example.com/");

    [Fact]
    public void CreateSnapshotAndRestore_RoundTripsCanonicalAndRestorableStateButDropsEphemeral()
    {
        var missionId = new RouteMetadataKey<string>("missionId");
        var draftId = new RouteMetadataKey<string>("coverImageDraftId");
        var replyId = new RouteMetadataKey<string>("replyCommentId");
        var registry = RouteStateRegistry.Create(builder => builder
            .Canonical(missionId)
            .Restorable(draftId)
            .Ephemeral(replyId));
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoutes.StoreRoute>(
            "/stores/{storeId}",
            route => route
                .QueryMetadata(missionId)
                .QueryMetadata(draftId)
                .QueryMetadata(replyId)));
        var serializer = new DeferredNavigationRequestSerializer(routes, new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = BaseUri,
            MetadataSerializer = new PassThroughMetadataSerializer(),
            RouteStateRegistry = registry
        });
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.AppLink,
            windowId: "main",
            metadata: new Dictionary<string, object?>
            {
                [missionId.Name] = "mission-1",
                [draftId.Name] = "draft-1",
                [replyId.Name] = "reply-1",
                ["request-id"] = "abc-123"
            },
            disposition: RouterNavigationDisposition.ReplaceCurrent);

        var snapshot = serializer.CreateSnapshot([request]);
        var restored = Assert.Single(serializer.Restore(snapshot));

        var snapshotRequest = Assert.Single(snapshot.Requests);
        var snapshotMetadata = Assert.IsAssignableFrom<IReadOnlyDictionary<string, NavigationMetadataValueSnapshot>>(snapshotRequest.Metadata);
        Assert.Equal("https://example.com/stores/northwind?missionId=mission-1", snapshotRequest.RouteUri);
        Assert.False(snapshotMetadata.ContainsKey(missionId.Name));
        Assert.Equal("draft-1", snapshotMetadata[draftId.Name].Value);
        Assert.False(snapshotMetadata.ContainsKey(replyId.Name));
        Assert.Equal("abc-123", snapshotMetadata["request-id"].Value);

        Assert.Equal("main", restored.WindowId);
        Assert.Equal(RouterNavigationDisposition.ReplaceCurrent, restored.Disposition);
        Assert.Equal("mission-1", restored.Metadata[missionId.Name]);
        Assert.Equal("draft-1", restored.Metadata[draftId.Name]);
        Assert.False(restored.Metadata.ContainsKey(replyId.Name));
        Assert.Equal("abc-123", restored.Metadata["request-id"]);
    }

    [Fact]
    public void CreateSnapshotAndRestore_PreservesCanonicalStateForUriOnlyRequests()
    {
        var missionId = new RouteMetadataKey<string>("missionId");
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoutes.StoreRoute>(
            "/stores/{storeId}",
            route => route.QueryMetadata(missionId)));
        var serializer = new DeferredNavigationRequestSerializer(routes, new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = BaseUri,
            RouteStateRegistry = RouteStateRegistry.Create(builder => builder.Canonical(missionId))
        });
        var request = RouterNavigationRequest.FromUri(
            new Uri("https://example.com/stores/northwind?missionId=mission-1"),
            NavigationRequestSource.AppLink);

        var restored = Assert.Single(serializer.Restore(serializer.CreateSnapshot([request])));

        Assert.Equal(request.Uri, restored.Uri);
        Assert.Null(restored.Route);
        Assert.Equal("mission-1", restored.Metadata[missionId.Name]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Restore_RejectsUnsupportedSchemaVersions(int schemaVersion)
    {
        var serializer = new DeferredNavigationRequestSerializer(TestRoutes.CreateTable());
        var snapshot = new DeferredNavigationRequestStoreSnapshot
        {
            SchemaVersion = schemaVersion,
            Requests = []
        };

        var exception = Assert.Throws<UnsupportedDeferredNavigationRequestSchemaException>(
            () => serializer.Restore(snapshot));

        Assert.Equal(schemaVersion, exception.ActualVersion);
        Assert.Equal(DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion, exception.SupportedVersion);
    }

    [Fact]
    public void CreateSnapshotAndRestore_UsesRouteCodecForRegisteredCustomState()
    {
        var draft = new RouteMetadataKey<DraftId>("draft");
        var routes = RouteTable.Create(builder => builder
            .AddValueCodec<DraftId>(
                static value => new DraftId(Guid.Parse(value)),
                static value => value.Value.ToString("N"))
            .MapRoute<TestRoutes.StoreRoute>("/stores/{storeId}"));
        var serializer = new DeferredNavigationRequestSerializer(routes, new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = BaseUri,
            RouteStateRegistry = RouteStateRegistry.Create(builder => builder.Restorable(draft))
        });
        var draftId = new DraftId(Guid.NewGuid());
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.InAppCommand,
            metadata: new Dictionary<string, object?> { [draft.Name] = draftId });

        var snapshot = serializer.CreateSnapshot([request]);
        NavigationMetadataValueSnapshot persisted = Assert.Single(snapshot.Requests).Metadata![draft.Name];
        var restored = Assert.Single(serializer.Restore(snapshot));

        Assert.Equal(DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.Null(persisted.Type);
        Assert.Equal(draftId.Value.ToString("N"), persisted.Value);
        Assert.Equal(draftId, restored.Metadata[draft.Name]);
    }

    [Fact]
    public void CustomMetadataSerializerPersistsStableScalarDiscriminators()
    {
        var routes = TestRoutes.CreateTable();
        var serializer = new DeferredNavigationRequestSerializer(routes, new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = BaseUri,
            MetadataSerializer = new PassThroughMetadataSerializer()
        });
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.InAppCommand,
            metadata: new Dictionary<string, object?> { ["attempt"] = 3 });

        var snapshot = serializer.CreateSnapshot([request]);
        NavigationMetadataValueSnapshot persisted = Assert.Single(snapshot.Requests).Metadata!["attempt"];
        var restored = Assert.Single(serializer.Restore(snapshot));

        Assert.Equal("int32", persisted.Type);
        Assert.Equal("3", persisted.Value);
        Assert.Equal(3, restored.Metadata["attempt"]);
    }

    [Theory]
    [InlineData(true, "int32", "3")]
    [InlineData(false, "int32", null)]
    public void RestoreRejectsContradictoryCustomMetadataValues(
        bool isNull,
        string? type,
        string? value)
    {
        var serializer = new DeferredNavigationRequestSerializer(TestRoutes.CreateTable(),
            new DeferredNavigationRequestPersistenceOptions
            {
                BaseUri = BaseUri,
                MetadataSerializer = new PassThroughMetadataSerializer()
            });
        DeferredNavigationRequestStoreSnapshot snapshot = serializer.CreateSnapshot(
            [RouterNavigationRequest.FromRoute(
                new TestRoutes.StoreRoute("northwind"),
                NavigationRequestSource.InAppCommand,
                metadata: new Dictionary<string, object?> { ["attempt"] = 3 })]);
        NavigationRequestSnapshot request = snapshot.Requests[0];
        snapshot = snapshot with
        {
            Requests =
            [
                request with
                {
                    Metadata = new Dictionary<string, NavigationMetadataValueSnapshot>
                    {
                        ["attempt"] = new NavigationMetadataValueSnapshot(type, value, isNull)
                    }
                }
            ]
        };

        InvalidDeferredNavigationRequestDataException exception =
            Assert.Throws<InvalidDeferredNavigationRequestDataException>(() => serializer.Restore(snapshot));

        Assert.Equal(0, exception.RequestIndex);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public void CustomMetadataSerializerRejectsUnstableRuntimeTypes()
    {
        var serializer = new DeferredNavigationRequestSerializer(TestRoutes.CreateTable(),
            new DeferredNavigationRequestPersistenceOptions
            {
                BaseUri = BaseUri,
                MetadataSerializer = new PassThroughMetadataSerializer()
            });
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.InAppCommand,
            metadata: new Dictionary<string, object?> { ["timestamp"] = DateTime.UtcNow });

        var exception = Assert.Throws<NotSupportedException>(() => serializer.CreateSnapshot([request]));

        Assert.Contains("Return a supported scalar value", exception.Message);
    }

    [Fact]
    public void CreateSnapshotAndRestore_RoundTripsProvenance()
    {
        var routes = TestRoutes.CreateTable();
        var serializer = new DeferredNavigationRequestSerializer(routes, new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = BaseUri
        });
        var provenance = new NavigationRequestProvenance(
            provider: "push",
            originalUri: new Uri("https://example.com/stores/northwind"),
            referrerUri: new Uri("https://notifications.example/message/1"),
            correlationId: "correlation-1",
            attributes: new Dictionary<string, string?>
            {
                ["notificationId"] = "notification-1"
            });
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.Push,
            provenance: provenance);

        var snapshot = serializer.CreateSnapshot([request]);
        var restored = Assert.Single(serializer.Restore(snapshot));

        var snapshotProvenance = Assert.Single(snapshot.Requests).Provenance!;
        Assert.Equal("push", snapshotProvenance.Provider);
        Assert.Equal("https://example.com/stores/northwind", snapshotProvenance.OriginalUri);
        Assert.Equal("https://notifications.example/message/1", snapshotProvenance.ReferrerUri);
        Assert.Equal("correlation-1", snapshotProvenance.CorrelationId);
        Assert.Equal("notification-1", snapshotProvenance.Attributes!["notificationId"]);
        Assert.Equal(provenance, restored.Provenance);
    }

    [Fact]
    public void Restore_RejectsMalformedProvenanceWithoutPartiallyRestoringRequest()
    {
        var routes = TestRoutes.CreateTable();
        var serializer = new DeferredNavigationRequestSerializer(routes, new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = BaseUri
        });
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.Push,
            provenance: new NavigationRequestProvenance(
                provider: "push",
                originalUri: new Uri("https://example.com/stores/northwind"),
                referrerUri: new Uri("https://notifications.example/message/1")));
        var snapshot = serializer.CreateSnapshot([request]);
        var snapshotRequest = snapshot.Requests[0];
        var malformed = snapshot with
        {
            Requests = new[]
            {
                snapshotRequest with
                {
                    Provenance = snapshotRequest.Provenance! with
                    {
                        OriginalUri = "http://[::1",
                        ReferrerUri = "https://notifications.example/message/1"
                    }
                }
            }
        };

        var exception = Assert.Throws<InvalidDeferredNavigationRequestDataException>(
            () => serializer.Restore(malformed));

        Assert.Equal(0, exception.RequestIndex);
        Assert.IsType<FormatException>(exception.InnerException);
    }

    [Fact]
    public void Restore_ReportsExactIndexForInvalidSchemaTwoRecord()
    {
        var serializer = new DeferredNavigationRequestSerializer(
            TestRoutes.CreateTable(),
            new DeferredNavigationRequestPersistenceOptions { BaseUri = BaseUri });
        DeferredNavigationRequestStoreSnapshot snapshot = serializer.CreateSnapshot(
            [
                RouterNavigationRequest.FromRoute(
                    new TestRoutes.StoreRoute("first"),
                    NavigationRequestSource.AppLink),
                RouterNavigationRequest.FromRoute(
                    new TestRoutes.StoreRoute("second"),
                    NavigationRequestSource.AppLink)
            ]);
        snapshot = snapshot with
        {
            Requests =
            [
                snapshot.Requests[0],
                snapshot.Requests[1] with { Source = (NavigationRequestSource)999 }
            ]
        };

        var exception = Assert.Throws<InvalidDeferredNavigationRequestDataException>(
            () => serializer.Restore(snapshot));

        Assert.Equal(1, exception.RequestIndex);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    private sealed class PassThroughMetadataSerializer : INavigationRequestMetadataSerializer
    {
        public IReadOnlyDictionary<string, object?>? Serialize(IReadOnlyDictionary<string, object?> metadata) => metadata;

        public IReadOnlyDictionary<string, object?>? Deserialize(IReadOnlyDictionary<string, object?> metadata) => metadata;
    }

    private readonly record struct DraftId(Guid Value);
}

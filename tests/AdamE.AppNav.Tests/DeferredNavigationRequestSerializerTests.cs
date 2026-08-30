using System.Text.Json;
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

        Assert.Null(restored.Uri);
        Assert.Equal(new TestRoutes.StoreRoute("northwind"), restored.Route);
        Assert.Equal("mission-1", restored.Metadata[missionId.Name]);
    }

    [Fact]
    public void CreateSnapshot_RejectsRouteWithAnOptionalPathHole()
    {
        var routes = RouteTable.Create(builder => builder.MapRoute<OptionalReportRoute>(
            "/reports/{year?}/{month?}"));
        var serializer = new DeferredNavigationRequestSerializer(routes, new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = BaseUri
        });
        var request = RouterNavigationRequest.FromRoute(
            new OptionalReportRoute(null, "august"),
            NavigationRequestSource.InAppCommand);

        var exception = Assert.Throws<InvalidOperationException>(() => serializer.CreateSnapshot([request]));

        Assert.Contains("/reports/{year?}/{month?}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("year", exception.Message, StringComparison.Ordinal);
        Assert.Contains("month", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Restore_RejectsUnsupportedSchemaVersions(int schemaVersion)
    {
        var serializer = new DeferredNavigationRequestSerializer(
            TestRoutes.CreateTable(),
            new DeferredNavigationRequestPersistenceOptions { BaseUri = BaseUri });
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
    public void CreateSnapshotAndRestore_PreservesOnlyProvenanceProvider()
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
        Assert.Equal("push", restored.Provenance!.Provider);
        Assert.Null(restored.Provenance.OriginalUri);
        Assert.Null(restored.Provenance.ReferrerUri);
        Assert.Null(restored.Provenance.CorrelationId);
        Assert.Null(restored.Provenance.IsColdStart);
        Assert.Empty(restored.Provenance.Attributes);
    }

    [Fact]
    public void SnapshotJson_DoesNotContainTransportOrProvenanceSecrets()
    {
        var routes = TestRoutes.CreateTable();
        var serializer = new DeferredNavigationRequestSerializer(routes, new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = BaseUri
        });
        var request = RouterNavigationRequest.FromUri(
            new Uri("https://example.com/stores/northwind?transportSecret=raw-secret"),
            NavigationRequestSource.Push,
            provenance: new NavigationRequestProvenance(
                provider: "push",
                originalUri: new Uri("https://source.example/path?token=original-secret"),
                referrerUri: new Uri("https://referrer.example/path?token=referrer-secret"),
                correlationId: "correlation-secret",
                isColdStart: true,
                attributes: new Dictionary<string, string?>
                {
                    ["authorization"] = "attribute-secret"
                }));
        var snapshot = serializer.CreateSnapshot([request]);
        string json = JsonSerializer.Serialize(snapshot);

        Assert.DoesNotContain("raw-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("original-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("referrer-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("correlation-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("attribute-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("transportSecret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("originalUri", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("referrerUri", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correlationId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isColdStart", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attributes", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restore_ReportsExactIndexForInvalidSchemaThreeRecord()
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

    [Theory]
    [InlineData("/stores/northwind")]
    [InlineData("https://attacker.example/stores/northwind")]
    [InlineData("https://example.com/stores/northwind?unexpected=value")]
    public void Restore_RejectsNonCanonicalOrWrongOriginRouteUris(string routeUri)
    {
        var serializer = new DeferredNavigationRequestSerializer(
            TestRoutes.CreateTable(),
            new DeferredNavigationRequestPersistenceOptions { BaseUri = BaseUri });
        DeferredNavigationRequestStoreSnapshot snapshot = serializer.CreateSnapshot(
            [RouterNavigationRequest.FromRoute(
                new TestRoutes.StoreRoute("northwind"),
                NavigationRequestSource.AppLink)]);
        snapshot = snapshot with
        {
            Requests = [snapshot.Requests[0] with { RouteUri = routeUri }]
        };

        Assert.Throws<InvalidDeferredNavigationRequestDataException>(() => serializer.Restore(snapshot));
    }

    [Fact]
    public void Constructor_RequiresExplicitAbsoluteBaseUri()
    {
        Assert.Equal(3, DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion);
        Assert.Throws<InvalidOperationException>(() => new DeferredNavigationRequestSerializer(
            TestRoutes.CreateTable(),
            new DeferredNavigationRequestPersistenceOptions()));
        Assert.Throws<ArgumentException>(() => new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = new Uri("relative", UriKind.Relative)
        });
        Assert.Throws<ArgumentException>(() => new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = new Uri("https://user:secret@example.com/")
        });
        Assert.Throws<ArgumentException>(() => new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = new Uri("https://@example.com/")
        });
        Assert.Throws<ArgumentException>(() => new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = new Uri("https://example.com/base/")
        });
        Assert.Throws<ArgumentException>(() => new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = new Uri("https://example.com/?token=secret")
        });
        Assert.Throws<ArgumentException>(() => new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = new Uri("https://example.com/?")
        });
        Assert.Throws<ArgumentException>(() => new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = new Uri("https://example.com/#")
        });
    }

    private sealed class PassThroughMetadataSerializer : INavigationRequestMetadataSerializer
    {
        public IReadOnlyDictionary<string, object?>? Serialize(IReadOnlyDictionary<string, object?> metadata) => metadata;

        public IReadOnlyDictionary<string, object?>? Deserialize(IReadOnlyDictionary<string, object?> metadata) => metadata;
    }

    private readonly record struct DraftId(Guid Value);

    private sealed record OptionalReportRoute(string? Year, string? Month) : AppRoute;
}

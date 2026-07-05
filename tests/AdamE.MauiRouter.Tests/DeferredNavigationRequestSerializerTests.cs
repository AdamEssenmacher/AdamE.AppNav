using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;

namespace AdamE.MauiRouter.Tests;

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

        Assert.IsType<TestRoutes.StoreRoute>(restored.Route);
        Assert.Equal("mission-1", restored.Metadata[missionId.Name]);
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
    public void Restore_IgnoresMalformedProvenanceUrisWithoutDroppingRequest()
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

        var restored = Assert.Single(serializer.Restore(malformed));

        Assert.Equal("push", restored.Provenance!.Provider);
        Assert.Null(restored.Provenance.OriginalUri);
        Assert.Equal(new Uri("https://notifications.example/message/1"), restored.Provenance.ReferrerUri);
    }

    private sealed class PassThroughMetadataSerializer : INavigationRequestMetadataSerializer
    {
        public IReadOnlyDictionary<string, object?>? Serialize(IReadOnlyDictionary<string, object?> metadata) => metadata;

        public IReadOnlyDictionary<string, object?>? Deserialize(IReadOnlyDictionary<string, object?> metadata) => metadata;
    }
}

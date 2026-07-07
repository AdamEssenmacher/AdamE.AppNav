using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Tests;

public sealed class RouterNavigationRequestConversionTests
{
    [Fact]
    public void FromRouteRequest_SnapshotsMetadataAndPreservesTransportFields()
    {
        var routeMetadata = new Dictionary<string, object?>
        {
            [MissionIdMetadata.Name] = "mission-1"
        };
        var routeRequest = new AppRouteRequest(new TestRoutes.StoreRoute("northwind"), routeMetadata);

        var request = RouterNavigationRequest.FromRouteRequest(
            routeRequest,
            NavigationRequestSource.AppLink,
            windowId: "main",
            disposition: RouterNavigationDisposition.ReplaceCurrent);

        routeMetadata[MissionIdMetadata.Name] = "mission-2";
        routeMetadata["extra"] = true;

        Assert.Equal(routeRequest.Route, request.Route);
        Assert.Equal(NavigationRequestSource.AppLink, request.Source);
        Assert.Equal("main", request.WindowId);
        Assert.Equal(RouterNavigationDisposition.ReplaceCurrent, request.Disposition);
        Assert.Equal("mission-1", request.Metadata[MissionIdMetadata.Name]);
        Assert.False(request.Metadata.ContainsKey("extra"));
    }

    [Fact]
    public void FromRouteRequest_MergesExtraMetadataWithExtraMetadataWinning()
    {
        var routeRequest = AppRouteRequest
            .For(new TestRoutes.StoreRoute("northwind"))
            .WithMetadata(MissionIdMetadata, "mission-1")
            .WithMetadata(ReplyCommentIdMetadata, "comment-1");
        var extraMetadata = new Dictionary<string, object?>
        {
            [MissionIdMetadata.Name] = "mission-2",
            [RankMetadata.Name] = 42
        };

        var request = RouterNavigationRequest.FromRouteRequest(
            routeRequest,
            NavigationRequestSource.InAppCommand,
            extraMetadata: extraMetadata);

        Assert.Equal("mission-2", request.Metadata[MissionIdMetadata.Name]);
        Assert.Equal("comment-1", request.Metadata[ReplyCommentIdMetadata.Name]);
        Assert.Equal(42, request.Metadata[RankMetadata.Name]);
    }

    [Fact]
    public void FactoriesPreserveSuppliedProvenance()
    {
        var uri = new Uri("https://example.com/stores/northwind");
        var route = new TestRoutes.StoreRoute("northwind");
        var routeRequest = AppRouteRequest.For(route);
        var provenance = new NavigationRequestProvenance(
            provider: "branch",
            originalUri: uri,
            referrerUri: new Uri("https://example.com/referrer"),
            correlationId: "correlation-1",
            isColdStart: true,
            attributes: new Dictionary<string, string?>
            {
                ["campaign"] = "spring",
                ["empty"] = null
            });

        var uriRequest = RouterNavigationRequest.FromUri(
            uri,
            NavigationRequestSource.AppLink,
            provenance: provenance);
        var routeOnlyRequest = RouterNavigationRequest.FromRoute(
            route,
            NavigationRequestSource.Push,
            provenance: provenance);
        var routeRequestRequest = RouterNavigationRequest.FromRouteRequest(
            routeRequest,
            NavigationRequestSource.InAppCommand,
            provenance: provenance);

        Assert.Equal(provenance, uriRequest.Provenance);
        Assert.Equal(provenance, routeOnlyRequest.Provenance);
        Assert.Equal(provenance, routeRequestRequest.Provenance);
        Assert.Empty(routeRequestRequest.Metadata);
    }

    private static readonly RouteMetadataKey<string> MissionIdMetadata = new("missionId");
    private static readonly RouteMetadataKey<string> ReplyCommentIdMetadata = new("replyCommentId");
    private static readonly RouteMetadataKey<int> RankMetadata = new("rank");
}

using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Tests;

public sealed class AppRouteRequestTests
{
    [Fact]
    public void MetadataIsSnapshotted()
    {
        var metadata = new Dictionary<string, object?>
        {
            [MissionIdMetadata.Name] = "mission-1"
        };

        var request = new AppRouteRequest(new TestRoutes.StoreRoute("northwind"), metadata);
        metadata[MissionIdMetadata.Name] = "mission-2";
        metadata["extra"] = true;

        Assert.Equal("mission-1", request.Metadata[MissionIdMetadata.Name]);
        Assert.False(request.Metadata.ContainsKey("extra"));
    }

    [Fact]
    public void WithMetadataNullRemovesKeyWithoutMutatingPreviousRequest()
    {
        var original = AppRouteRequest
            .For(new TestRoutes.StoreRoute("northwind"))
            .WithMetadata(MissionIdMetadata, "mission-1");

        var updated = original.WithMetadata(MissionIdMetadata, null);

        Assert.True(original.TryGetMetadata(MissionIdMetadata, out var originalMissionId));
        Assert.Equal("mission-1", originalMissionId);
        Assert.False(updated.Metadata.ContainsKey(MissionIdMetadata.Name));
        Assert.False(updated.TryGetMetadata(MissionIdMetadata, out _));
    }

    [Fact]
    public void TryGetMetadataReturnsTrueForTypedValue()
    {
        var request = AppRouteRequest
            .For(new TestRoutes.StoreRoute("northwind"))
            .WithMetadata(RankMetadata, 42);

        Assert.True(request.TryGetMetadata(RankMetadata, out var rank));
        Assert.Equal(42, rank);
    }

    private static readonly RouteMetadataKey<string> MissionIdMetadata = new("missionId");
    private static readonly RouteMetadataKey<int> RankMetadata = new("rank");
}

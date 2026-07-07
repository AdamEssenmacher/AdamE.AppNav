using AdamE.AppNav;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Tests;

public sealed class RouteStateRegistryTests
{
    private static readonly RouteMetadataKey<string> MissionId = new("missionId");
    private static readonly RouteMetadataKey<string> DraftId = new("coverImageDraftId");
    private static readonly RouteMetadataKey<string> ReplyId = new("replyCommentId");

    private static readonly RouteStateRegistry Registry = RouteStateRegistry.Create(
        static builder => builder
            .Canonical(MissionId)
            .Restorable(DraftId)
            .Ephemeral(ReplyId));

    [Fact]
    public void FilterKnown_ReturnsOnlyRegisteredRouteState()
    {
        IReadOnlyDictionary<string, object?> metadata = new Dictionary<string, object?>
        {
            [MissionId.Name] = "mission-1",
            [DraftId.Name] = "draft-1",
            ["other"] = "ignored"
        };

        var filtered = Registry.FilterKnown(metadata);

        Assert.Equal(2, filtered.Count);
        Assert.Equal("mission-1", filtered[MissionId.Name]);
        Assert.Equal("draft-1", filtered[DraftId.Name]);
        Assert.False(filtered.ContainsKey("other"));
    }

    [Fact]
    public void FilterRestorable_ReturnsOnlyRestorableRouteState()
    {
        IReadOnlyDictionary<string, object?> metadata = new Dictionary<string, object?>
        {
            [MissionId.Name] = "mission-1",
            [DraftId.Name] = "draft-1",
            [ReplyId.Name] = "reply-1"
        };

        var filtered = Registry.FilterRestorable(metadata);

        Assert.Single(filtered);
        Assert.Equal("draft-1", filtered[DraftId.Name]);
    }

    [Fact]
    public void Canonicalize_KeepsOnlyCanonicalRouteState()
    {
        var request = AppRouteRequest
            .For(new TestRoutes.StoreRoute("northwind"))
            .WithMetadata(MissionId, "mission-1")
            .WithMetadata(DraftId, "draft-1")
            .WithMetadata(ReplyId, "reply-1");

        var canonical = Registry.Canonicalize(request);

        Assert.Equal(request.Route, canonical.Route);
        Assert.Single(canonical.Metadata);
        Assert.Equal("mission-1", canonical.Metadata[MissionId.Name]);
    }
}

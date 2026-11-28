using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Tests;

public sealed class NavigationRequestEquivalenceComparerTests
{
    [Theory]
    [InlineData(nameof(RouterNavigationRequest.Uri))]
    [InlineData(nameof(RouterNavigationRequest.Route))]
    [InlineData(nameof(RouterNavigationRequest.Source))]
    [InlineData(nameof(RouterNavigationRequest.WindowId))]
    [InlineData(nameof(RouterNavigationRequest.Disposition))]
    [InlineData(nameof(RouterNavigationRequest.Provenance))]
    [InlineData(nameof(RouterNavigationRequest.Metadata))]
    public void Equals_ReturnsFalseWhenOneFieldDiffers(string field)
    {
        var uri = new Uri("https://example.com/stores/first");
        var original = RouterNavigationRequest.FromUri(
            uri,
            NavigationRequestSource.AppLink,
            windowId: "main",
            metadata: new Dictionary<string, object?> { ["request-id"] = "one" });
        if (field == nameof(RouterNavigationRequest.Route))
            original = original.WithTarget(new TestRoutes.StoreRoute("first"));

        var changed = field switch
        {
            nameof(RouterNavigationRequest.Uri) => original.WithTarget(new Uri("https://example.com/stores/second")),
            nameof(RouterNavigationRequest.Route) => original.WithTarget(new TestRoutes.StoreRoute("second")),
            nameof(RouterNavigationRequest.Source) => RouterNavigationRequest.FromUri(
                uri, NavigationRequestSource.Push, original.WindowId, original.Metadata)
                with { Timestamp = original.Timestamp },
            nameof(RouterNavigationRequest.WindowId) => original with { WindowId = "Main" },
            nameof(RouterNavigationRequest.Disposition) => original with { Disposition = RouterNavigationDisposition.Canonical },
            nameof(RouterNavigationRequest.Provenance) => original with { Provenance = new NavigationRequestProvenance("push") },
            nameof(RouterNavigationRequest.Metadata) => original with
            {
                Metadata = new Dictionary<string, object?> { ["request-id"] = "two" }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        Assert.False(NavigationRequestEquivalenceComparer.Instance.Equals(original, changed));
    }

    [Fact]
    public void EqualRequests_HashIdenticallyAcrossTimestampsAndMetadataInsertionOrders()
    {
        var original = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("first"),
            NavigationRequestSource.AppLink,
            metadata: new Dictionary<string, object?> { ["request-id"] = "one", ["attempt"] = 1 });
        var equivalent = original with
        {
            Timestamp = original.Timestamp.AddMinutes(1),
            Metadata = new Dictionary<string, object?> { ["attempt"] = 1, ["request-id"] = "one" }
        };

        var comparer = NavigationRequestEquivalenceComparer.Instance;
        Assert.True(comparer.Equals(original, equivalent));
        Assert.Equal(comparer.GetHashCode(original), comparer.GetHashCode(equivalent));
    }
}

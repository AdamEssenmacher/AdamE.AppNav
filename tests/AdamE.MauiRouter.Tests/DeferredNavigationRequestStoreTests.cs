using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Tests;

public sealed class DeferredNavigationRequestStoreTests
{
    [Fact]
    public async Task EnqueueAsync_MultipleRequests_DequeueReturnsFifoOrder()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("first"), NavigationRequestSource.AppLink);
        var second = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("second"), NavigationRequestSource.Push);

        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);

        Assert.True(await store.HasDeferredRequestsAsync());
        Assert.Same(first, await store.TryDequeueAsync());
        Assert.Same(second, await store.TryDequeueAsync());
        Assert.False(await store.HasDeferredRequestsAsync());
    }

    [Fact]
    public async Task TryDequeueAsync_EmptyStore_ReturnsNull()
    {
        var store = new InMemoryDeferredNavigationRequestStore();

        var request = await store.TryDequeueAsync();

        Assert.Null(request);
        Assert.False(await store.HasDeferredRequestsAsync());
    }

    [Fact]
    public async Task DrainAsync_ReturnsFifoRequestsAndEmptiesStore()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("first"), NavigationRequestSource.AppLink);
        var second = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("second"), NavigationRequestSource.Push);
        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);

        var drained = await store.DrainAsync();

        Assert.Equal([first, second], drained);
        Assert.False(await store.HasDeferredRequestsAsync());
        Assert.Null(await store.TryDequeueAsync());
    }

    [Fact]
    public async Task DrainAsync_ReturnsSnapshotThatIsNotAffectedByLaterEnqueue()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("first"), NavigationRequestSource.AppLink);
        var second = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("second"), NavigationRequestSource.Push);
        await store.EnqueueAsync(first);

        var drained = await store.DrainAsync();
        await store.EnqueueAsync(second);

        Assert.Equal([first], drained);
        Assert.Same(second, await store.TryDequeueAsync());
    }

    [Fact]
    public async Task ClearAsync_EmptiesStore()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        await store.EnqueueAsync(RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("first"), NavigationRequestSource.AppLink));

        await store.ClearAsync();

        Assert.False(await store.HasDeferredRequestsAsync());
        Assert.Null(await store.TryDequeueAsync());
    }

    [Fact]
    public async Task EnqueueAsync_CollapsesExactDuplicatesIgnoringTimestamp()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("first"),
            NavigationRequestSource.AppLink,
            metadata: new Dictionary<string, object?> { ["request-id"] = "one" });
        var duplicate = first with
        {
            Timestamp = first.Timestamp.AddMinutes(5)
        };

        await store.EnqueueAsync(first);
        await store.EnqueueAsync(duplicate);

        Assert.Equal([first], await store.DrainAsync());
    }

    [Fact]
    public async Task EnqueueAsync_TreatsDifferentProvenanceAsDistinct()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("first"),
            NavigationRequestSource.Push,
            provenance: new NavigationRequestProvenance(
                provider: "firebase-push",
                correlationId: "notification-1"));
        var second = first with
        {
            Provenance = new NavigationRequestProvenance(
                provider: "firebase-push",
                correlationId: "notification-2")
        };

        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);

        Assert.Equal([first, second], await store.DrainAsync());
    }

    [Fact]
    public async Task EnqueueAsync_NullRequest_Throws()
    {
        var store = new InMemoryDeferredNavigationRequestStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.EnqueueAsync(null!).AsTask());
    }
}

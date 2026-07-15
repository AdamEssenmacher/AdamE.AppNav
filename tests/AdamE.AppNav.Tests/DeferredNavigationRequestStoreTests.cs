using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Tests;

public sealed class DeferredNavigationRequestStoreTests
{
    [Fact]
    public async Task ReplayLease_AcknowledgesRequestsInFifoSnapshot()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = Request("first", NavigationRequestSource.AppLink);
        var second = Request("second", NavigationRequestSource.Push);
        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);

        await using (IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync())
        {
            Assert.Equal([first, second], lease.Requests);
            Assert.True(await store.HasDeferredRequestsAsync());
            await lease.AcknowledgeAsync(0);
            await lease.AcknowledgeAsync(1);
        }

        Assert.False(await store.HasDeferredRequestsAsync());
    }

    [Fact]
    public async Task ReplayLease_EmptyStoreReturnsEmptySnapshot()
    {
        var store = new InMemoryDeferredNavigationRequestStore();

        await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();

        Assert.Empty(lease.Requests);
        Assert.False(await store.HasDeferredRequestsAsync());
    }

    [Fact]
    public async Task ReplayLease_DoesNotRemoveUnacknowledgedRequestsAndExcludesLaterEnqueue()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = Request("first", NavigationRequestSource.AppLink);
        var second = Request("second", NavigationRequestSource.Push);
        await store.EnqueueAsync(first);

        await using (IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync())
        {
            await store.EnqueueAsync(second);
            Assert.Equal([first], lease.Requests);
        }

        await using IDeferredNavigationRequestLease nextLease = await store.AcquireReplayLeaseAsync();
        Assert.Equal([first, second], nextLease.Requests);
    }

    [Fact]
    public async Task ReplayLease_AcknowledgedLaterRequestLeavesFailedOlderRequestBeforeNewEnqueue()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = Request("first", NavigationRequestSource.AppLink);
        var second = Request("second", NavigationRequestSource.Push);
        var third = Request("third", NavigationRequestSource.Push);
        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);

        await using (IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync())
        {
            await store.EnqueueAsync(third);
            await lease.AcknowledgeAsync(1);
        }

        await using IDeferredNavigationRequestLease nextLease = await store.AcquireReplayLeaseAsync();
        Assert.Equal([first, third], nextLease.Requests);
    }

    [Fact]
    public async Task AcquireReplayLeaseAsync_WaitsForExistingLease()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        IDeferredNavigationRequestLease firstLease = await store.AcquireReplayLeaseAsync();

        Task<IDeferredNavigationRequestLease> pending = store.AcquireReplayLeaseAsync().AsTask();
        Assert.False(pending.IsCompleted);

        await firstLease.DisposeAsync();
        await using IDeferredNavigationRequestLease secondLease = await pending;
        Assert.Empty(secondLease.Requests);
    }

    [Fact]
    public async Task ClearAsync_EmptiesStore()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        await store.EnqueueAsync(Request("first", NavigationRequestSource.AppLink));

        await store.ClearAsync();

        Assert.False(await store.HasDeferredRequestsAsync());
        await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();
        Assert.Empty(lease.Requests);
    }

    [Fact]
    public async Task EnqueueAsync_CollapsesExactDuplicatesIgnoringTimestamp()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("first"),
            NavigationRequestSource.AppLink,
            metadata: new Dictionary<string, object?> { ["request-id"] = "one" });
        var duplicate = first with { Timestamp = first.Timestamp.AddMinutes(5) };

        await store.EnqueueAsync(first);
        await store.EnqueueAsync(duplicate);

        await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();
        Assert.Equal([first], lease.Requests);
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

        await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();
        Assert.Equal([first, second], lease.Requests);
    }

    [Fact]
    public async Task ReplayLease_RejectsDuplicateAndOutOfRangeAcknowledgements()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        await store.EnqueueAsync(Request("first", NavigationRequestSource.AppLink));
        await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();

        await lease.AcknowledgeAsync(0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.AcknowledgeAsync(0).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => lease.AcknowledgeAsync(1).AsTask());
    }

    [Fact]
    public async Task EnqueueAsync_NullRequest_Throws()
    {
        var store = new InMemoryDeferredNavigationRequestStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.EnqueueAsync(null!).AsTask());
    }

    private static RouterNavigationRequest Request(string id, NavigationRequestSource source)
    {
        return RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute(id), source);
    }
}

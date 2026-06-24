using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Tests;

public sealed class DeferredNavigationRequestReplayerTests
{
    [Fact]
    public async Task ReplayAsync_ReplaysDeferredRequestsInFifoOrder()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("first"), NavigationRequestSource.AppLink, windowId: "main");
        var second = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("second"),
            NavigationRequestSource.Push,
            windowId: "secondary",
            metadata: new Dictionary<string, object?> { ["request-id"] = "two" },
            disposition: RouterNavigationDisposition.ReplaceCurrent);
        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);

        var navigator = new RecordingRouterNavigator();
        var replayer = new DeferredNavigationRequestReplayer(store, navigator);

        var result = await replayer.ReplayAsync();

        Assert.Equal(new[] { first, second }, navigator.Calls);
        Assert.Equal(new DeferredNavigationReplayResult(2, 2, 0), result);
        Assert.False(await store.HasDeferredRequestsAsync());
    }

    [Fact]
    public async Task ReplayAsync_ContinuesAfterOneReplayFailure()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("first"),
            NavigationRequestSource.AppLink,
            metadata: new Dictionary<string, object?> { ["throw"] = true });
        var second = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("second"), NavigationRequestSource.Push);
        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);

        var navigator = new RecordingRouterNavigator(static request =>
            request.Metadata.TryGetValue("throw", out var value) && Equals(value, true)
                ? throw new InvalidOperationException("Replay failure.")
                : new NavigationResult(request.Route!, new NavigationPlan(NavigationState.Empty), NavigationState.Empty, Presented: true));
        var replayer = new DeferredNavigationRequestReplayer(store, navigator);

        var result = await replayer.ReplayAsync();

        Assert.Equal(new[] { first, second }, navigator.Calls);
        Assert.Equal(new DeferredNavigationReplayResult(2, 1, 1), result);
        Assert.False(await store.HasDeferredRequestsAsync());
    }

    private sealed class RecordingRouterNavigator(Func<RouterNavigationRequest, NavigationResult>? navigate = null) : IRouterNavigator
    {
        private readonly Func<RouterNavigationRequest, NavigationResult> _navigate =
            navigate ?? (request => new NavigationResult(request.Route!, new NavigationPlan(NavigationState.Empty), NavigationState.Empty, Presented: true));

        public List<RouterNavigationRequest> Calls { get; } = [];

        public NavigationState CurrentState => NavigationState.Empty;

        public NavigationHistory History => NavigationHistory.Empty;

        public ValueTask<NavigationResult> NavigateAsync(Uri uri, NavigationRequestSource source = NavigationRequestSource.InAppCommand, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(Uri uri, NavigationRequestSource source, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(Uri uri, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRoute route, NavigationRequestSource source = NavigationRequestSource.InAppCommand, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRoute route, NavigationRequestSource source, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRoute route, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRouteRequest routeRequest, NavigationRequestSource source = NavigationRequestSource.InAppCommand, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRouteRequest routeRequest, NavigationRequestSource source, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRouteRequest routeRequest, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<NavigationResult> NavigateAsync(
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return ValueTask.FromResult(_navigate(request));
        }

        public ValueTask<BackNavigationResult> BackAsync(string? windowId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> ReconcileAsync(NavigationReconciliation reconciliation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationRestoreResult> RestoreAsync(NavigationSnapshot snapshot, NavigationRestoreOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationRestoreResult> RestoreFromStoreAsync(NavigationRestoreOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WhenReconciliationIdleAsync() => Task.CompletedTask;
    }
}

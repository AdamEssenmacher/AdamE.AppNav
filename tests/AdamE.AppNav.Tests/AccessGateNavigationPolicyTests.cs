using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class AccessGateNavigationPolicyTests
{
    [Fact]
    public async Task ApplyAsync_AllowedRequest_PassesThroughUnchanged()
    {
        var request = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.AppLink);
        var policy = new AccessGateNavigationPolicy(
            new StubEvaluator(_ => NavigationAccessDecision.Allow()),
            new InMemoryDeferredNavigationRequestStore());

        var result = await policy.ApplyAsync(Context(request));

        Assert.Same(request, result);
    }

    [Fact]
    public async Task ApplyAsync_DeniedRedirect_DoesNotDeferOriginalRequest()
    {
        var request = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.AppLink, windowId: "main");
        var store = new InMemoryDeferredNavigationRequestStore();
        var policy = new AccessGateNavigationPolicy(
            new StubEvaluator(_ => NavigationAccessDecision.Redirect(
                RouterNavigationRequest.FromRoute(
                    new TestRoutes.StoreRoute("login"),
                    NavigationRequestSource.InAppCommand,
                    disposition: RouterNavigationDisposition.Canonical))),
            store);

        var result = await policy.ApplyAsync(Context(request));

        Assert.Equal("main", result.WindowId);
        Assert.False(await store.HasDeferredRequestsAsync());
    }

    [Fact]
    public async Task ApplyAsync_DeniedDeferredRedirect_BuffersOriginalRequestAndReturnsRedirect()
    {
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.AppLink,
            windowId: "main",
            metadata: new Dictionary<string, object?> { ["request-id"] = "one" });
        var store = new InMemoryDeferredNavigationRequestStore();
        var policy = new AccessGateNavigationPolicy(
            new StubEvaluator(_ => NavigationAccessDecision.DeferAndRedirect(
                RouterNavigationRequest.FromRoute(
                    new TestRoutes.StoreRoute("login"),
                    NavigationRequestSource.InAppCommand,
                    disposition: RouterNavigationDisposition.Canonical))),
            store);

        var result = await policy.ApplyAsync(Context(request));

        Assert.Equal("main", result.WindowId);
        await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();
        var buffered = Assert.Single(lease.Requests);
        Assert.Same(request, buffered);
        Assert.Equal("one", buffered.Metadata["request-id"]);
    }

    [Fact]
    public async Task ApplyAsync_RepeatedDeniedDuplicates_DoNotEnqueueDuplicates()
    {
        var request = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.AppLink);
        var store = new InMemoryDeferredNavigationRequestStore();
        var policy = new AccessGateNavigationPolicy(
            new StubEvaluator(_ => NavigationAccessDecision.DeferAndRedirect(
                RouterNavigationRequest.FromRoute(
                    new TestRoutes.StoreRoute("login"),
                    NavigationRequestSource.InAppCommand,
                    disposition: RouterNavigationDisposition.Canonical))),
            store);

        await policy.ApplyAsync(Context(request));
        await policy.ApplyAsync(Context(request));

        await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();
        Assert.Single(lease.Requests);
    }

    private static NavigationRequestPolicyContext Context(RouterNavigationRequest request)
    {
        return new NavigationRequestPolicyContext(
            request,
            request.Route ?? new TestRoutes.StoreRoute("fallback"),
            routeMetadata: null,
            NavigationState.Empty,
            "operation");
    }

    private sealed class StubEvaluator(Func<NavigationRequestPolicyContext, NavigationAccessDecision> evaluate) : INavigationAccessEvaluator
    {
        public ValueTask<NavigationAccessDecision> EvaluateAsync(
            NavigationRequestPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(evaluate(context));
        }
    }
}

using AdamE.AppNav.Leases;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class RouteEntryLeaseTests
{
    [Fact]
    public async Task CommitThatDropsTheEntryRetiresAndReleasesIt()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease one = leases.Open("one");
        var resource = new RecordingResource(host);
        one.Own(resource);

        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));

        Assert.True(one.IsCommitted);
        Assert.False(one.IsRetired);

        await router.NavigateAsync(new TestRoutes.StoreRoute("two"));

        Assert.True(one.RetiredToken.IsCancellationRequested);
        Assert.Equal(RouteEntryLeaseRetirement.Dropped, one.Retirement);
        await one.Released.WaitAsync(Timeout);
        Assert.True(resource.Disposed);
        Assert.True(resource.DisposedOnHostDispatcher);
        Assert.False(leases.TryGet("one", out _));
    }

    [Fact]
    public async Task ReleaseWaitsForEveryArtifactAndRecreationNeverReleasesACommittedEntry()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease one = leases.Open("one");
        var resource = new RecordingResource(host);
        one.Own(resource);
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));
        Assert.True(one.TryAttach(out RouteEntryLeaseAttachment? oldView));

        // Host recreation: the old view detaches and the refresh attaches a new one.
        oldView.Detach();
        await router.RefreshPresentationAsync();
        Assert.True(one.TryAttach(out RouteEntryLeaseAttachment? newView));
        Assert.False(resource.Disposed);

        await router.NavigateAsync(new TestRoutes.StoreRoute("two"));
        Assert.True(one.IsRetired);
        Assert.False(one.Released.IsCompleted);
        Assert.False(one.TryAttach(out _));

        newView.Detach();
        newView.Detach();
        await one.Released.WaitAsync(Timeout);
        Assert.True(resource.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledPresentationAbandonsOnlyTheEntriesItIntroduced(bool cancel)
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var presenter = new RecordingNavigationPresenter();
        await using IRouterNavigator router = CreateRouter(leases, presenter);
        RouteEntryLease one = leases.Open("one");
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));
        RouteEntryLease two = leases.Open("two");
        RouteEntryLease queued = leases.Open("three");
        RouteEntryLeaseAttachment? rolledBack = null;
        presenter.OnApplyAsync = (_, _, _) =>
        {
            Assert.True(two.TryAttach(out rolledBack));
            // The adapter's rollback detaches what it built before ApplyAsync returns.
            rolledBack.Detach();
            Assert.False(two.IsRetired);
            return cancel
                ? ValueTask.FromCanceled(new CancellationToken(true))
                : ValueTask.FromException(new InvalidOperationException("Presentation failed"));
        };

        await Assert.ThrowsAnyAsync<Exception>(() => router.NavigateAsync(new TestRoutes.StoreRoute("two")).AsTask());

        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, two.Retirement);
        await two.Released.WaitAsync(Timeout);
        Assert.False(one.IsRetired);
        Assert.False(queued.IsRetired);
        Assert.False(leases.TryGet("two", out _));

        // The ID is free for a new occurrence.
        presenter.OnApplyAsync = null;
        RouteEntryLease retry = leases.Open("two");
        await router.NavigateAsync(new TestRoutes.StoreRoute("two"));
        Assert.True(retry.IsCommitted);
    }

    [Fact]
    public async Task ApplicationRetirementKeepsACommittedEntryUntilANavigationRemovesIt()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var presenter = new RecordingNavigationPresenter();
        await using IRouterNavigator router = CreateRouter(leases, presenter);
        RouteEntryLease one = leases.Open("one");
        var resource = new RecordingResource(host);
        one.Own(resource);
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));
        Assert.True(one.TryAttach(out RouteEntryLeaseAttachment? view));

        one.Retire();

        Assert.True(one.RetiredToken.IsCancellationRequested);
        Assert.Equal(RouteEntryLeaseRetirement.Application, one.Retirement);
        await Task.Delay(50);
        Assert.False(resource.Disposed);

        // A refresh after recreation finds a retired, committed entry and presents a placeholder.
        view.Detach();
        bool? placeholder = null;
        presenter.OnApplyAsync = (plan, _, _) =>
        {
            Assert.Equal(NavigationPlanKind.Refresh, plan.Kind);
            Assert.True(leases.TryGet("one", out RouteEntryLease? lease));
            placeholder = lease.IsRetired && lease.IsCommitted;
            Assert.False(lease.TryAttach(out _));
            return ValueTask.CompletedTask;
        };
        await router.RefreshPresentationAsync();
        Assert.True(placeholder);

        // Its last view detached, so it was released while still committed; it stays current until dropped.
        await one.Released.WaitAsync(Timeout);
        Assert.True(resource.Disposed);
        Assert.Same(one, leases.GetOrOpen("one"));

        presenter.OnApplyAsync = null;
        await router.NavigateAsync(new TestRoutes.StoreRoute("two"));
        Assert.False(leases.TryGet("one", out _));
        Assert.Equal(RouteEntryLeaseRetirement.Application, one.Retirement);
    }

    [Fact]
    public async Task ParentRetiresItsChildrenAndIsReleasedAfterThemAndAfterTrackedWork()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease frame = leases.Open("one");
        RouteEntryLease answer = leases.Open("answer", frame);
        RouteEntryLease tab = frame.OpenChild();
        var order = new List<string>();
        frame.Own(new RecordingResource(host, () => order.Add("frame")));
        answer.Own(new RecordingResource(host, () => order.Add("answer")));
        tab.Own(new RecordingResource(host, () => order.Add("tab")));
        var preparation = new TaskCompletionSource();
        frame.Track(preparation.Task);
        Assert.True(tab.TryAttach(out RouteEntryLeaseAttachment? tabView));
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));

        await router.NavigateAsync(new TestRoutes.StoreRoute("two"));

        Assert.Equal(RouteEntryLeaseRetirement.Dropped, frame.Retirement);
        Assert.Equal(RouteEntryLeaseRetirement.Parent, answer.Retirement);
        Assert.Equal(RouteEntryLeaseRetirement.Parent, tab.Retirement);
        await answer.Released.WaitAsync(Timeout);
        Assert.False(frame.Released.IsCompleted);

        tabView.Detach();
        await tab.Released.WaitAsync(Timeout);
        Assert.False(frame.Released.IsCompleted);

        preparation.SetResult();
        await frame.Released.WaitAsync(Timeout);
        Assert.Equal("frame", order[^1]);
        Assert.Equal(3, order.Count);
        Assert.Null(tab.EntryId);
        Assert.Same(frame, tab.Parent);
    }

    [Fact]
    public async Task UnsafeReleaseKeepsResourcesAndReachesTheFatalBoundary()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease frame = leases.Open("one");
        RouteEntryLease child = frame.OpenChild();
        var frameResource = new RecordingResource(host);
        var first = new RecordingResource(host);
        frame.Own(frameResource);
        child.Own(first);
        child.Own(new RecordingResource(host, () => throw new InvalidOperationException("Dispose failed")));
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));

        await router.NavigateAsync(new TestRoutes.StoreRoute("two"));

        RouteEntryLeaseReleaseException failure = await host.Fatal.Task.WaitAsync(Timeout);
        Assert.Null(failure.EntryId);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.False(first.Disposed);
        Assert.False(frameResource.Disposed);
        Assert.False(child.Released.IsCompleted);
        Assert.False(frame.Released.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => child.Own(new RecordingResource(host)));
    }

    [Fact]
    public async Task RouterDisposalRetiresEveryLeaseAndCompletesOnlyAfterEveryRelease()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease one = leases.Open("one");
        RouteEntryLease pending = leases.Open("pending");
        var resource = new RecordingResource(host);
        one.Own(resource);
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));
        Assert.True(one.TryAttach(out RouteEntryLeaseAttachment? view));

        Task disposal = router.DisposeAsync().AsTask();

        Assert.Equal(RouteEntryLeaseRetirement.Shutdown, one.Retirement);
        Assert.Equal(RouteEntryLeaseRetirement.Shutdown, pending.Retirement);
        await pending.Released.WaitAsync(Timeout);
        await Task.Delay(50);
        Assert.False(disposal.IsCompleted);

        // The adapter's own shutdown detaches its artifacts.
        view.Detach();
        await disposal.WaitAsync(Timeout);
        Assert.True(resource.Disposed);
        Assert.Throws<ObjectDisposedException>(() => leases.Open("late"));
    }

    [Fact]
    public async Task RouterDisposalCompletesAfterAnUnsafeReleaseIsReported()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease one = leases.Open("one");
        one.OpenChild().Own(new RecordingResource(host, () => throw new InvalidOperationException("Dispose failed")));
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));

        await router.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.True(host.Fatal.Task.IsCompleted);
        Assert.False(one.Released.IsCompleted);
    }

    [Fact]
    public async Task LeaseOpenedForACommittedEntryIsCommittedFromBirthAndGetOrOpenReusesTheCurrentLease()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));

        RouteEntryLease lease = leases.GetOrOpen("one");

        Assert.True(lease.IsCommitted);
        Assert.Same(lease, leases.GetOrOpen("one"));
        Assert.Throws<InvalidOperationException>(() => leases.Open("one"));
        Assert.Throws<InvalidOperationException>(() => CreateRouter(leases, new RecordingNavigationPresenter()));
    }

    [Fact]
    public async Task ChildOpenedUnderARetiredParentIsBornRetired()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease parent = leases.Open("one");
        Assert.True(parent.TryAttach(out RouteEntryLeaseAttachment? view));
        parent.Retire();

        RouteEntryLease child = parent.OpenChild();

        Assert.Equal(RouteEntryLeaseRetirement.Parent, child.Retirement);
        await child.Released.WaitAsync(Timeout);
        view.Detach();
        await parent.Released.WaitAsync(Timeout);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static IRouterNavigator CreateRouter(RouteEntryLeaseRegistry leases, RecordingNavigationPresenter presenter)
    {
        // Each store is one entry whose ID is the store ID, so navigating to another store drops the previous one.
        var planner = new TestNavigationPlanner((context, _) =>
        {
            var route = (TestRoutes.StoreRoute)context.Route;
            NavigationState state = TestNavigationState.State(
                "main",
                TestNavigationState.Window(
                    "main",
                    TestNavigationState.Stack("stack", TestNavigationState.Entry(route.StoreId, route))));
            return ValueTask.FromResult(new NavigationPlan(state));
        });
        return RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            planner,
            presenter,
            new RouterNavigatorFactoryOptions { EntryLeases = leases });
    }

    private sealed class RecordingLeaseHost : IRouteEntryLeaseHost
    {
        private readonly AsyncLocal<bool> _onDispatcher = new();

        public TaskCompletionSource<RouteEntryLeaseReleaseException> Fatal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsOnDispatcher => _onDispatcher.Value;

        public async Task InvokeAsync(Func<ValueTask> release)
        {
            await Task.Yield();
            _onDispatcher.Value = true;
            await release();
        }

        public void ReportFatal(RouteEntryLeaseReleaseException failure) => Fatal.TrySetResult(failure);
    }

    private sealed class RecordingResource(RecordingLeaseHost host, Action? onDispose = null) : IDisposable
    {
        public bool Disposed { get; private set; }

        public bool DisposedOnHostDispatcher { get; private set; }

        public void Dispose()
        {
            onDispose?.Invoke();
            DisposedOnHostDispatcher = host.IsOnDispatcher;
            Disposed = true;
        }
    }
}

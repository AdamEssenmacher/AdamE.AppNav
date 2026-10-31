using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Leases;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class RouteEntryLeaseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [ThreadStatic]
    private static bool t_retiring;

    [Fact]
    public async Task CommitThatDropsTheEntryRetiresAndReleasesItOnTheHostDispatcher()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease one = leases.Open("one");
        var resource = new RecordingResource(host);
        one.Own(resource.Dispose);
        await router.NavigateAsync(Request("one"), one);
        one.Close();

        Assert.True(one.IsCommitted);
        Assert.False(one.IsRetired);

        await router.NavigateAsync(new TestRoutes.StoreRoute("two"));

        Assert.True(one.IsRetired);
        Assert.Equal(RouteEntryLeaseRetirement.Dropped, one.Retirement);
        Assert.False(one.IsCommitted);
        await one.Released.WaitAsync(Timeout);
        Assert.True(one.RetiredToken.IsCancellationRequested);
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
        var resource = new RecordingResource(host);
        RouteEntryLease one = await OpenSentClosedAsync(router, leases, "one", opened => opened.Own(resource.Dispose));
        Assert.True(one.TryAttach(out RouteEntryLeaseAttachment? oldView));

        // Host recreation: the old view detaches and the refresh attaches a new one.
        oldView.Detach();
        await router.RefreshPresentationAsync();
        Assert.True(one.TryAttach(out RouteEntryLeaseAttachment? newView));
        await AssertHeldAsync(one);

        await router.NavigateAsync(new TestRoutes.StoreRoute("two"));
        Assert.True(one.IsRetired);
        Assert.False(one.TryAttach(out _));
        await AssertHeldAsync(one);
        Assert.False(resource.Disposed);

        newView.Detach();
        newView.Detach();
        await one.Released.WaitAsync(Timeout);
        Assert.True(resource.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledPresentationAbandonsItsLeaseAfterTheAdaptersRollback(bool cancel)
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var presenter = new RecordingNavigationPresenter();
        await using IRouterNavigator router = CreateRouter(leases, presenter);
        RouteEntryLease one = await OpenSentClosedAsync(router, leases, "one");
        RouteEntryLease two = leases.Open("two");
        RouteEntryLease unsent = leases.Open("three");
        presenter.OnApplyAsync = (_, _, _) =>
        {
            Assert.True(two.TryAttach(out RouteEntryLeaseAttachment? rolledBack));
            // The adapter's rollback detaches what it built before ApplyAsync returns.
            rolledBack.Detach();
            Assert.False(two.IsRetired);
            return cancel
                ? ValueTask.FromCanceled(new CancellationToken(true))
                : ValueTask.FromException(new InvalidOperationException("Presentation failed"));
        };

        await Assert.ThrowsAnyAsync<Exception>(() => router.NavigateAsync(Request("two"), two).AsTask());
        // Release also waits for its opener to close it.
        await AssertHeldAsync(two);
        two.Close();

        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, two.Retirement);
        await two.Released.WaitAsync(Timeout);
        Assert.False(one.IsRetired);
        Assert.False(unsent.IsRetired);
        Assert.False(leases.TryGet("two", out _));

        // The ID is free for a new occurrence.
        presenter.OnApplyAsync = null;
        RouteEntryLease retry = leases.Open("two");
        await router.NavigateAsync(Request("two"), retry);
        Assert.True(retry.IsCommitted);
        foreach (RouteEntryLease lease in new[] { unsent, retry })
            lease.Close();
    }

    [Fact]
    public async Task RequestThatEndsWithoutCommittingTheEntryAbandonsItsLease()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        var rejectTwo = new RejectingPolicy("two");
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter(), rejectTwo);

        // A policy rejection never reaches presentation.
        RouteEntryLease rejected = leases.Open("two");
        await Assert.ThrowsAnyAsync<Exception>(() => router.NavigateAsync(Request("two"), rejected).AsTask());
        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, rejected.Retirement);

        // A request that commits a state without the entry abandons it too.
        RouteEntryLease elsewhere = leases.Open("four");
        await router.NavigateAsync(Request("one"), elsewhere);
        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, elsewhere.Retirement);

        // A request cancelled while queued.
        RouteEntryLease queued = leases.Open("five");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            router.NavigateAsync(Request("five"), queued, new CancellationToken(true)).AsTask());
        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, queued.Retirement);

        // A lease its opener closes without ever sending.
        RouteEntryLease neverSent = leases.Open("six");
        neverSent.Close();
        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, neverSent.Retirement);
        await neverSent.Released.WaitAsync(Timeout);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.NavigateAsync(Request("two"), rejected).AsTask());
        foreach (RouteEntryLease lease in new[] { rejected, elsewhere, queued })
        {
            lease.Close();
            await lease.Released.WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task ResourcesMayBeAttachedAfterRetirementAndReleaseWaitsForClose()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease root = leases.Open("one");
        await router.NavigateAsync(Request("one"), root);

        root.Retire();
        await AssertHeldAsync(root);

        // The root's view model is handed over only at retirement.
        var viewModel = new RecordingResource(host);
        root.Own(viewModel.Dispose);
        root.Close();

        await root.Released.WaitAsync(Timeout);
        Assert.True(viewModel.Disposed);
        Assert.Throws<InvalidOperationException>(() => root.Own(new RecordingResource(host).Dispose));
    }

    [Fact]
    public async Task RetirementIsNeverDeliveredInlineAndItsWorkIsAwaitedBeforeRelease()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        var callbackInline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerRequest = new TaskCompletionSource();
        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool workInline = true;
        bool workOnDispatcher = false;
        RouteEntryLease lease = await OpenSentClosedAsync(router, leases, "one", opened =>
        {
            opened.RetiredToken.Register(() => callbackInline.TrySetResult(t_retiring));
            opened.OnRetired(async () =>
            {
                workInline = t_retiring;
                workOnDispatcher = host.IsOnDispatcher;
                workStarted.SetResult();
                await providerRequest.Task;
            });
        });

        t_retiring = true;
        try
        {
            lease.Retire();
        }
        finally
        {
            t_retiring = false;
        }

        Assert.False(await callbackInline.Task.WaitAsync(Timeout));
        await workStarted.Task.WaitAsync(Timeout);
        Assert.False(workInline);
        Assert.True(workOnDispatcher);
        Assert.False(lease.IsReleaseStarted);

        providerRequest.SetResult();
        await lease.Released.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ApplicationRetirementKeepsACommittedEntryCurrentForPlaceholdersUntilANavigationRemovesIt()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var presenter = new RecordingNavigationPresenter();
        await using IRouterNavigator router = CreateRouter(leases, presenter);
        var resource = new RecordingResource(host);
        RouteEntryLease one = await OpenSentClosedAsync(router, leases, "one", opened => opened.Own(resource.Dispose));
        Assert.True(one.TryAttach(out RouteEntryLeaseAttachment? view));

        one.Retire();

        Assert.True(one.IsRetired);
        Assert.Equal(RouteEntryLeaseRetirement.Application, one.Retirement);
        await AssertHeldAsync(one);
        Assert.False(resource.Disposed);

        // Recreation: the old view detaches and the refresh finds a retired, committed entry.
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

        await one.Released.WaitAsync(Timeout);
        Assert.True(resource.Disposed);
        Assert.Same(one, leases.GetOrOpen("one", out bool opened));
        Assert.False(opened);

        presenter.OnApplyAsync = null;
        await router.NavigateAsync(new TestRoutes.StoreRoute("two"));
        Assert.False(leases.TryGet("one", out _));
    }

    [Fact]
    public async Task ApplicationLeaseParentsEntryLeasesAndIsReleasedAfterThemAndAfterTrackedWork()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease selection = leases.OpenApplicationLease();
        RouteEntryLease frame = leases.Open("one", selection);
        RouteEntryLease answer = leases.Open("answer", frame);
        RouteEntryLease tab = leases.OpenApplicationLease(frame);
        var order = new List<string>();
        selection.Own(new RecordingResource(host, () => order.Add("selection")).Dispose);
        frame.Own(new RecordingResource(host, () => order.Add("frame")).Dispose);
        answer.Own(new RecordingResource(host, () => order.Add("answer")).Dispose);
        tab.Own(new RecordingResource(host, () => order.Add("tab")).Dispose);
        var preparation = new TaskCompletionSource();
        frame.Track(preparation.Task);
        Assert.True(tab.TryAttach(out RouteEntryLeaseAttachment? tabView));
        foreach (RouteEntryLease lease in new[] { tab, selection })
            lease.Close();
        await router.NavigateAsync(Request("one"), frame);
        frame.Close();

        selection.Retire();
        answer.Close();

        Assert.Equal(RouteEntryLeaseRetirement.Parent, frame.Retirement);
        Assert.Equal(RouteEntryLeaseRetirement.Parent, answer.Retirement);
        Assert.Equal(RouteEntryLeaseRetirement.Parent, tab.Retirement);
        await answer.Released.WaitAsync(Timeout);
        Assert.False(frame.Released.IsCompleted);

        tabView.Detach();
        await tab.Released.WaitAsync(Timeout);
        await AssertHeldAsync(frame);

        preparation.SetResult();
        await selection.Released.WaitAsync(Timeout);
        Assert.Equal(["answer", "tab", "frame", "selection"], order);
        Assert.Null(selection.EntryId);
    }

    [Fact]
    public async Task ThrowingDisposerIsASurvivedDefectAndReleaseContinues()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease lease = leases.OpenApplicationLease();
        var first = new RecordingResource(host);
        lease.Own(first.Dispose);
        lease.Own(new RecordingResource(host, () => throw new InvalidOperationException("Dispose failed")).Dispose);
        lease.Close();

        lease.Retire();

        await lease.Released.WaitAsync(Timeout);
        Assert.True(first.Disposed);
        RouteEntryLeaseReleaseException defect = Assert.Single(host.Defects);
        Assert.IsType<InvalidOperationException>(defect.InnerException);
        Assert.False(host.Fatal.Task.IsCompleted);
    }

    [Fact]
    public async Task ChildWhoseReleaseCannotCompleteKeepsItsParentsResourcesAndIsFatal()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease frame = leases.OpenApplicationLease();
        RouteEntryLease child = leases.OpenApplicationLease(frame);
        var frameResource = new RecordingResource(host);
        frame.Own(frameResource.Dispose);
        child.Own(new RecordingResource(host).Dispose);
        frame.Close();
        child.Close();
        host.FailInvocations = true;

        frame.Retire();

        RouteEntryLeaseReleaseException failure = await host.Fatal.Task.WaitAsync(Timeout);
        Assert.Null(failure.EntryId);
        await AssertHeldAsync(frame);
        Assert.False(frameResource.Disposed);
        Assert.False(child.Released.IsCompleted);
        Assert.False(frame.Released.IsCompleted);

        // Router disposal throws the failure once everything else has settled.
        await Assert.ThrowsAsync<RouteEntryLeaseReleaseException>(
            () => router.DisposeAsync().AsTask().WaitAsync(Timeout));
    }

    [Fact]
    public async Task RouterDisposalRetiresEveryLeaseAndReturnsAfterDetachmentCloseAndRelease()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease root = leases.Open("one");
        RouteEntryLease pending = leases.Open("pending");
        pending.Close();
        await router.NavigateAsync(Request("one"), root);
        Assert.True(root.TryAttach(out RouteEntryLeaseAttachment? view));

        Task disposal = router.DisposeAsync().AsTask();

        // 1. Every lease is retired and the router stops accepting work.
        Assert.Equal(RouteEntryLeaseRetirement.Shutdown, root.Retirement);
        Assert.Throws<ObjectDisposedException>(() => leases.Open("late"));
        await AssertHeldAsync(root);
        Assert.False(disposal.IsCompleted);

        // 2. The adapter detaches its artifacts.
        view.Detach();
        await AssertHeldAsync(root);
        Assert.False(disposal.IsCompleted);

        // 3. The application hands over the root's view model and closes its lease.
        var viewModel = new RecordingResource(host);
        root.Own(viewModel.Dispose);
        root.Close();

        // 4–5. Every release finishes, then disposal returns.
        await disposal.WaitAsync(Timeout);
        Assert.True(viewModel.Disposed);
        Assert.True(pending.Released.IsCompleted);
    }

    [Fact]
    public async Task AdapterOpensALeaseInsidePresentationThatACommitOwnsAndAFailureAbandons()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        var presenter = new RecordingNavigationPresenter();
        await using IRouterNavigator router = CreateRouter(leases, presenter);
        RouteEntryLease? opened = null;
        presenter.OnApplyAsync = (plan, _, _) =>
        {
            if (plan.Kind == NavigationPlanKind.Refresh)
                return ValueTask.CompletedTask;
            RouteEntryLease lease = leases.GetOrOpen("one", out bool isNew);
            Assert.True(isNew);
            lease.Close();
            opened = lease;
            return ValueTask.FromException(new InvalidOperationException("Presentation failed"));
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.NavigateAsync(new TestRoutes.StoreRoute("one")).AsTask());

        Assert.NotNull(opened);
        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, opened.Retirement);
        await opened.Released.WaitAsync(Timeout);

        presenter.OnApplyAsync = null;
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));
        // Outside a presentation, GetOrOpen opens nothing: no operation would close what it opened.
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => leases.GetOrOpen("one", out _));
        Assert.Contains("only while the router is presenting", refusal.Message, StringComparison.Ordinal);
        RouteEntryLease committed = leases.Open("one");
        Assert.True(committed.IsCommitted);
        committed.Close();
        Assert.False(committed.IsRetired);
        Assert.Throws<InvalidOperationException>(() => leases.Open("one"));
        Assert.Throws<InvalidOperationException>(() => CreateRouter(leases, new RecordingNavigationPresenter()));
    }

    [Fact]
    public async Task LeaseThatIsNoLongerOpenAndCurrentCannotBeSent()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease closedFirst = leases.Open("one");
        closedFirst.Close();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.NavigateAsync(Request("one"), closedFirst).AsTask());

        Assert.Same(NavigationState.Empty, router.CurrentState);
        RouteEntryLease retired = leases.Open("two");
        retired.Retire();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.NavigateAsync(Request("two"), retired).AsTask());
        retired.Close();
    }

    [Fact]
    public async Task LeaseRetiredWhileItsRequestIsInFlightStaysCurrentAndCommitsAsAPlaceholder()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        var presenter = new RecordingNavigationPresenter();
        await using IRouterNavigator router = CreateRouter(leases, presenter);
        RouteEntryLease lease = leases.Open("one");
        bool? currentDuringPresentation = null;
        presenter.OnApplyAsync = async (_, _, _) =>
        {
            lease.Retire();
            lease.Close();
            await lease.Released.WaitAsync(Timeout);
            currentDuringPresentation = leases.TryGet("one", out RouteEntryLease? found) && ReferenceEquals(found, lease);
        };

        await router.NavigateAsync(Request("one"), lease);

        Assert.True(currentDuringPresentation);
        Assert.True(leases.TryGet("one", out RouteEntryLease? committed));
        Assert.Same(lease, committed);
        Assert.True(committed.IsRetired && committed.IsCommitted);
    }

    [Fact]
    public async Task ReconciliationCommitThatDropsTheEntryRetiresIt()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease lease = await OpenSentClosedAsync(router, leases, "one");

        await router.ReconcileAsync(new NavigationReconciliation(
            router.CurrentState,
            NavigationState.Empty,
            NavigationReconciliationSource.HostBack));

        Assert.Equal(RouteEntryLeaseRetirement.Dropped, lease.Retirement);
        await lease.Released.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ShutdownRetiresAnAncestorThatAFaultKeeps()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease selection = leases.OpenApplicationLease();
        RouteEntryLease child = leases.OpenApplicationLease(selection);
        selection.Close();
        child.Close();
        host.FailInvocations = true;
        child.Retire();
        await host.Fatal.Task.WaitAsync(Timeout);
        Assert.False(selection.IsRetired);

        await Assert.ThrowsAsync<RouteEntryLeaseReleaseException>(
            () => router.DisposeAsync().AsTask().WaitAsync(Timeout));

        Assert.Equal(RouteEntryLeaseRetirement.Shutdown, selection.Retirement);
        Assert.True(selection.RetiredToken.IsCancellationRequested);
        Assert.False(selection.Released.IsCompleted);
    }

    [Fact]
    public async Task PlanThatRepeatsAnEntryIdIsRejectedBeforePresentation()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        var presenter = new RecordingNavigationPresenter();
        var route = new TestRoutes.StoreRoute("settings");
        NavigationState twoTabs = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.BranchHost(
                    "tabs",
                    "a",
                    TestNavigationState.Branch("a", "A",
                        TestNavigationState.Stack("a-stack", TestNavigationState.Entry("settings", route))),
                    TestNavigationState.Branch("b", "B",
                        TestNavigationState.Stack("b-stack", TestNavigationState.Entry("settings", route))))));
        await using IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.ForState(twoTabs),
            presenter,
            new RouterNavigatorFactoryOptions { EntryLeases = leases });

        AppNavigationConfigurationException failure = await Assert.ThrowsAsync<AppNavigationConfigurationException>(
            () => router.NavigateAsync(route).AsTask());

        Assert.Contains("'settings'", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, presenter.ApplyCount);
        Assert.Same(NavigationState.Empty, router.CurrentState);

        // Without leases, the same plan is still accepted.
        await using IRouterNavigator unleased = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.ForState(twoTabs),
            new RecordingNavigationPresenter());
        await unleased.NavigateAsync(route);
        Assert.Same(twoTabs, unleased.CurrentState);
    }

    [Fact]
    public async Task FatalDisposalKeepsTheRestOfTheLeaseAndEveryAncestor()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease selection = leases.OpenApplicationLease();
        RouteEntryLease frame = leases.OpenApplicationLease(selection);
        var selectionResource = new RecordingResource(host);
        var earlier = new RecordingResource(host);
        selection.Own(selectionResource.Dispose);
        frame.Own(earlier.Dispose);
        frame.Own(
            new RecordingResource(host, () => throw new InvalidOperationException("Frame disposal failed")).Dispose,
            RouteEntryLeaseDisposalFailure.Fatal);
        frame.Close();
        selection.Close();

        selection.Retire();

        RouteEntryLeaseReleaseException failure = await host.Fatal.Task.WaitAsync(Timeout);
        Assert.Equal("Frame disposal failed", failure.InnerException?.Message);
        await AssertHeldAsync(selection);
        Assert.False(earlier.Disposed);
        Assert.False(selectionResource.Disposed);
        Assert.Empty(host.Defects);

        // Shutdown step 5: a failed release makes router disposal throw, so its owner disposes nothing after it.
        RouteEntryLeaseReleaseException disposalFailure = await Assert.ThrowsAsync<RouteEntryLeaseReleaseException>(
            () => router.DisposeAsync().AsTask().WaitAsync(Timeout));
        Assert.Same(failure, disposalFailure);
        Assert.False(selectionResource.Disposed);
    }

    [Fact]
    public async Task LeaseIsRefusedByARouterThatDoesNotUseItsRegistry()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        await using IRouterNavigator leased = CreateRouter(leases, new RecordingNavigationPresenter());
        var otherLeases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        await using IRouterNavigator other = CreateRouter(otherLeases, new RecordingNavigationPresenter());
        await using IRouterNavigator unleased = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            new TestNavigationPlanner((context, _) => ValueTask.FromResult(new NavigationPlan(
                TestNavigationState.State("main", TestNavigationState.Window("main",
                    TestNavigationState.Stack("stack", TestNavigationState.Entry("one", context.Route))))))),
            new RecordingNavigationPresenter());

        RouteEntryLease first = leases.Open("one");
        await Assert.ThrowsAsync<InvalidOperationException>(() => unleased.NavigateAsync(Request("one"), first).AsTask());
        RouteEntryLease second = leases.Open("one");
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.NavigateAsync(Request("one"), second).AsTask());

        Assert.Same(NavigationState.Empty, unleased.CurrentState);
        Assert.Same(NavigationState.Empty, other.CurrentState);
        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, first.Retirement);
        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, second.Retirement);
        first.Close();
        second.Close();
    }

    [Fact]
    public async Task LeaseAnAdapterOpensForAnEntryOutsideTheStateEndsWithItsOperation()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        var presenter = new RecordingNavigationPresenter();
        await using IRouterNavigator router = CreateRouter(leases, presenter);
        var strays = new List<RouteEntryLease>();
        presenter.OnApplyAsync = (_, _, _) =>
        {
            // A presenter that uses GetOrOpen as a lookup for an entry the plan does not contain, and never closes it.
            RouteEntryLease stray = leases.GetOrOpen("elsewhere", out bool opened);
            Assert.True(opened);
            strays.Add(stray);
            return ValueTask.CompletedTask;
        };

        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));
        await router.RefreshPresentationAsync();

        Assert.Equal(2, strays.Count);
        Assert.All(strays, static stray => Assert.Equal(RouteEntryLeaseRetirement.Abandoned, stray.Retirement));
        await Task.WhenAll(strays.Select(static stray => stray.Released)).WaitAsync(Timeout);
        Assert.False(leases.TryGet("elsewhere", out _));
        leases.Open("elsewhere").Close();
    }

    [Fact]
    public async Task LeaseAnAdapterOpensForACommittedEntryIsClosedWhenItsOperationEnds()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        var presenter = new RecordingNavigationPresenter();
        await using IRouterNavigator router = CreateRouter(leases, presenter);
        RouteEntryLease? lookedUp = null;
        presenter.OnApplyAsync = (_, _, _) =>
        {
            // A presenter that uses GetOrOpen as a lookup for the entry it presents, and never closes it.
            presenter.OnApplyAsync = null;
            lookedUp = leases.GetOrOpen("one", out bool opened);
            Assert.True(opened);
            return ValueTask.CompletedTask;
        };

        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));

        Assert.NotNull(lookedUp);
        Assert.True(lookedUp.IsCommitted);
        Assert.Throws<InvalidOperationException>(() => lookedUp.Own(() => { }));

        await router.NavigateAsync(new TestRoutes.StoreRoute("two"));

        await lookedUp.Released.WaitAsync(Timeout);
        Assert.Equal(RouteEntryLeaseRetirement.Dropped, lookedUp.Retirement);
    }

    [Fact]
    public async Task ShutdownStillWaitingAfterTheDelayNamesEachLeaseAndWhatHoldsIt()
    {
        var warning = new TaskCompletionSource<NavigationDiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new NavigationDiagnostics(
            options: new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full });
        diagnostics.AddObserver(new MessageObserver(NavigationDiagnosticEventKind.EntryLeaseShutdownWaiting, warning));
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            new TestNavigationPlanner((_, _) => ValueTask.FromResult(new NavigationPlan(NavigationState.Empty))),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions
            {
                EntryLeases = leases,
                Diagnostics = diagnostics,
                EntryLeaseShutdownWarningDelay = TimeSpan.FromMilliseconds(20)
            });
        RouteEntryLease unclosed = leases.Open("one");

        Task disposal = router.DisposeAsync().AsTask();
        NavigationDiagnosticEvent diagnostic = await warning.Task.WaitAsync(Timeout);

        Assert.Contains("lease of route entry 'one' (not closed by its opener)", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1, diagnostic.Data[NavigationDiagnosticDataKeys.Count]);
        Assert.Equal(
            "not closed: 1; native artifacts not detached: 0; pending work: 0; child leases not released: 0",
            diagnostic.Data[NavigationDiagnosticDataKeys.Reason]);
        Assert.False(disposal.IsCompleted);
        unclosed.Close();
        await disposal.WaitAsync(Timeout);
    }

    [Fact]
    public async Task SafeModeShutdownWarningKeepsTheCountsButNotTheEntryIds()
    {
        var warning = new TaskCompletionSource<NavigationDiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new NavigationDiagnostics();
        diagnostics.AddObserver(new MessageObserver(NavigationDiagnosticEventKind.EntryLeaseShutdownWaiting, warning));
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            new TestNavigationPlanner((_, _) => ValueTask.FromResult(new NavigationPlan(NavigationState.Empty))),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions
            {
                EntryLeases = leases,
                Diagnostics = diagnostics,
                EntryLeaseShutdownWarningDelay = TimeSpan.FromMilliseconds(20)
            });
        RouteEntryLease unclosed = leases.Open("private-entry");

        Task disposal = router.DisposeAsync().AsTask();
        NavigationDiagnosticEvent diagnostic = await warning.Task.WaitAsync(Timeout);

        Assert.DoesNotContain("private-entry", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(1, diagnostic.Data[NavigationDiagnosticDataKeys.Count]);
        Assert.StartsWith("not closed: 1;", (string?)diagnostic.Data[NavigationDiagnosticDataKeys.Reason]);
        unclosed.Close();
        await disposal.WaitAsync(Timeout);
    }

    [Fact]
    public async Task HostThatCannotRunRetirementWorkIsFatalAndKeepsTheResources()
    {
        var host = new RecordingLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease lease = leases.Open("one");
        var resourceDisposed = false;
        var workRan = false;
        lease.Own(() => resourceDisposed = true);
        lease.OnRetired(() =>
        {
            workRan = true;
            return ValueTask.CompletedTask;
        });
        lease.Close();
        host.FailInvocations = true;

        lease.Retire();
        RouteEntryLeaseReleaseException fatal = await host.Fatal.Task.WaitAsync(Timeout);

        Assert.Equal("one", fatal.EntryId);
        Assert.Contains("retirement work", fatal.Message, StringComparison.Ordinal);
        Assert.False(workRan);
        Assert.False(resourceDisposed);
        Assert.False(lease.Released.IsCompleted);
        await Assert.ThrowsAnyAsync<Exception>(() => router.DisposeAsync().AsTask().WaitAsync(Timeout));
        Assert.False(resourceDisposed);
    }

    [Fact]
    public void ShutdownWarningDelayMustBePositiveOrInfinite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            new TestNavigationPlanner((_, _) => ValueTask.FromResult(new NavigationPlan(NavigationState.Empty))),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions { EntryLeaseShutdownWarningDelay = TimeSpan.Zero }));
    }

    [Fact]
    public void RegistryThatServesNoRouterRefusesAdapterLeases()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => leases.GetOrOpen("one", out _));

        Assert.Contains("EntryLeases", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeaseClosedBeforeItWasSentIsRefusedWithTheReason()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter());
        RouteEntryLease lease = leases.Open("one");
        lease.Close();

        InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => router.NavigateAsync(Request("one"), lease).AsTask());

        Assert.Contains("closed before it was sent", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, lease.Retirement);
    }

    [Fact]
    public async Task OneRequestInstanceCannotCarryTwoLeasesAtOnce()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        var presenter = new RecordingNavigationPresenter();
        await using IRouterNavigator router = CreateRouter(leases, presenter);
        var presenting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.OnApplyAsync = async (_, _, _) =>
        {
            presenter.OnApplyAsync = null;
            presenting.SetResult();
            await proceed.Task;
        };
        RouterNavigationRequest request = Request("one");
        RouteEntryLease first = leases.Open("one");
        Task<NavigationResult> sending = router.NavigateAsync(request, first).AsTask();
        await presenting.Task.WaitAsync(Timeout);
        var foreign = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        RouteEntryLease second = foreign.Open("one");

        await Assert.ThrowsAsync<InvalidOperationException>(() => router.NavigateAsync(request, second).AsTask());

        // The refused send left the second lease unsent, so closing it abandons it.
        second.Close();
        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, second.Retirement);
        proceed.SetResult();
        await sending.WaitAsync(Timeout);
        Assert.True(first.IsCommitted);
        first.Close();
    }

    [Fact]
    public async Task RequestWhosePlanOmitsItsLeasesEntryIsDiagnosed()
    {
        var events = new List<NavigationDiagnosticEventKind>();
        var diagnostics = new NavigationDiagnostics();
        diagnostics.AddObserver(new RecordingObserver(events));
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        await using IRouterNavigator router = CreateRouter(leases, new RecordingNavigationPresenter(), diagnostics: diagnostics);
        RouteEntryLease lease = leases.Open("four");

        await router.NavigateAsync(Request("one"), lease);

        Assert.Contains(NavigationDiagnosticEventKind.EntryLeaseNotInPlan, events);
        Assert.Equal(RouteEntryLeaseRetirement.Abandoned, lease.Retirement);
        lease.Close();
    }

    [Fact]
    public async Task ShutdownWhileALeasedRequestIsQueuedRetiresItAndWaitsForItsClose()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        var presenter = new RecordingNavigationPresenter();
        IRouterNavigator router = CreateRouter(leases, presenter);
        var presenting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.OnApplyAsync = async (_, _, token) =>
        {
            presenting.TrySetResult();
            await Task.Delay(Timeout, token);
        };
        Task first = router.NavigateAsync(new TestRoutes.StoreRoute("zero")).AsTask();
        await presenting.Task.WaitAsync(Timeout);
        RouteEntryLease queued = leases.Open("one");
        Task<NavigationResult> waiting = router.NavigateAsync(Request("one"), queued).AsTask();

        Task disposal = router.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<Exception>(() => waiting);
        await Assert.ThrowsAnyAsync<Exception>(() => first);
        Assert.True(queued.IsRetired);
        Assert.False(queued.IsCommitted);
        await AssertHeldAsync(queued);
        Assert.False(disposal.IsCompleted);

        queued.Close();
        await disposal.WaitAsync(Timeout);
        Assert.True(queued.Released.IsCompleted);
    }

    [Fact]
    public async Task AnotherRequestsFailureNeverAbandonsALeaseWhoseOwnRequestIsQueued()
    {
        var leases = new RouteEntryLeaseRegistry(new RecordingLeaseHost());
        var presenter = new RecordingNavigationPresenter();
        await using IRouterNavigator router = CreateRouter(leases, presenter);
        var presenting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.OnApplyAsync = async (_, _, _) =>
        {
            presenter.OnApplyAsync = null;
            presenting.SetResult();
            await fail.Task;
            throw new InvalidOperationException("Presentation failed");
        };

        // A request without a lease (a deep link, say) for entry "one" is presenting.
        Task unleased = router.NavigateAsync(Request("one")).AsTask();
        await presenting.Task.WaitAsync(Timeout);
        // The application opens a lease for "one" and its own request queues behind it.
        RouteEntryLease lease = leases.Open("one");
        Task<NavigationResult> leased = router.NavigateAsync(Request("one"), lease).AsTask();
        fail.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unleased);

        await leased.WaitAsync(Timeout);

        Assert.False(lease.IsRetired);
        Assert.True(lease.IsCommitted);
        Assert.True(leases.TryGet("one", out RouteEntryLease? current));
        Assert.Same(lease, current);
        lease.Close();
    }

    [Fact]
    public async Task ReconciliationAndInitialStateThatRepeatAnEntryIdAreRejected()
    {
        var route = new TestRoutes.StoreRoute("settings");
        NavigationState twoTabs = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.BranchHost(
                    "tabs",
                    "a",
                    TestNavigationState.Branch("a", "A",
                        TestNavigationState.Stack("a-stack", TestNavigationState.Entry("settings", route))),
                    TestNavigationState.Branch("b", "B",
                        TestNavigationState.Stack("b-stack", TestNavigationState.Entry("settings", route))))));

        Assert.Throws<AppNavigationConfigurationException>(() => RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.ForState(twoTabs),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions
            {
                InitialState = twoTabs,
                EntryLeases = new RouteEntryLeaseRegistry(new RecordingLeaseHost())
            }));

        var presenter = new RecordingNavigationPresenter();
        await using IRouterNavigator router = CreateRouter(new RouteEntryLeaseRegistry(new RecordingLeaseHost()), presenter);
        await Assert.ThrowsAsync<AppNavigationConfigurationException>(() => router.ReconcileAsync(
            new NavigationReconciliation(router.CurrentState, twoTabs, NavigationReconciliationSource.BranchChanged))
            .AsTask());
        Assert.Equal(0, presenter.ApplyCount);
    }

    // Deterministic: once the lease's retirement has been delivered, release starts synchronously with whichever call
    // meets its last condition, so a lease that has not started releasing by then is held.
    private static async Task AssertHeldAsync(RouteEntryLease lease)
    {
        if (lease.IsRetired)
            await lease.RetirementNotified.WaitAsync(Timeout);
        Assert.False(lease.IsReleaseStarted);
        Assert.False(lease.Released.IsCompleted);
    }

    private static async Task<RouteEntryLease> OpenSentClosedAsync(
        IRouterNavigator router,
        RouteEntryLeaseRegistry leases,
        string entryId,
        Action<RouteEntryLease>? attach = null)
    {
        RouteEntryLease lease = leases.Open(entryId);
        attach?.Invoke(lease);
        await router.NavigateAsync(Request(entryId), lease);
        lease.Close();
        return lease;
    }

    private static RouterNavigationRequest Request(string storeId)
    {
        return RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute(storeId),
            NavigationRequestSource.InAppCommand);
    }

    private static IRouterNavigator CreateRouter(
        RouteEntryLeaseRegistry leases,
        RecordingNavigationPresenter presenter,
        INavigationRequestPolicy? policy = null,
        NavigationDiagnostics? diagnostics = null)
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
            new RouterNavigatorFactoryOptions
            {
                EntryLeases = leases,
                RequestPolicies = policy is null ? [] : [policy],
                Diagnostics = diagnostics
            });
    }

    private sealed class MessageObserver(NavigationDiagnosticEventKind kind, TaskCompletionSource<NavigationDiagnosticEvent> seen)
        : INavigationDiagnosticObserver
    {
        public void OnNavigationDiagnosticEvent(NavigationDiagnosticEvent diagnostic)
        {
            if (diagnostic.Kind == kind)
                seen.TrySetResult(diagnostic);
        }
    }

    private sealed class RecordingObserver(List<NavigationDiagnosticEventKind> events) : INavigationDiagnosticObserver
    {
        public void OnNavigationDiagnosticEvent(NavigationDiagnosticEvent diagnostic)
        {
            lock (events)
            {
                events.Add(diagnostic.Kind);
            }
        }
    }

    private sealed class RejectingPolicy(string storeId) : INavigationRequestPolicy
    {
        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            return context.Route is TestRoutes.StoreRoute route && route.StoreId == storeId
                ? ValueTask.FromException<RouterNavigationRequest>(new UnauthorizedAccessException("Rejected"))
                : ValueTask.FromResult(context.Request);
        }
    }

    private sealed class RecordingLeaseHost : IRouteEntryLeaseHost
    {
        private readonly AsyncLocal<bool> _onDispatcher = new();
        private readonly List<RouteEntryLeaseReleaseException> _defects = [];

        public TaskCompletionSource<RouteEntryLeaseReleaseException> Fatal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<RouteEntryLeaseReleaseException> Defects
        {
            get
            {
                lock (_defects)
                {
                    return _defects.ToArray();
                }
            }
        }

        public bool IsOnDispatcher => _onDispatcher.Value;

        public bool FailInvocations { get; set; }

        public async Task InvokeAsync(Func<ValueTask> work)
        {
            await Task.Yield();
            if (FailInvocations)
                throw new InvalidOperationException("The dispatcher is gone.");
            _onDispatcher.Value = true;
            await work();
        }

        public void ReportDefect(RouteEntryLeaseReleaseException defect)
        {
            lock (_defects)
            {
                _defects.Add(defect);
            }
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

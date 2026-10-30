using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;

namespace AdamE.AppNav.Tests;

public sealed class RouterNavigatorRefreshTests
{
    [Fact]
    public async Task RefreshReappliesAcceptedStateWithoutPlanningOrHistoryMutation()
    {
        var presenter = new RecordingNavigationPresenter();
        var planner = TestNavigationPlanner.EchoStack();
        await using var router = new RouterNavigator(TestRoutes.CreateTable(), planner, presenter);
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));
        var state = router.CurrentState;
        var history = router.History;

        await router.RefreshPresentationAsync();

        Assert.Same(state, presenter.LastPlan?.TargetState);
        Assert.Equal(NavigationPlanKind.Refresh, presenter.LastPlan?.Kind);
        Assert.Same(state, router.CurrentState);
        Assert.Same(history, router.History);
        Assert.Single(planner.Calls);
        Assert.Equal(2, presenter.ApplyCount);
    }

    [Fact]
    public async Task RefreshQueuedBehindNavigationReadsItsCommittedState()
    {
        var presenter = new RecordingNavigationPresenter();
        var planner = TestNavigationPlanner.EchoStack();
        await using var router = new RouterNavigator(TestRoutes.CreateTable(), planner, presenter);
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.OnApplyAsync = async (plan, _, token) =>
        {
            if (plan.Kind == NavigationPlanKind.Refresh)
                return;
            entered.SetResult();
            await release.Task.WaitAsync(token);
        };
        var navigation = router.NavigateAsync(new TestRoutes.StoreRoute("two")).AsTask();
        await entered.Task;
        var refresh = router.RefreshPresentationAsync().AsTask();
        Assert.False(refresh.IsCompleted);
        Assert.Equal(2, presenter.ApplyCount);

        release.SetResult();
        var accepted = await navigation;
        await refresh;

        Assert.Same(accepted.State, presenter.LastPlan?.TargetState);
        Assert.Same(accepted.State, router.CurrentState);
        Assert.Equal(2, planner.Calls.Count);
        Assert.Equal(3, presenter.ApplyCount);
    }

    [Fact]
    public async Task FailedRefreshPreservesAcceptedStateAndHistory()
    {
        var presenter = new RecordingNavigationPresenter();
        await using var router = new RouterNavigator(TestRoutes.CreateTable(), TestNavigationPlanner.EchoStack(), presenter);
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));
        var state = router.CurrentState;
        var history = router.History;
        presenter.ThrowOnApply = new InvalidOperationException("Native remount failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => router.RefreshPresentationAsync().AsTask());

        Assert.Same(state, router.CurrentState);
        Assert.Same(history, router.History);
    }

    [Fact]
    public async Task CancelledRefreshPreservesAcceptedStateAndHistory()
    {
        var presenter = new RecordingNavigationPresenter();
        await using var router = new RouterNavigator(TestRoutes.CreateTable(), TestNavigationPlanner.EchoStack(), presenter);
        await router.NavigateAsync(new TestRoutes.StoreRoute("one"));
        var state = router.CurrentState;
        var history = router.History;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.OnApplyAsync = async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        using var cancellation = new CancellationTokenSource();
        var refresh = router.RefreshPresentationAsync(cancellation.Token).AsTask();
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Same(state, router.CurrentState);
        Assert.Same(history, router.History);
    }

    [Fact]
    public async Task RefreshOfEmptyStateDoesNotCreateHistory()
    {
        var presenter = new RecordingNavigationPresenter();
        var planner = TestNavigationPlanner.EchoStack();
        await using var router = new RouterNavigator(TestRoutes.CreateTable(), planner, presenter);
        var state = router.CurrentState;
        var history = router.History;

        await router.RefreshPresentationAsync();

        Assert.Same(state, presenter.LastPlan?.TargetState);
        Assert.Same(state, router.CurrentState);
        Assert.Same(history, router.History);
        Assert.Empty(planner.Calls);
    }

    [Fact]
    public async Task ShutdownCancelsAndDrainsPendingRefresh()
    {
        var presenter = new RecordingNavigationPresenter();
        var router = new RouterNavigator(TestRoutes.CreateTable(), TestNavigationPlanner.EchoStack(), presenter);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.OnApplyAsync = async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var refresh = router.RefreshPresentationAsync().AsTask();
        await entered.Task;

        await router.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.True(refresh.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => router.RefreshPresentationAsync().AsTask());
    }

    [Fact]
    public async Task PresenterCannotReenterRefreshDuringItsOwnOperation()
    {
        var presenter = new RecordingNavigationPresenter();
        await using var router = new RouterNavigator(TestRoutes.CreateTable(), TestNavigationPlanner.EchoStack(), presenter);
        presenter.OnApplyAsync = async (_, _, _) =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => router.RefreshPresentationAsync().AsTask());

        await router.RefreshPresentationAsync();

        Assert.Equal(1, presenter.ApplyCount);
    }
}

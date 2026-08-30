using AdamE.AppNav.Navigation;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class RouterNavigatorReentrancyTests
{
    [Theory]
    [InlineData(ReentrantOperation.Navigate)]
    [InlineData(ReentrantOperation.Back)]
    [InlineData(ReentrantOperation.Reconcile)]
    public async Task ReentrantOperationFailsPromptlyWithoutCommittingOuterNavigation(
        ReentrantOperation reentrantOperation)
    {
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);
        NavigationState reconciledState = StateFor(new TestRoutes.CatalogRoute("reconciled"));

        presenter.OnApplyAsync = async (_, _, _) =>
        {
            switch (reentrantOperation)
            {
                case ReentrantOperation.Navigate:
                    await navigator.NavigateAsync(new TestRoutes.StoreRoute("nested"));
                    break;
                case ReentrantOperation.Back:
                    await navigator.BackAsync();
                    break;
                case ReentrantOperation.Reconcile:
                    await navigator.ReconcileAsync(new NavigationReconciliation(
                        reconciledState,
                        NavigationReconciliationSource.HostBack,
                        new TestRoutes.CatalogRoute("reconciled")));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(reentrantOperation));
            }
        };

        Task<NavigationResult> outerNavigation = navigator
            .NavigateAsync(new TestRoutes.StoreRoute("outer"))
            .AsTask();
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            outerNavigation.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("Reentrant router operations are not supported", exception.Message, StringComparison.Ordinal);
        Assert.Contains("same RouterNavigator", exception.Message, StringComparison.Ordinal);
        Assert.Same(NavigationState.Empty, navigator.CurrentState);
        Assert.Empty(navigator.History.Entries);

        presenter.OnApplyAsync = null;
        NavigationResult laterNavigation = await navigator.NavigateAsync(new TestRoutes.StoreRoute("later"));

        Assert.Equal(new TestRoutes.StoreRoute("later"), laterNavigation.Route);
        Assert.Same(laterNavigation.State, navigator.CurrentState);
        Assert.Single(navigator.History.Entries);
    }

    [Fact]
    public async Task IndependentlyStartedNavigationOperationsRemainSerialized()
    {
        var firstPresentationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPresentation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyCount = 0;
        var presenter = new RecordingNavigationPresenter
        {
            OnApplyAsync = async (_, _, _) =>
            {
                if (Interlocked.Increment(ref applyCount) == 1)
                {
                    firstPresentationStarted.TrySetResult();
                    await releaseFirstPresentation.Task;
                }
            }
        };
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);

        Task<NavigationResult> first = navigator
            .NavigateAsync(new TestRoutes.StoreRoute("first"))
            .AsTask();
        await firstPresentationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<NavigationResult> second = navigator
            .NavigateAsync(new TestRoutes.StoreRoute("second"))
            .AsTask();

        Assert.False(second.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref applyCount));

        releaseFirstPresentation.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, applyCount);
        Assert.Equal(
            ["first", "second"],
            presenter.Contexts.Select(context => Assert.IsType<TestRoutes.StoreRoute>(context.Route).StoreId));
    }

    [Fact]
    public async Task CapturedBackgroundWorkCanNavigateAfterOuterOperationCompletes()
    {
        var contextCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startBackgroundNavigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? backgroundNavigation = null;
        var applyCount = 0;
        RouterNavigator navigator = null!;
        var presenter = new RecordingNavigationPresenter
        {
            OnApplyAsync = async (_, _, _) =>
            {
                if (Interlocked.Increment(ref applyCount) != 1)
                    return;

                backgroundNavigation = Task.Run(async () =>
                {
                    contextCaptured.TrySetResult();
                    await startBackgroundNavigation.Task;
                    await navigator.NavigateAsync(new TestRoutes.StoreRoute("background"));
                });
                await contextCaptured.Task;
            }
        };
        navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);

        await navigator
            .NavigateAsync(new TestRoutes.StoreRoute("outer"))
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        startBackgroundNavigation.TrySetResult();
        await Assert.IsAssignableFrom<Task>(backgroundNavigation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, applyCount);
        Assert.Equal("background", Assert.IsType<TestRoutes.StoreRoute>(navigator.History.Current!.Route).StoreId);
    }

    [Fact]
    public async Task ReconciliationCapturedDuringNavigationRunsAfterOuterOperationReleasesTheLock()
    {
        var reconciledRoute = new TestRoutes.CatalogRoute("reconciled");
        NavigationState reconciledState = StateFor(reconciledRoute);
        var applyCount = 0;
        var presenter = new RecordingNavigationPresenter();
        presenter.OnApplyAsync = (_, _, _) =>
        {
            if (Interlocked.Increment(ref applyCount) == 1)
            {
                presenter.RequestReconciliation(new NavigationReconciliation(
                    reconciledState,
                    NavigationReconciliationSource.HostBack,
                    reconciledRoute));
            }

            return ValueTask.CompletedTask;
        };
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);

        await navigator.NavigateAsync(new TestRoutes.StoreRoute("outer"));
        await navigator.WhenReconciliationIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, applyCount);
        Assert.Equal(reconciledState, navigator.CurrentState);
        Assert.Equal(reconciledRoute, navigator.History.Current!.Route);
        Assert.Equal(2, navigator.History.Entries.Count);
    }

    [Fact]
    public async Task QueuedReconciliationRejectsReentrantNavigationWithoutDeadlocking()
    {
        var reconciledRoute = new TestRoutes.CatalogRoute("reconciled");
        NavigationState reconciledState = StateFor(reconciledRoute);
        var applyCount = 0;
        RouterNavigator navigator = null!;
        var presenter = new RecordingNavigationPresenter();
        presenter.OnApplyAsync = async (_, _, _) =>
        {
            int currentApply = Interlocked.Increment(ref applyCount);
            if (currentApply == 1)
            {
                presenter.RequestReconciliation(new NavigationReconciliation(
                    reconciledState,
                    NavigationReconciliationSource.HostBack,
                    reconciledRoute));
            }
            else if (currentApply == 2)
            {
                await navigator.NavigateAsync(new TestRoutes.StoreRoute("nested"));
            }
        };
        navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);

        NavigationResult outerNavigation = await navigator.NavigateAsync(new TestRoutes.StoreRoute("outer"));
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            navigator.WhenReconciliationIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("Reentrant router operations are not supported", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, applyCount);
        Assert.Same(outerNavigation.State, navigator.CurrentState);
        Assert.Equal(new TestRoutes.StoreRoute("outer"), navigator.History.Current!.Route);
        Assert.Single(navigator.History.Entries);
    }

    [Fact]
    public async Task CapturedBackgroundWorkCanNavigateAfterOuterOperationIsCancelled()
    {
        var contextCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startBackgroundNavigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? backgroundNavigation = null;
        var applyCount = 0;
        RouterNavigator navigator = null!;
        var presenter = new RecordingNavigationPresenter
        {
            OnApplyAsync = async (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref applyCount) != 1)
                    return;

                backgroundNavigation = Task.Run(async () =>
                {
                    contextCaptured.TrySetResult();
                    await startBackgroundNavigation.Task;
                    await navigator.NavigateAsync(new TestRoutes.StoreRoute("background"));
                });
                await contextCaptured.Task;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);
        using var cancellation = new CancellationTokenSource();

        Task<NavigationResult> outerNavigation = navigator
            .NavigateAsync(new TestRoutes.StoreRoute("outer"), cancellation.Token)
            .AsTask();
        await contextCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            outerNavigation.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(NavigationState.Empty, navigator.CurrentState);
        Assert.Empty(navigator.History.Entries);

        startBackgroundNavigation.TrySetResult();
        await Assert.IsAssignableFrom<Task>(backgroundNavigation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, applyCount);
        Assert.Equal("background", Assert.IsType<TestRoutes.StoreRoute>(navigator.History.Current!.Route).StoreId);
    }

    private static NavigationState StateFor(AppRoute route)
    {
        return TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("route", route))));
    }

    public enum ReentrantOperation
    {
        Navigate,
        Back,
        Reconcile
    }
}

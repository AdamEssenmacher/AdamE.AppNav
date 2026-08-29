using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class RouterNavigatorLifetimeTests
{
    [Fact]
    public async Task DisposeCancelsAcceptedNavigationBeforePresentationCommits()
    {
        var presenter = new BlockingFirstPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);
        Task<NavigationResult> navigation = navigator
            .NavigateAsync(new TestRoutes.StoreRoute("northwind"))
            .AsTask();
        await presenter.FirstApplyStarted;

        navigator.Dispose();
        presenter.ReleaseFirstApply();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        Assert.Same(NavigationState.Empty, navigator.CurrentState);
        Assert.Null(navigator.History.Current);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            navigator.NavigateAsync(new TestRoutes.StoreRoute("contoso")).AsTask());
        await navigator.DisposeAsync();
    }

    [Fact]
    public async Task WorkThatReturnsAfterPresentationCommitStillCompletesDuringShutdown()
    {
        var presenter = new BlockingFirstPresenter(throwWhenCancellationObserved: false);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);
        Task<NavigationResult> navigation = navigator
            .NavigateAsync(new TestRoutes.StoreRoute("northwind"))
            .AsTask();
        await presenter.FirstApplyStarted;

        navigator.Dispose();
        presenter.ReleaseFirstApply();

        NavigationResult result = await navigation;
        Assert.True(result.Presented);
        Assert.Same(result.State, navigator.CurrentState);
        Assert.NotNull(navigator.History.Current);
        await navigator.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsyncWaitsForAdmittedNavigationAndQueuedReconciliation()
    {
        var presenter = new BlockingFirstPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);
        Task<NavigationResult> navigation = navigator
            .NavigateAsync(new TestRoutes.StoreRoute("northwind"))
            .AsTask();
        await presenter.FirstApplyStarted;
        NavigationState reconciledState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("catalog", new TestRoutes.CatalogRoute("northwind")))));
        presenter.RequestReconciliation(new NavigationReconciliation(
            reconciledState,
            NavigationReconciliationSource.HostBack,
            new TestRoutes.CatalogRoute("northwind")));

        Task shutdown = navigator.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, presenter.HandlerCount);

        presenter.ReleaseFirstApply();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        await shutdown;

        Assert.Equal(1, presenter.ApplyCount);
        Assert.Same(NavigationState.Empty, navigator.CurrentState);
        Assert.Null(navigator.History.Current);
    }

    [Fact]
    public void FactoryNavigatorExposesAndHonorsSynchronousOwnership()
    {
        var presenter = new BlockingFirstPresenter();
        var planner = new DisposablePlanner();
        IRouterNavigator navigator = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            planner,
            presenter);

        Assert.Equal(1, presenter.HandlerCount);

        navigator.Dispose();

        Assert.Equal(0, presenter.HandlerCount);
        Assert.False(presenter.Disposed);
        Assert.False(planner.Disposed);
    }

    [Fact]
    public async Task DisposeDoesNotWaitForCancellationCallbacksButDisposeAsyncDoes()
    {
        var planner = new BlockingCancellationPlanner();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            NullNavigationPresenter.Instance);
        Task<NavigationResult> navigation = navigator
            .NavigateAsync(new TestRoutes.StoreRoute("northwind"))
            .AsTask();
        await planner.Started;

        Task dispose = Task.Run(navigator.Dispose);
        Task shutdown = Task.CompletedTask;
        try
        {
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
            await planner.CancellationCallbackStarted.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown = navigator.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            planner.ReleaseCancellationCallback();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        await shutdown;
    }

    private sealed class BlockingFirstPresenter(bool throwWhenCancellationObserved = true) : INavigationPresenter, IDisposable
    {
        private readonly TaskCompletionSource<bool> _firstApplyStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFirstApply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EventHandler<NavigationReconciliationRequestedEventArgs>? _reconciliationRequested;

        public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
        {
            add => _reconciliationRequested += value;
            remove => _reconciliationRequested -= value;
        }

        public Task FirstApplyStarted => _firstApplyStarted.Task;

        public int ApplyCount { get; private set; }

        public int HandlerCount => _reconciliationRequested?.GetInvocationList().Length ?? 0;

        public bool Disposed { get; private set; }

        public async ValueTask ApplyAsync(
            NavigationPlan plan,
            NavigationPresentationContext context,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            if (ApplyCount != 1)
                return;

            _firstApplyStarted.TrySetResult(true);
            await _releaseFirstApply.Task;
            if (throwWhenCancellationObserved)
                cancellationToken.ThrowIfCancellationRequested();
        }

        public void RequestReconciliation(NavigationReconciliation reconciliation)
        {
            _reconciliationRequested?.Invoke(
                this,
                new NavigationReconciliationRequestedEventArgs(reconciliation));
        }

        public void ReleaseFirstApply()
        {
            _releaseFirstApply.TrySetResult(true);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class DisposablePlanner : IAppNavigationPlanner, IDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class BlockingCancellationPlanner : IAppNavigationPlanner
    {
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _cancellationCallbackStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseCancellationCallback =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;

        public Task Started => _started.Task;

        public Task CancellationCallbackStarted => _cancellationCallbackStarted.Task;

        public async ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            _registration = cancellationToken.Register(() =>
            {
                _cancellationCallbackStarted.TrySetResult(true);
                _releaseCancellationCallback.Task.GetAwaiter().GetResult();
            });
            _started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation wait completed unexpectedly.");
        }

        public void ReleaseCancellationCallback()
        {
            _releaseCancellationCallback.TrySetResult(true);
            _registration.Dispose();
        }
    }
}

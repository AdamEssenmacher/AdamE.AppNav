using AdamE.AppNav.Back;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;

namespace AdamE.AppNav.AdapterContract.Tests;

public sealed class PublicAdapterContractTests
{
    [Fact]
    public async Task SuccessfulPresentationCommitsLogicalStateAndHistory()
    {
        NavigationState targetState = StackState(new ContractRoute("detail"));
        var presenter = new ContractPresenter();
        await using IRouterNavigator navigator = CreateNavigator(presenter, targetState);
        RouterNavigationRequest request = RouterNavigationRequest.FromRoute(
            new ContractRoute("requested"),
            NavigationRequestSource.Test,
            disposition: RouterNavigationDisposition.Canonical);

        NavigationResult result = await navigator.NavigateAsync(request);

        Assert.True(result.Presented);
        Assert.Equal(targetState, result.State);
        Assert.Equal(targetState, navigator.CurrentState);
        Assert.Equal(targetState, presenter.LastPlan?.TargetState);
        Assert.Same(NavigationState.Empty, presenter.LastContext?.CurrentState);
        Assert.Equal(request.Route, presenter.LastContext?.Request.Route);
        Assert.Equal(request.Source, presenter.LastContext?.Request.Source);
        Assert.Equal(request.Disposition, presenter.LastContext?.Request.Disposition);
        Assert.Equal(targetState, navigator.History.Current?.State);
        Assert.Equal(new ContractRoute("detail"), navigator.History.Current?.Route);
    }

    [Fact]
    public async Task PresentationFailureDoesNotCommitLogicalStateOrHistory()
    {
        NavigationState initialState = StackState(new ContractRoute("home"));
        NavigationState targetState = StackState(new ContractRoute("detail"));
        var expected = new ContractPresentationException();
        var presenter = new ContractPresenter { Failure = expected };
        await using IRouterNavigator navigator = CreateNavigator(presenter, targetState, initialState);

        ContractPresentationException actual = await Assert.ThrowsAsync<ContractPresentationException>(() =>
            navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
                new ContractRoute("detail"),
                NavigationRequestSource.Test)).AsTask());

        Assert.Same(expected, actual);
        Assert.Equal(initialState, navigator.CurrentState);
        Assert.Null(navigator.History.Current);
    }

    [Fact]
    public async Task PresentationCancellationDoesNotCommitLogicalStateOrHistory()
    {
        NavigationState initialState = StackState(new ContractRoute("home"));
        NavigationState targetState = StackState(new ContractRoute("detail"));
        var presenter = new ContractPresenter { WaitForCancellation = true };
        await using IRouterNavigator navigator = CreateNavigator(presenter, targetState, initialState);
        using var cancellation = new CancellationTokenSource();
        Task<NavigationResult> navigation = navigator.NavigateAsync(
                RouterNavigationRequest.FromRoute(new ContractRoute("detail"), NavigationRequestSource.Test),
                cancellation.Token)
            .AsTask();
        await presenter.ApplyStarted.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        Assert.Equal(initialState, navigator.CurrentState);
        Assert.Null(navigator.History.Current);
    }

    [Fact]
    public async Task BackPolicyCancellationDoesNotCrossThePresenterBoundary()
    {
        var firstRoute = new ContractRoute("first");
        NavigationState initialState = new(
            [
                new WindowNode(
                    "main",
                    new StackNode(
                        "main-stack",
                        [new RouteEntry("first", firstRoute), new RouteEntry("second", new ContractRoute("second"))]))
            ],
            "main");
        var presenter = new ContractPresenter();
        var policy = new CancelBackPolicy();
        await using IRouterNavigator navigator = CreateNavigator(
            presenter,
            StackState(new ContractRoute("unused")),
            initialState,
            [policy]);

        BackNavigationResult result = await navigator.BackAsync(
            new BackNavigationRequest("main", BackNavigationSource.Host));

        Assert.Equal(BackNavigationStatus.Canceled, result.Status);
        Assert.Null(result.NavigationResult);
        Assert.Equal(initialState, navigator.CurrentState);
        Assert.Null(navigator.History.Current);
        Assert.Equal(0, presenter.ApplyCount);
        Assert.Equal("main", policy.Context?.Request.WindowId);
        Assert.Equal(BackNavigationSource.Host, policy.Context?.Request.Source);
        NavigationState candidateState = Assert.IsType<NavigationState>(policy.Context?.CandidatePlan.TargetState);
        WindowNode candidateWindow = Assert.Single(candidateState.Windows);
        var candidateStack = Assert.IsType<StackNode>(candidateWindow.Root);
        RouteEntry candidateEntry = Assert.Single(candidateStack.Entries);
        Assert.Equal("main", candidateWindow.Id);
        Assert.Equal("main-stack", candidateStack.Id);
        Assert.Equal("first", candidateEntry.Id);
        Assert.Equal(firstRoute, candidateEntry.Route);
    }

    [Fact]
    public async Task PresenterReconciliationIsAppliedAndCommitted()
    {
        NavigationState reconciledState = BranchHostState(new ContractRoute("catalog"));
        var presenter = new ContractPresenter();
        await using IRouterNavigator navigator = CreateNavigator(
            presenter,
            StackState(new ContractRoute("unused")));

        presenter.RequestReconciliation(new NavigationReconciliation(
            reconciledState,
            NavigationReconciliationSource.BranchChanged,
            new ContractRoute("catalog"),
            "adapter observed a branch change"));

        await presenter.ApplyCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => navigator.CurrentState == reconciledState);

        Assert.Equal(reconciledState, navigator.CurrentState);
        Assert.Equal(NavigationPlanKind.Reconcile, presenter.LastPlan?.Kind);
        Assert.Equal(NavigationRequestSource.HostReconciliation, presenter.LastContext?.Request.Source);
        Assert.Equal(reconciledState, navigator.History.Current?.State);
        Assert.Equal(new ContractRoute("catalog"), navigator.History.Current?.Route);
    }

    [Fact]
    public async Task DisposalDetachesPresenterEventsWithoutTakingPresenterOwnership()
    {
        NavigationState targetState = StackState(new ContractRoute("home"));
        var presenter = new ContractPresenter();
        IRouterNavigator navigator = CreateNavigator(presenter, targetState);

        Assert.Equal(1, presenter.ReconciliationHandlerCount);

        await navigator.DisposeAsync();

        Assert.Equal(0, presenter.ReconciliationHandlerCount);
        Assert.False(presenter.Disposed);
        presenter.RequestReconciliation(new NavigationReconciliation(
            targetState,
            NavigationReconciliationSource.HostBack,
            new ContractRoute("home")));
        Assert.Equal(0, presenter.ApplyCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
                new ContractRoute("detail"),
                NavigationRequestSource.Test)).AsTask());
    }

    [Fact]
    public async Task AsyncShutdownCancelsInFlightPresentationBeforeCommit()
    {
        NavigationState targetState = StackState(new ContractRoute("detail"));
        var presenter = new ContractPresenter { WaitForCancellation = true };
        IRouterNavigator navigator = CreateNavigator(presenter, targetState);
        Task<NavigationResult> navigation = navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
                new ContractRoute("detail"),
                NavigationRequestSource.Test))
            .AsTask();
        await presenter.ApplyStarted.WaitAsync(TimeSpan.FromSeconds(5));

        Task shutdown = navigator.DisposeAsync().AsTask();

        Assert.Equal(0, presenter.ReconciliationHandlerCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        await shutdown;
        Assert.Same(NavigationState.Empty, navigator.CurrentState);
        Assert.Null(navigator.History.Current);
        Assert.False(presenter.Disposed);
    }

    [Theory]
    [MemberData(nameof(SupportedTopologies))]
    public async Task PublicPresenterCanInspectEverySupportedTopology(
        NavigationState targetState,
        IReadOnlyList<Type> expectedNodeTypes)
    {
        var presenter = new ContractPresenter();
        await using IRouterNavigator navigator = CreateNavigator(presenter, targetState);

        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new ContractRoute("requested"),
            NavigationRequestSource.Test));

        Assert.Equal(expectedNodeTypes, presenter.VisitedNodeTypes);
    }

    public static TheoryData<NavigationState, IReadOnlyList<Type>> SupportedTopologies => new()
    {
        {
            StackState(new ContractRoute("stack")),
            new[] { typeof(WindowNode), typeof(StackNode) }
        },
        {
            BranchHostState(new ContractRoute("branch")),
            new[] { typeof(WindowNode), typeof(BranchHostNode), typeof(StackNode), typeof(StackNode) }
        },
        {
            ModalState(new ContractRoute("modal"), new ContractRoute("modal-content")),
            new[] { typeof(WindowNode), typeof(StackNode), typeof(ModalNode), typeof(StackNode) }
        },
        {
            ModalState(new ContractRoute("modal")),
            new[] { typeof(WindowNode), typeof(StackNode), typeof(ModalNode) }
        }
    };

    private static IRouterNavigator CreateNavigator(
        ContractPresenter presenter,
        NavigationState targetState,
        NavigationState? initialState = null,
        IReadOnlyList<IBackNavigationPolicy>? backNavigationPolicies = null)
    {
        var planner = new ContractPlanner(targetState);
        return RouterNavigatorFactory.Create(
            new RouteTableBuilder().Build(),
            planner,
            presenter,
            new RouterNavigatorFactoryOptions
            {
                InitialState = initialState,
                BackNavigationPolicies = backNavigationPolicies ?? []
            });
    }

    private static NavigationState StackState(ContractRoute route)
    {
        return new NavigationState(
            [new WindowNode("main", new StackNode("main-stack", [new RouteEntry("route", route)]))],
            "main");
    }

    private static NavigationState BranchHostState(ContractRoute route)
    {
        var branchHost = new BranchHostNode(
            "tabs",
            [
                new NavigationBranch(
                    "home",
                    "Home",
                    new StackNode("home-stack", [new RouteEntry("home", new ContractRoute("home"))])),
                new NavigationBranch(
                    "catalog",
                    "Catalog",
                    new StackNode("catalog-stack", [new RouteEntry("catalog", route)]))
            ],
            "catalog",
            "home");
        return new NavigationState([new WindowNode("main", branchHost)], "main");
    }

    private static NavigationState ModalState(ContractRoute modalRoute, ContractRoute? contentRoute = null)
    {
        var root = new StackNode("main-stack", [new RouteEntry("home", new ContractRoute("home"))]);
        StackNode? modalContent = contentRoute is null
            ? null
            : new StackNode("modal-stack", [new RouteEntry("modal-content", contentRoute)]);
        var modal = new ModalNode("dialog", new RouteEntry("dialog-shell", modalRoute), modalContent);
        return new NavigationState([new WindowNode("main", root, [modal])], "main");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed record ContractRoute(string Name) : AppRoute;

    private sealed class CancelBackPolicy : IBackNavigationPolicy
    {
        public BackNavigationPolicyContext? Context { get; private set; }

        public ValueTask<BackNavigationPolicyDecision> EvaluateAsync(
            BackNavigationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Context = context;
            return ValueTask.FromResult(BackNavigationPolicyDecision.Cancel);
        }
    }

    private sealed class ContractPlanner(NavigationState targetState) : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new NavigationPlan(targetState));
        }
    }

    private sealed class ContractPresenter : INavigationPresenter, IDisposable
    {
        private readonly TaskCompletionSource<bool> _applyStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _applyCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Type> _visitedNodeTypes = [];
        private EventHandler<NavigationReconciliationRequestedEventArgs>? _reconciliationRequested;

        public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
        {
            add => _reconciliationRequested += value;
            remove => _reconciliationRequested -= value;
        }

        public Exception? Failure { get; init; }

        public bool WaitForCancellation { get; init; }

        public Task ApplyStarted => _applyStarted.Task;

        public Task ApplyCompleted => _applyCompleted.Task;

        public int ApplyCount { get; private set; }

        public int ReconciliationHandlerCount => _reconciliationRequested?.GetInvocationList().Length ?? 0;

        public bool Disposed { get; private set; }

        public NavigationPlan? LastPlan { get; private set; }

        public NavigationPresentationContext? LastContext { get; private set; }

        public IReadOnlyList<Type> VisitedNodeTypes => _visitedNodeTypes.ToArray();

        public async ValueTask ApplyAsync(
            NavigationPlan plan,
            NavigationPresentationContext context,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            LastPlan = plan;
            LastContext = context;
            _visitedNodeTypes.Clear();
            Visit(plan.TargetState);
            _applyStarted.TrySetResult(true);

            try
            {
                if (Failure is not null)
                    throw Failure;

                if (WaitForCancellation)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                _applyCompleted.TrySetResult(true);
            }
        }

        public void RequestReconciliation(NavigationReconciliation reconciliation)
        {
            _reconciliationRequested?.Invoke(
                this,
                new NavigationReconciliationRequestedEventArgs(reconciliation));
        }

        public void Dispose()
        {
            Disposed = true;
        }

        private void Visit(NavigationState state)
        {
            foreach (WindowNode window in state.Windows)
            {
                _visitedNodeTypes.Add(typeof(WindowNode));
                if (window.Root is not null)
                    Visit(window.Root);

                foreach (ModalNode modal in window.Modals)
                    Visit(modal);
            }
        }

        private void Visit(NavigationNode node)
        {
            _visitedNodeTypes.Add(node.GetType());
            switch (node)
            {
                case StackNode:
                    return;
                case BranchHostNode branchHost:
                    foreach (NavigationBranch branch in branchHost.Branches)
                        Visit(branch.Content);
                    return;
                case ModalNode modal when modal.Content is not null:
                    Visit(modal.Content);
                    return;
                case ModalNode:
                    return;
                default:
                    throw new NotSupportedException($"Unsupported contract node type '{node.GetType().FullName}'.");
            }
        }
    }

    private sealed class ContractPresentationException : Exception
    {
    }
}

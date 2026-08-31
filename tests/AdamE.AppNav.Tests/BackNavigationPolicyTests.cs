using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.History;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class BackNavigationPolicyTests
{
    [Fact]
    public async Task PoliciesSeeValidatedCandidatePlanAndRequestInRegistrationOrder()
    {
        var calls = new List<string>();
        BackNavigationPolicyContext? observed = null;
        var first = new DelegatePolicy((context, _) =>
        {
            calls.Add("first");
            observed = context;
            return ValueTask.FromResult(BackNavigationPolicyDecision.Continue);
        });
        var second = new DelegatePolicy((_, _) =>
        {
            calls.Add("second");
            return ValueTask.FromResult(BackNavigationPolicyDecision.Continue);
        });
        var presenter = new RecordingNavigationPresenter();
        var navigator = CreateNavigator(presenter, first, second);
        var request = new BackNavigationRequest("main", BackNavigationSource.Host);

        BackNavigationResult result = await navigator.BackAsync(request);

        Assert.Equal(["first", "second"], calls);
        Assert.NotNull(observed);
        Assert.Same(request, observed.Request);
        Assert.Equal("main", observed.NavigationContext.ResolvedWindowId);
        Assert.Equal(NavigationPlanKind.Back, observed.CandidatePlan.Kind);
        Assert.Single(Assert.IsType<StackNode>(observed.CandidatePlan.TargetState.ActiveWindow!.Root).Entries);
        Assert.Equal(BackNavigationStatus.Completed, result.Status);
        Assert.Equal(1, presenter.ApplyCount);
    }

    [Fact]
    public async Task FirstCancelShortCircuitsWithoutPresentationStateOrHistoryMutation()
    {
        var calls = new List<string>();
        var first = new DelegatePolicy((_, _) =>
        {
            calls.Add("first");
            return ValueTask.FromResult(BackNavigationPolicyDecision.Cancel);
        });
        var second = new DelegatePolicy((_, _) =>
        {
            calls.Add("second");
            return ValueTask.FromResult(BackNavigationPolicyDecision.Continue);
        });
        var presenter = new RecordingNavigationPresenter();
        var navigator = CreateNavigator(presenter, first, second);
        NavigationState previousState = navigator.CurrentState;
        var previousHistory = navigator.History;

        BackNavigationResult result = await navigator.BackAsync();

        Assert.Equal(BackNavigationStatus.Canceled, result.Status);
        Assert.Null(result.NavigationResult);
        Assert.Equal(["first"], calls);
        Assert.Same(previousState, navigator.CurrentState);
        Assert.Same(previousHistory, navigator.History);
        Assert.Equal(0, presenter.ApplyCount);
    }

    [Fact]
    public async Task UnhandledBackSkipsPolicies()
    {
        var policy = new DelegatePolicy((_, _) =>
            throw new Xunit.Sdk.XunitException("Policy should not run."));
        var state = new NavigationState(
            [new WindowNode("main", new StackNode("stack", [Entry("home")]))],
            "main");
        var navigator = CreateNavigator(new RecordingNavigationPresenter(), state, policy);

        BackNavigationResult result = await navigator.BackAsync();

        Assert.Equal(BackNavigationStatus.Unhandled, result.Status);
        Assert.Equal(0, policy.CallCount);
    }

    [Fact]
    public async Task AsyncPolicyDefersPresentationUntilDecisionCompletes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new DelegatePolicy(async (_, cancellationToken) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            return BackNavigationPolicyDecision.Continue;
        });
        var presenter = new RecordingNavigationPresenter();
        var navigator = CreateNavigator(presenter, policy);

        Task<BackNavigationResult> operation = navigator.BackAsync().AsTask();
        await entered.Task;

        Assert.False(operation.IsCompleted);
        Assert.Equal(0, presenter.ApplyCount);

        release.SetResult();
        Assert.Equal(BackNavigationStatus.Completed, (await operation).Status);
        Assert.Equal(1, presenter.ApplyCount);
    }

    [Fact]
    public async Task PolicyCancellationAndFailureDoNotMutateState()
    {
        var policyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledPolicy = new DelegatePolicy(async (_, cancellationToken) =>
        {
            policyEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return BackNavigationPolicyDecision.Continue;
        });
        var canceledPresenter = new RecordingNavigationPresenter();
        var canceledNavigator = CreateNavigator(canceledPresenter, canceledPolicy);
        NavigationState canceledState = canceledNavigator.CurrentState;
        NavigationHistory canceledHistory = canceledNavigator.History;
        using var cancellation = new CancellationTokenSource();

        Task<BackNavigationResult> canceledOperation = canceledNavigator
            .BackAsync(cancellationToken: cancellation.Token)
            .AsTask();
        await policyEntered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledOperation);
        Assert.Same(canceledState, canceledNavigator.CurrentState);
        Assert.Same(canceledHistory, canceledNavigator.History);
        Assert.Equal(0, canceledPresenter.ApplyCount);

        var failingPolicy = new DelegatePolicy((_, _) =>
            ValueTask.FromException<BackNavigationPolicyDecision>(new InvalidOperationException("policy failed")));
        var failingNavigator = CreateNavigator(new RecordingNavigationPresenter(), failingPolicy);
        NavigationState failingState = failingNavigator.CurrentState;

        await Assert.ThrowsAsync<InvalidOperationException>(() => failingNavigator.BackAsync().AsTask());
        Assert.Same(failingState, failingNavigator.CurrentState);
    }

    [Fact]
    public async Task WindowIdOverloadForwardsAnApplicationCommandRequest()
    {
        BackNavigationPolicyContext? observed = null;
        var policy = new DelegatePolicy((context, _) =>
        {
            observed = context;
            return ValueTask.FromResult(BackNavigationPolicyDecision.Cancel);
        });
        IRouterNavigator navigator = CreateNavigator(new RecordingNavigationPresenter(), policy);

        BackNavigationResult result = await navigator.BackAsync("main");

        Assert.Equal(BackNavigationStatus.Canceled, result.Status);
        Assert.Equal("main", observed?.Request.WindowId);
        Assert.Equal(BackNavigationSource.ApplicationCommand, observed?.Request.Source);
    }

    [Fact]
    public async Task PolicyDiagnosticsShareOperationAndReportCancellation()
    {
        var diagnostics = new NavigationDiagnostics(
            options: new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full });
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var policy = new DelegatePolicy((_, _) =>
            ValueTask.FromResult(BackNavigationPolicyDecision.Cancel));
        var navigator = CreateNavigator(new RecordingNavigationPresenter(), DefaultState(), diagnostics, policy);

        await navigator.BackAsync(new BackNavigationRequest("main", BackNavigationSource.Host));

        NavigationDiagnosticEvent started = Assert.Single(events, item => item.Kind == NavigationDiagnosticEventKind.BackStarted);
        NavigationDiagnosticEvent policyStarted = Assert.Single(events, item => item.Kind == NavigationDiagnosticEventKind.BackPolicyStarted);
        NavigationDiagnosticEvent policyCompleted = Assert.Single(events, item => item.Kind == NavigationDiagnosticEventKind.BackPolicyCompleted);
        NavigationDiagnosticEvent canceled = Assert.Single(events, item => item.Kind == NavigationDiagnosticEventKind.BackCanceled);
        Assert.Equal(started.OperationId, policyStarted.OperationId);
        Assert.Equal(started.OperationId, policyCompleted.OperationId);
        Assert.Equal(started.OperationId, canceled.OperationId);
        Assert.Equal(BackNavigationSource.Host.ToString(), policyStarted.Data[NavigationDiagnosticDataKeys.BackSource]);
        Assert.DoesNotContain(events, item => item.Kind == NavigationDiagnosticEventKind.PresentationStarted);
    }

    [Fact]
    public async Task FactoryOptionsPropagateBackPolicies()
    {
        var policy = new DelegatePolicy((_, _) =>
            ValueTask.FromResult(BackNavigationPolicyDecision.Cancel));
        await using IRouterNavigator navigator = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions
            {
                InitialState = DefaultState(),
                BackNavigationPolicies = [policy]
            });

        BackNavigationResult result = await navigator.BackAsync();

        Assert.Equal(BackNavigationStatus.Canceled, result.Status);
        Assert.Equal(1, policy.CallCount);
    }

    [Fact]
    public async Task PolicyCannotReenterSameNavigator()
    {
        IRouterNavigator? navigator = null;
        var policy = new DelegatePolicy(async (_, cancellationToken) =>
        {
            await navigator!.BackAsync(cancellationToken: cancellationToken);
            return BackNavigationPolicyDecision.Continue;
        });
        navigator = CreateNavigator(new RecordingNavigationPresenter(), policy);
        NavigationState previousState = navigator.CurrentState;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => navigator.BackAsync().AsTask());

        Assert.Contains("Reentrant router operations are not supported", exception.Message, StringComparison.Ordinal);
        Assert.Same(previousState, navigator.CurrentState);
    }

    [Fact]
    public async Task ShutdownCancelsDeferredPolicyAndWaitsForOperationExit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policy = new DelegatePolicy(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return BackNavigationPolicyDecision.Continue;
        });
        var navigator = CreateNavigator(new RecordingNavigationPresenter(), policy);
        Task<BackNavigationResult> operation = navigator.BackAsync().AsTask();
        await entered.Task;

        Task shutdown = navigator.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnknownPolicyDecisionFailsWithoutMutation()
    {
        var policy = new DelegatePolicy((_, _) =>
            ValueTask.FromResult((BackNavigationPolicyDecision)42));
        var navigator = CreateNavigator(new RecordingNavigationPresenter(), policy);
        NavigationState previousState = navigator.CurrentState;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => navigator.BackAsync().AsTask());

        Assert.Contains("unknown decision value '42'", exception.Message, StringComparison.Ordinal);
        Assert.Same(previousState, navigator.CurrentState);
    }

    [Theory]
    [InlineData(BackNavigationSource.Host, NavigationRequestSource.HostBack)]
    [InlineData(BackNavigationSource.ApplicationCommand, NavigationRequestSource.InAppCommand)]
    public async Task BackAttributesTheOriginatingSourceToHistoryAndPresentation(
        BackNavigationSource backSource,
        NavigationRequestSource expectedSource)
    {
        var presenter = new RecordingNavigationPresenter();
        RouterNavigator navigator = CreateNavigator(presenter);

        BackNavigationResult result = await navigator.BackAsync(
            new BackNavigationRequest("main", backSource));

        Assert.Equal(BackNavigationStatus.Completed, result.Status);
        Assert.Equal(expectedSource, presenter.LastContext!.Request.Source);
        Assert.Equal(expectedSource, navigator.History.Current!.Request.Source);
    }

    private static RouterNavigator CreateNavigator(
        RecordingNavigationPresenter presenter,
        params IBackNavigationPolicy[] policies) =>
        CreateNavigator(presenter, DefaultState(), NavigationDiagnostics.None, policies);

    private static RouterNavigator CreateNavigator(
        RecordingNavigationPresenter presenter,
        NavigationState state,
        params IBackNavigationPolicy[] policies) =>
        CreateNavigator(presenter, state, NavigationDiagnostics.None, policies);

    private static RouterNavigator CreateNavigator(
        RecordingNavigationPresenter presenter,
        NavigationState state,
        NavigationDiagnostics diagnostics,
        params IBackNavigationPolicy[] policies) =>
        new(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter,
            new RouterNavigatorOptions
            {
                InitialState = state,
                Diagnostics = diagnostics,
                BackNavigationPolicies = policies
            });

    private static NavigationState DefaultState() =>
        new(
            [new WindowNode("main", new StackNode("stack", [Entry("home"), Entry("detail")]))],
            "main");

    private static RouteEntry Entry(string id) =>
        new(id, new TestRoutes.StoreRoute(id));

    private sealed class DelegatePolicy(
        Func<BackNavigationPolicyContext, CancellationToken, ValueTask<BackNavigationPolicyDecision>> evaluate)
        : IBackNavigationPolicy
    {
        public int CallCount { get; private set; }

        public ValueTask<BackNavigationPolicyDecision> EvaluateAsync(
            BackNavigationPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return evaluate(context, cancellationToken);
        }
    }
}

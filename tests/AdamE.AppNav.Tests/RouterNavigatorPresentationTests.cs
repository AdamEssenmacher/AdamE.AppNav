using System.Threading.Channels;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class RouterNavigatorPresentationTests
{
    [Fact]
    public async Task SettlementDoesNotBlockNavigationAndReportsFailureWithoutChangingOutcome()
    {
        var diagnostics = new NavigationDiagnostics();
        var failures = Channel.CreateUnbounded<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) =>
        {
            if (diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationSettlementFailed)
                failures.Writer.TryWrite(diagnosticEvent);
        };

        var presenter = new SettlingPresenter();
        await using var navigator = new RouterNavigator(TestRoutes.CreateTable(), TestNavigationPlanner.EchoStack(),
            presenter, new RouterNavigatorOptions { Diagnostics = diagnostics });
        var entered = new TaskCompletionSource<(NavigationState State, string OperationId, int HistoryCount)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.Settle = (state, operationId) =>
        {
            if (!entered.TrySetResult((state, operationId, navigator.History.Entries.Count)))
                return ValueTask.CompletedTask;

            // Also block the synchronous part of cleanup to catch accidental inline invocation.
            finish.Task.GetAwaiter().GetResult();
            throw new InvalidOperationException("Post-commit cleanup failed.");
        };

        var timeout = TimeSpan.FromSeconds(5);
        try
        {
            var result = await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
                new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test)).AsTask().WaitAsync(timeout);
            var first = await entered.Task.WaitAsync(timeout);
            await navigator.RefreshPresentationAsync().AsTask().WaitAsync(timeout);

            Assert.True(result.Presented);
            Assert.Same(result.State, first.State);
            Assert.Same(result.State, navigator.CurrentState);
            Assert.Equal(1, first.HistoryCount);
            Assert.Single(navigator.History.Entries);
            Assert.Equal(presenter.AppliedOperations[0], first.OperationId);
        }
        finally
        {
            finish.TrySetResult();
        }

        var failure = await failures.Reader.ReadAsync().AsTask().WaitAsync(timeout);
        Assert.Equal((await entered.Task).OperationId, failure.OperationId);
        Assert.Equal(NavigationDiagnosticPhase.Presentation, failure.Phase);
    }

    [Fact]
    public async Task FailedPresentationSettlementReportsItsOwnFaultAndPreservesApplyFailure()
    {
        var diagnostics = new NavigationDiagnostics();
        var settlementFailure = new TaskCompletionSource<NavigationDiagnosticEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        diagnostics.EventWritten += (_, diagnosticEvent) =>
        {
            if (diagnosticEvent.Kind == NavigationDiagnosticEventKind.PresentationSettlementFailed)
                settlementFailure.TrySetResult(diagnosticEvent);
        };
        var applyFailure = new InvalidOperationException("Apply failed after rollback.");
        var presenter = new SettlingPresenter { ApplyFailure = applyFailure };
        await using var navigator = new RouterNavigator(TestRoutes.CreateTable(), TestNavigationPlanner.EchoStack(),
            presenter, new RouterNavigatorOptions { Diagnostics = diagnostics });
        var original = navigator.CurrentState;
        var settled = new TaskCompletionSource<NavigationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.Settle = (state, _) =>
        {
            settled.SetResult(state);
            throw new ApplicationException("Restored surface cleanup failed.");
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => navigator.NavigateAsync(
            RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test)).AsTask());
        var diagnostic = await settlementFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(applyFailure, error);
        Assert.Same(original, await settled.Task);
        Assert.Same(original, navigator.CurrentState);
        Assert.Empty(navigator.History.Entries);
        Assert.Equal(Assert.Single(presenter.AppliedOperations), diagnostic.OperationId);
    }

    [Fact]
    public async Task DisposalWaitsForSettlementStartedByAnOperationInFlightAndIgnoresItsFailure()
    {
        var presenter = new SettlingPresenter
        {
            ApplyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var navigator = new RouterNavigator(TestRoutes.CreateTable(), TestNavigationPlanner.EchoStack(), presenter);
        var settling = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.Settle = async (_, operationId) =>
        {
            settling.TrySetResult(operationId);
            await finish.Task.ConfigureAwait(false);
            throw new InvalidOperationException("Cleanup failed during shutdown.");
        };

        var timeout = TimeSpan.FromSeconds(5);
        try
        {
            // Dispose while the operation still holds admission, so its settlement is registered during shutdown.
            Task navigation = navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
                new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test)).AsTask();
            await presenter.Applying.Task.WaitAsync(timeout);
            Task shutdown = navigator.DisposeAsync().AsTask();
            presenter.ApplyGate.SetResult();
            await Task.WhenAny(navigation).WaitAsync(timeout);

            Assert.Equal(Assert.Single(presenter.AppliedOperations), await settling.Task.WaitAsync(timeout));
            await Task.Delay(100);
            Assert.False(shutdown.IsCompleted, "Disposal completed while presentation settlement was still running.");

            finish.SetResult();
            await shutdown.WaitAsync(timeout);
        }
        finally
        {
            finish.TrySetResult();
            presenter.ApplyGate.TrySetResult();
        }
    }

    private sealed class SettlingPresenter : INavigationPresenter, INavigationPresentationSettlement
    {
        public List<string> AppliedOperations { get; } = [];
        public Exception? ApplyFailure { get; init; }
        public TaskCompletionSource? ApplyGate { get; init; }
        public TaskCompletionSource Applying { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<NavigationState, string, ValueTask>? Settle { get; set; }

        public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
        {
            add { }
            remove { }
        }

        public async ValueTask ApplyAsync(NavigationPlan plan, NavigationPresentationContext context,
            CancellationToken cancellationToken = default)
        {
            AppliedOperations.Add(context.OperationId);
            Applying.TrySetResult();
            if (ApplyGate is { } gate)
                await gate.Task.ConfigureAwait(false);
            if (ApplyFailure is { } failure)
                throw failure;
        }

        public ValueTask OnPresentationSettledAsync(NavigationState state, string operationId) =>
            Settle?.Invoke(state, operationId) ?? ValueTask.CompletedTask;
    }

    [Fact]
    public async Task NavigationFinalizesPresentedRouteForPresenterResultAndHistory()
    {
        var requestedRoute = new TestRoutes.StoreRoute("requested");
        var rewrittenRoute = new TestRoutes.CatalogRoute("northwind");
        var rewrittenState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("rewritten", rewrittenRoute))));
        var presenter = new RecordingNavigationPresenter();
        var planner = TestNavigationPlanner.ForState(rewrittenState);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            planner,
            presenter);

        var result = await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            requestedRoute, NavigationRequestSource.Test));

        Assert.Equal(requestedRoute, planner.LastRoute);
        Assert.Equal(rewrittenState, presenter.LastPlan?.TargetState);
        Assert.Equal(rewrittenRoute, presenter.LastContext!.Route);
        Assert.Equal(rewrittenRoute, result.Route);
        Assert.Equal(rewrittenRoute, navigator.History.Current!.Route);
    }

    [Fact]
    public async Task PresenterRequestedReconciliationCommitsStateAndHistory()
    {
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);
        var reconciledRoute = new TestRoutes.CatalogRoute("northwind");
        var reconciledState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("catalog", reconciledRoute))));

        presenter.RequestReconciliation(new NavigationReconciliation(
            navigator.CurrentState,
            reconciledState,
            NavigationReconciliationSource.HostBack,
            reconciledRoute,
            "test reconciliation"));
        await navigator.WhenReconciliationIdleAsync();

        Assert.Equal(reconciledState, navigator.CurrentState);
        Assert.Equal(NavigationRequestSource.HostReconciliation, navigator.History.Current!.Request.Source);
        Assert.Equal(reconciledRoute, navigator.History.Current.Route);
        Assert.Equal(1, presenter.ApplyCount);
        Assert.Equal(NavigationPlanKind.Reconcile, presenter.LastPlan?.Kind);
    }

    [Fact]
    public async Task ReconciliationFinalizesPresentedRouteBeforeCommittingState()
    {
        var rewrittenRoute = new TestRoutes.CatalogRoute("northwind");
        var rewrittenState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("rewritten", rewrittenRoute))));
        var presenter = new RecordingNavigationPresenter();
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);
        var requestedRoute = new TestRoutes.StoreRoute("requested");

        var result = await navigator.ReconcileAsync(new NavigationReconciliation(
            navigator.CurrentState,
            rewrittenState,
            NavigationReconciliationSource.HostBack,
            requestedRoute,
            "test reconciliation"));

        Assert.Equal(1, presenter.ApplyCount);
        Assert.Equal(NavigationPlanKind.Reconcile, presenter.LastPlan?.Kind);
        Assert.Equal(rewrittenState, presenter.LastPlan?.TargetState);
        Assert.Equal(rewrittenRoute, presenter.LastContext!.Route);
        Assert.Equal(rewrittenRoute, result.Route);
        Assert.Equal(rewrittenState, result.State);
        Assert.False(result.Presented);
        Assert.Equal(rewrittenState, navigator.CurrentState);
        Assert.Equal(rewrittenRoute, navigator.History.Current!.Route);
        Assert.Equal(rewrittenState, navigator.History.Current.State);
    }

    [Fact]
    public async Task ReconciliationPresenterFailureDoesNotCommitStateOrHistory()
    {
        var initialState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("home", new TestRoutes.StoreRoute("northwind")))));
        var presenter = new RecordingNavigationPresenter
        {
            ThrowOnApply = new InvalidOperationException("Presentation failed.")
        };
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEventKind>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent.Kind);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter,
            new RouterNavigatorOptions
            {
                InitialState = initialState,
                Diagnostics = diagnostics
            });
        var reconciledState = TestNavigationState.State(
            "main",
            TestNavigationState.Window(
                "main",
                TestNavigationState.Stack(
                    "stack",
                    TestNavigationState.Entry("catalog", new TestRoutes.CatalogRoute("northwind")))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => navigator.ReconcileAsync(new NavigationReconciliation(
            navigator.CurrentState,
            reconciledState,
            NavigationReconciliationSource.HostBack,
            new TestRoutes.CatalogRoute("northwind"),
            "test reconciliation")).AsTask());

        Assert.Equal(1, presenter.ApplyCount);
        Assert.Equal(initialState, navigator.CurrentState);
        Assert.Empty(navigator.History.Entries);
        Assert.Contains(NavigationDiagnosticEventKind.PresentationFailed, events);
        Assert.Contains(NavigationDiagnosticEventKind.ReconciliationFailed, events);
        Assert.DoesNotContain(NavigationDiagnosticEventKind.PresentationCompleted, events);
        Assert.DoesNotContain(NavigationDiagnosticEventKind.ReconciliationCompleted, events);
    }
}

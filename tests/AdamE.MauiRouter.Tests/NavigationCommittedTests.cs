using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.Testing;

namespace AdamE.MauiRouter.Tests;

public sealed class NavigationCommittedTests
{
    [Fact]
    public async Task NavigateRaisesCommittedEventWithFinalStateAndHistory()
    {
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance);
        var previousState = navigator.CurrentState;
        var committedEvents = new List<NavigationCommittedEventArgs>();
        navigator.NavigationCommitted += (_, eventArgs) =>
        {
            committedEvents.Add(eventArgs);
            Assert.Same(navigator.CurrentState, eventArgs.CurrentState);
            Assert.Same(navigator.History, eventArgs.CurrentHistory);
        };
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("northwind"),
            NavigationRequestSource.Test,
            disposition: RouterNavigationDisposition.ReplaceCurrent);

        var result = await navigator.NavigateAsync(request);

        var committed = Assert.Single(committedEvents);
        Assert.Equal(NavigationCommitKind.Navigate, committed.Kind);
        Assert.Equal(result.Route, committed.Route);
        Assert.Same(result.Plan, committed.Plan);
        Assert.Same(previousState, committed.PreviousState);
        Assert.Same(result.State, committed.CurrentState);
        Assert.Same(navigator.History, committed.CurrentHistory);
        Assert.True(committed.Presented);
        Assert.Equal(RouterNavigationDisposition.ReplaceCurrent, committed.Request.Disposition);
        Assert.Equal(NavigationRequestSource.Test, committed.Request.Source);
        Assert.Equal(committed.OperationId, navigator.History.Current!.Id);
        Assert.Equal(committed.OperationId, committed.CurrentHistory.Current!.Id);
        Assert.Equal(committed.Request, navigator.History.Current.Request);
    }

    [Fact]
    public async Task RedirectedNavigationCommitsOnlyFinalRouteOnce()
    {
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                RequestPolicies = [new RedirectStoreToCatalogPolicy()]
            });
        var committedEvents = new List<NavigationCommittedEventArgs>();
        navigator.NavigationCommitted += (_, eventArgs) => committedEvents.Add(eventArgs);

        await navigator.NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test);

        var committed = Assert.Single(committedEvents);
        var route = Assert.IsType<TestRoutes.CatalogRoute>(committed.Route);
        Assert.Equal("northwind", route.StoreId);
        Assert.Equal(committed.Route, navigator.History.Current!.Route);
    }

    [Fact]
    public async Task BackRaisesCommittedEventOnlyWhenHandled()
    {
        var handledNavigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                InitialState = StateFor(
                    new TestRoutes.StoreRoute("northwind"),
                    new TestRoutes.CatalogRoute("northwind"))
            });
        var handledEvents = new List<NavigationCommittedEventArgs>();
        handledNavigator.NavigationCommitted += (_, eventArgs) => handledEvents.Add(eventArgs);

        var handled = await handledNavigator.BackAsync();

        Assert.True(handled.Handled);
        var committed = Assert.Single(handledEvents);
        Assert.Equal(NavigationCommitKind.Back, committed.Kind);
        Assert.True(committed.Presented);
        Assert.Equal(committed.OperationId, handledNavigator.History.Current!.Id);
        Assert.IsType<TestRoutes.StoreRoute>(committed.Route);

        var unhandledNavigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                InitialState = StateFor(new TestRoutes.StoreRoute("northwind"))
            });
        var unhandledEvents = new List<NavigationCommittedEventArgs>();
        unhandledNavigator.NavigationCommitted += (_, eventArgs) => unhandledEvents.Add(eventArgs);

        var unhandled = await unhandledNavigator.BackAsync();

        Assert.False(unhandled.Handled);
        Assert.Empty(unhandledEvents);
    }

    [Fact]
    public async Task ReconcileRaisesCommittedEventWithoutPresentedResult()
    {
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance);
        var targetState = StateFor(new TestRoutes.StoreRoute("northwind"));
        var committedEvents = new List<NavigationCommittedEventArgs>();
        navigator.NavigationCommitted += (_, eventArgs) => committedEvents.Add(eventArgs);

        var result = await navigator.ReconcileAsync(new NavigationReconciliation(
            targetState,
            NavigationReconciliationSource.NativeBackGesture,
            new TestRoutes.StoreRoute("northwind"),
            "native back"));

        var committed = Assert.Single(committedEvents);
        Assert.Equal(NavigationCommitKind.Reconcile, committed.Kind);
        Assert.False(result.Presented);
        Assert.False(committed.Presented);
        Assert.Equal(NavigationRequestSource.NativeReconciliation, committed.Request.Source);
        Assert.Equal(committed.OperationId, navigator.History.Current!.Id);
        Assert.Same(result.State, committed.CurrentState);
    }

    [Fact]
    public async Task RestoreRaisesCommittedEventForAcceptedSnapshot()
    {
        var state = StateFor(new TestRoutes.ProductDetailRoute("northwind", 123, "blue", "spring"));
        var history = HistoryFor(state, new TestRoutes.ProductDetailRoute("northwind", 123, "blue", "spring"));
        var snapshot = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).CreateSnapshot(state, history);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance);
        var committedEvents = new List<NavigationCommittedEventArgs>();
        navigator.NavigationCommitted += (_, eventArgs) => committedEvents.Add(eventArgs);

        var result = await navigator.RestoreAsync(snapshot);

        Assert.True(result.Accepted);
        var committed = Assert.Single(committedEvents);
        Assert.Equal(NavigationCommitKind.Restore, committed.Kind);
        Assert.True(committed.Presented);
        Assert.Equal(NavigationRequestSource.Restore, committed.Request.Source);
        Assert.False(string.IsNullOrWhiteSpace(committed.OperationId));
        Assert.Same(navigator.CurrentState, committed.CurrentState);
        Assert.Same(navigator.History, committed.CurrentHistory);
        Assert.Equal(history.Current!.Id, navigator.History.Current!.Id);
        Assert.Equal(history.Current.Id, committed.CurrentHistory.Current!.Id);
        Assert.NotEqual(committed.OperationId, committed.CurrentHistory.Current.Id);
    }

    [Fact]
    public async Task RestoreFromStoreRaisesCommittedEventForAcceptedSnapshot()
    {
        var state = StateFor(new TestRoutes.StoreRoute("northwind"));
        var history = HistoryFor(state, new TestRoutes.StoreRoute("northwind"));
        var store = new InMemoryNavigationStateStore
        {
            Snapshot = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).CreateSnapshot(state, history)
        };
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Persistence = new NavigationPersistenceOptions { Store = store }
            });
        var committedEvents = new List<NavigationCommittedEventArgs>();
        navigator.NavigationCommitted += (_, eventArgs) => committedEvents.Add(eventArgs);

        var result = await navigator.RestoreFromStoreAsync();

        Assert.True(result.Accepted);
        Assert.Equal(1, store.LoadCount);
        var committed = Assert.Single(committedEvents);
        Assert.Equal(NavigationCommitKind.Restore, committed.Kind);
        Assert.Same(navigator.CurrentState, committed.CurrentState);
        Assert.Same(navigator.History, committed.CurrentHistory);
    }

    [Fact]
    public async Task RejectedAndMissingRestoreDoNotRaiseCommittedEvent()
    {
        var state = StateFor(new TestRoutes.StoreRoute("northwind"));
        var history = HistoryFor(state, new TestRoutes.StoreRoute("northwind"));
        var snapshot = new NavigationSnapshotSerializer(TestRoutes.CreateTable()).CreateSnapshot(state, history);
        var rejectingNavigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Persistence = new NavigationPersistenceOptions
                {
                    RestorePolicies = [new RejectRestorePolicy()]
                }
            });
        var rejectedEvents = new List<NavigationCommittedEventArgs>();
        rejectingNavigator.NavigationCommitted += (_, eventArgs) => rejectedEvents.Add(eventArgs);

        var rejected = await rejectingNavigator.RestoreAsync(snapshot);

        Assert.False(rejected.Accepted);
        Assert.Empty(rejectedEvents);

        var missingStoreNavigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                Persistence = new NavigationPersistenceOptions { Store = new InMemoryNavigationStateStore() }
            });
        var missingEvents = new List<NavigationCommittedEventArgs>();
        missingStoreNavigator.NavigationCommitted += (_, eventArgs) => missingEvents.Add(eventArgs);

        var missing = await missingStoreNavigator.RestoreFromStoreAsync();

        Assert.False(missing.Accepted);
        Assert.Empty(missingEvents);
    }

    [Fact]
    public async Task FailedNavigationDoesNotRaiseCommittedEvent()
    {
        var presenter = new RecordingNavigationPresenter
        {
            ThrowOnApply = new InvalidOperationException("presentation failed")
        };
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            presenter);
        var committedEvents = new List<NavigationCommittedEventArgs>();
        navigator.NavigationCommitted += (_, eventArgs) => committedEvents.Add(eventArgs);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            navigator.NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test).AsTask());

        Assert.Empty(committedEvents);
    }

    [Fact]
    public async Task ThrowingSubscriberDoesNotFailNavigationOrPreventLaterSubscribers()
    {
        var diagnostics = new NavigationDiagnostics();
        var diagnosticEvents = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => diagnosticEvents.Add(diagnosticEvent);
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions { Diagnostics = diagnostics });
        var committedEvents = new List<NavigationCommittedEventArgs>();
        navigator.NavigationCommitted += (_, _) => throw new InvalidOperationException("subscriber failed");
        navigator.NavigationCommitted += (_, eventArgs) => committedEvents.Add(eventArgs);

        var result = await navigator.NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test);

        Assert.True(result.Presented);
        var committed = Assert.Single(committedEvents);
        var failure = Assert.Single(
            diagnosticEvents,
            diagnosticEvent => diagnosticEvent.Kind == NavigationDiagnosticEventKind.NavigationCommittedHandlerFailed);
        Assert.Equal(committed.OperationId, failure.OperationId);
        Assert.Equal(nameof(IRouterNavigator.NavigationCommitted), failure.Data[NavigationDiagnosticDataKeys.OriginalKind]);
        Assert.Equal(typeof(InvalidOperationException).FullName, failure.Data[NavigationDiagnosticDataKeys.ExceptionType]);
    }

    [Fact]
    public async Task SubscribersReceiveOriginalCommitBeforeReentrantFollowUpCommit()
    {
        var navigator = new RouterNavigator(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            NullNavigationPresenter.Instance);
        var observed = new List<string>();
        NavigationResult? followUpResult = null;
        var followUpTimedOut = false;

        navigator.NavigationCommitted += (_, eventArgs) =>
        {
            observed.Add($"first:{eventArgs.Route.GetType().Name}");
            if (eventArgs.Route is not TestRoutes.StoreRoute)
            {
                return;
            }

            var followUpTask = navigator.NavigateAsync(
                new TestRoutes.CatalogRoute("northwind"),
                NavigationRequestSource.Test).AsTask();
            if (!followUpTask.Wait(TimeSpan.FromSeconds(2)))
            {
                followUpTimedOut = true;
                return;
            }

            followUpResult = followUpTask.GetAwaiter().GetResult();
        };
        navigator.NavigationCommitted += (_, eventArgs) =>
        {
            observed.Add($"second:{eventArgs.Route.GetType().Name}");
        };

        await navigator.NavigateAsync(new TestRoutes.StoreRoute("northwind"), NavigationRequestSource.Test);

        Assert.False(followUpTimedOut);
        Assert.NotNull(followUpResult);
        Assert.Equal(
            [
                "first:StoreRoute",
                "second:StoreRoute",
                "first:CatalogRoute",
                "second:CatalogRoute"
            ],
            observed);
        Assert.IsType<TestRoutes.CatalogRoute>(navigator.History.Current!.Route);
    }

    private static NavigationState StateFor(params AppRoute[] routes)
    {
        var entries = routes
            .Select((route, index) => new RouteEntry($"entry-{index}", route))
            .ToArray();

        return new NavigationState(new[]
        {
            new WindowNode("main", new StackNode("stack", entries))
        }, "main");
    }

    private static NavigationHistory HistoryFor(NavigationState state, AppRoute route)
    {
        return NavigationHistory.Empty.Push(new NavigationHistoryEntry(
            "history",
            RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Test),
            route,
            state,
            "test",
            DateTimeOffset.UtcNow));
    }

    private sealed class RedirectStoreToCatalogPolicy : INavigationRequestPolicy
    {
        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            return request.Route is TestRoutes.StoreRoute storeRoute
                ? ValueTask.FromResult(RouterNavigationRequest.FromRoute(
                    new TestRoutes.CatalogRoute(storeRoute.StoreId),
                    request.Source,
                    request.WindowId,
                    request.Metadata,
                    request.Disposition,
                    request.Provenance))
                : ValueTask.FromResult(request);
        }
    }

    private sealed class RejectRestorePolicy : INavigationRestorePolicy
    {
        public ValueTask<NavigationRestoreDecision> EvaluateAsync(
            NavigationRestoreContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(NavigationRestoreDecision.Reject("restore rejected"));
        }
    }

    private sealed class InMemoryNavigationStateStore : INavigationStateStore
    {
        public NavigationSnapshot? Snapshot { get; init; }

        public int LoadCount { get; private set; }

        public ValueTask<NavigationSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return ValueTask.FromResult(Snapshot);
        }

        public ValueTask SaveAsync(NavigationSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.CompletedTask;
        }
    }
}

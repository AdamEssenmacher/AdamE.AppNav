using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.History;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using Microsoft.Extensions.Logging;

namespace AdamE.AppNav.Tests;

public sealed class DeferredNavigationRequestReplayerTests
{
    [Fact]
    public void DefaultReplayResultRepresentsNoDeferredWork()
    {
        DeferredNavigationReplayResult result = default;

        Assert.Equal(0, result.AttemptedCount);
        Assert.Equal(0, result.ReplayedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.False(result.HadDeferredRequests);
        Assert.False(result.ReplayedAny);
    }

    [Fact]
    public async Task ReplayAsync_ReplaysDeferredRequestsInFifoOrder()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("first"), NavigationRequestSource.AppLink, windowId: "main");
        var second = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("second"),
            NavigationRequestSource.Push,
            windowId: "secondary",
            metadata: new Dictionary<string, object?> { ["request-id"] = "two" },
            disposition: RouterNavigationDisposition.ReplaceCurrent);
        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);

        var navigator = new RecordingRouterNavigator();
        var replayer = new DeferredNavigationRequestReplayer(store, navigator);

        var result = await replayer.ReplayAsync();

        Assert.Equal(new[] { first, second }, navigator.Calls);
        Assert.Equal(new DeferredNavigationReplayResult(2, 2, 0), result);
        Assert.False(await store.HasDeferredRequestsAsync());
    }

    [Fact]
    public async Task ReplayAsync_ContinuesAfterOneReplayFailure()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("first"),
            NavigationRequestSource.AppLink,
            metadata: new Dictionary<string, object?> { ["throw"] = true });
        var second = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("second"), NavigationRequestSource.Push);
        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);

        var navigator = new RecordingRouterNavigator(static (request, _) =>
        {
            if (request.Metadata.TryGetValue("throw", out var value) && Equals(value, true))
            {
                throw new InvalidOperationException("Replay failure.");
            }

            return ValueTask.FromResult(new NavigationResult(
                request.Route!,
                new NavigationPlan(NavigationState.Empty),
                NavigationState.Empty,
                Presented: true));
        });
        var replayer = new DeferredNavigationRequestReplayer(store, navigator);

        var result = await replayer.ReplayAsync();

        Assert.Equal(new[] { first, second }, navigator.Calls);
        Assert.Equal(new DeferredNavigationReplayResult(2, 1, 1), result);
        Assert.True(await store.HasDeferredRequestsAsync());
        Assert.Equal([first], await SnapshotAsync(store));
    }

    [Fact]
    public async Task ReplayAsync_LoggerConstructorSanitizesReplayFailures()
    {
        const string routeValue = "private-store-id";
        const string exceptionMessage = "private-exception-message";
        var store = new InMemoryDeferredNavigationRequestStore();
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute(routeValue), NavigationRequestSource.AppLink);
        await store.EnqueueAsync(request);
        var navigator = new RecordingRouterNavigator((_, _) => throw new InvalidOperationException(exceptionMessage));
        var logger = new ReplayLogger();
        var replayer = new DeferredNavigationRequestReplayer(store, navigator, logger);

        Assert.Equal(new DeferredNavigationReplayResult(1, 0, 1), await replayer.ReplayAsync());
        Assert.Equal([request], await SnapshotAsync(store));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(routeValue, entry.Message);
        Assert.DoesNotContain(exceptionMessage, entry.Message);
        var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(entry.State["@Data"]);
        Assert.Equal(3, data.Count);
        Assert.Equal(typeof(TestRoutes.StoreRoute).FullName, data[NavigationDiagnosticDataKeys.RouteType]);
        Assert.Equal(nameof(NavigationRequestSource.AppLink), data[NavigationDiagnosticDataKeys.RequestSource]);
        Assert.Equal(typeof(InvalidOperationException).FullName, data[NavigationDiagnosticDataKeys.ExceptionType]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayAsync_FullDiagnosticsLogExceptionOnlyWithoutRedactor(bool useRedactor)
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("retry"), NavigationRequestSource.AppLink);
        await store.EnqueueAsync(request);
        var exception = new InvalidOperationException("Replay failed.", new Exception("Inner failure."));
        var navigator = new RecordingRouterNavigator((_, _) => throw exception);
        var logger = new ReplayLogger();
        var diagnostics = new NavigationDiagnostics(
            logger,
            new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full },
            useRedactor ? new ReplayRedactor() : null);
        var replayer = new DeferredNavigationRequestReplayer(store, navigator, logger: null, diagnostics: diagnostics);

        Assert.Equal(new DeferredNavigationReplayResult(1, 0, 1), await replayer.ReplayAsync());
        Assert.Equal([request], await SnapshotAsync(store));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(useRedactor ? null : exception, entry.Exception);
        var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(entry.State["@Data"]);
        Assert.Equal(!useRedactor, data.ContainsKey(NavigationDiagnosticDataKeys.ExceptionMessage));
    }

    private sealed class ReplayRedactor : INavigationDiagnosticRedactor
    {
        public NavigationDiagnosticEvent Redact(NavigationDiagnosticEvent diagnosticEvent)
        {
            var data = new Dictionary<string, object?>(diagnosticEvent.Data);
            data.Remove(NavigationDiagnosticDataKeys.ExceptionMessage);
            return diagnosticEvent with { Data = data };
        }
    }

    private sealed class ReplayLogger : ILogger<DeferredNavigationRequestReplayer>
    {
        public List<(LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> State)>
            Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
            Entries.Add((logLevel, formatter(state, exception), exception, properties.ToDictionary()));
        }
    }

    [Fact]
    public async Task ReplayAsync_RetriesPreviouslyFailedRequestOnLaterPass()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var request = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("retry"), NavigationRequestSource.AppLink);
        await store.EnqueueAsync(request);

        var attempts = 0;
        var navigator = new RecordingRouterNavigator((request, _) =>
        {
            attempts++;
            return attempts == 1
                ? throw new InvalidOperationException("Replay failure.")
                : ValueTask.FromResult(new NavigationResult(
                    request.Route!,
                    new NavigationPlan(NavigationState.Empty),
                    NavigationState.Empty,
                    Presented: true));
        });
        var replayer = new DeferredNavigationRequestReplayer(store, navigator);

        var firstPass = await replayer.ReplayAsync();

        Assert.Equal(new DeferredNavigationReplayResult(1, 0, 1), firstPass);
        Assert.Equal([request], await SnapshotAsync(store));

        var secondPass = await replayer.ReplayAsync();

        Assert.Equal(new DeferredNavigationReplayResult(1, 1, 0), secondPass);
        Assert.Equal([request, request], navigator.Calls);
        Assert.False(await store.HasDeferredRequestsAsync());
    }

    [Fact]
    public async Task ReplayAsync_SuccessfulRequestThatRedefersItselfRemainsForNextReplay()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        RouterNavigationRequest request = RouterNavigationRequest.FromRoute(
            new TestRoutes.StoreRoute("access-gated"),
            NavigationRequestSource.AppLink);
        await store.EnqueueAsync(request);
        var navigator = new RecordingRouterNavigator(async (replayed, cancellationToken) =>
        {
            await store.EnqueueAsync(replayed, cancellationToken);
            return new NavigationResult(
                replayed.Route!,
                new NavigationPlan(NavigationState.Empty),
                NavigationState.Empty,
                Presented: true);
        });
        var replayer = new DeferredNavigationRequestReplayer(store, navigator);

        DeferredNavigationReplayResult result = await replayer.ReplayAsync();

        Assert.Equal(new DeferredNavigationReplayResult(1, 1, 0), result);
        Assert.Equal([request], await SnapshotAsync(store));
    }

    [Fact]
    public async Task ReplayAsync_CancellationRequeuesFailedAndUnattemptedRequestsInOrder()
    {
        var store = new InMemoryDeferredNavigationRequestStore();
        var first = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("first"), NavigationRequestSource.AppLink);
        var second = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("second"), NavigationRequestSource.Push);
        var third = RouterNavigationRequest.FromRoute(new TestRoutes.StoreRoute("third"), NavigationRequestSource.Push);
        await store.EnqueueAsync(first);
        await store.EnqueueAsync(second);
        await store.EnqueueAsync(third);

        using var cancellationTokenSource = new CancellationTokenSource();
        var navigator = new RecordingRouterNavigator((request, cancellationToken) =>
        {
            if (Equals(request, first))
            {
                throw new InvalidOperationException("Replay failure.");
            }

            cancellationTokenSource.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new NavigationResult(
                request.Route!,
                new NavigationPlan(NavigationState.Empty),
                NavigationState.Empty,
                Presented: true));
        });
        var replayer = new DeferredNavigationRequestReplayer(store, navigator);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            replayer.ReplayAsync(cancellationTokenSource.Token).AsTask());

        Assert.Equal([first, second], navigator.Calls);
        Assert.Equal([first, second, third], await SnapshotAsync(store));
    }

    private static async Task<IReadOnlyList<RouterNavigationRequest>> SnapshotAsync(
        InMemoryDeferredNavigationRequestStore store)
    {
        await using IDeferredNavigationRequestLease lease = await store.AcquireReplayLeaseAsync();
        return lease.Requests.ToArray();
    }

    private sealed class RecordingRouterNavigator(
        Func<RouterNavigationRequest, CancellationToken, ValueTask<NavigationResult>>? navigate = null)
        : IRouterNavigator
    {
        private readonly Func<RouterNavigationRequest, CancellationToken, ValueTask<NavigationResult>> _navigate =
            navigate ?? ((request, _) => ValueTask.FromResult(new NavigationResult(
                request.Route!,
                new NavigationPlan(NavigationState.Empty),
                NavigationState.Empty,
                Presented: true)));

        public List<RouterNavigationRequest> Calls { get; } = [];

        public NavigationState CurrentState => NavigationState.Empty;

        public NavigationHistory History => NavigationHistory.Empty;

        public ValueTask<NavigationResult> NavigateAsync(
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return _navigate(request, cancellationToken);
        }

        public ValueTask<BackNavigationResult> BackAsync(BackNavigationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> ReconcileAsync(NavigationReconciliation reconciliation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask RefreshPresentationAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

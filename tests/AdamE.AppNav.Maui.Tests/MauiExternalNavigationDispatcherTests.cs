using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.History;
using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace AdamE.AppNav.Maui.Tests;

[Collection(ExternalNavigationBridgeTestCollection.Name)]
public sealed class MauiExternalNavigationDispatcherTests
{
    [Fact]
    public async Task DispatchBeforeReady_IsBufferedAndDispatchedAfterReadyAndForegrounded()
    {
        var request = RouterNavigationRequest.FromRoute(new TestRoute("first"), NavigationRequestSource.AppLink);
        var navigator = new RecordingRouterNavigator();
        var services = new ServiceCollection();
        services.AddSingleton(new NavigationDiagnostics());
        services.AddSingleton<IRouterNavigator>(navigator);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMauiExternalNavigationDispatcher>();
        var runtimeDispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();

        dispatcher.Dispatch(request);
        runtimeDispatcher.MarkReady();
        runtimeDispatcher.SetForegrounded(true);

        await WaitUntilAsync(() => navigator.Calls.Count == 1);

        Assert.Equal([request], navigator.Calls);
    }

    [Fact]
    public async Task DispatchWhileBackgrounded_WaitsForForegroundAndCollapsesDuplicates()
    {
        var request = RouterNavigationRequest.FromRoute(new TestRoute("first"), NavigationRequestSource.AppLink);
        var navigator = new RecordingRouterNavigator();
        var services = new ServiceCollection();
        services.AddSingleton(new NavigationDiagnostics());
        services.AddSingleton<IRouterNavigator>(navigator);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMauiExternalNavigationDispatcher>();
        var runtimeDispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        runtimeDispatcher.MarkReady();
        runtimeDispatcher.SetForegrounded(false);

        dispatcher.Dispatch(request);
        dispatcher.Dispatch(request with { Timestamp = request.Timestamp.AddMinutes(1) });
        await Task.Delay(150);
        Assert.Empty(navigator.Calls);

        runtimeDispatcher.SetForegrounded(true);
        await WaitUntilAsync(() => navigator.Calls.Count == 1);

        Assert.Equal([request], navigator.Calls);
    }

    [Fact]
    public async Task DispatchAppOwnedPushAndQrRequests_UsesPublicDispatcherAndPreservesProvenance()
    {
        var pushUri = new Uri("https://example.com/orders/ready");
        var qrUri = new Uri("https://example.com/products/123");
        var pushRequest = RouterNavigationRequest.FromUri(
            pushUri,
            NavigationRequestSource.Push,
            provenance: new NavigationRequestProvenance(
                provider: "firebase-push",
                originalUri: pushUri,
                correlationId: "notification-123",
                attributes: new Dictionary<string, string?>
                {
                    ["messageId"] = "message-456"
                }));
        var qrRequest = RouterNavigationRequest.FromUri(
            qrUri,
            NavigationRequestSource.QrCode,
            provenance: new NavigationRequestProvenance(
                provider: "qr-scanner",
                originalUri: qrUri,
                correlationId: "scan-789"));
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var navigator = new RecordingRouterNavigator();
        var services = new ServiceCollection();
        services.AddSingleton(diagnostics);
        services.AddSingleton<IRouterNavigator>(navigator);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMauiExternalNavigationDispatcher>();
        var runtimeDispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        runtimeDispatcher.MarkReady();
        runtimeDispatcher.SetForegrounded(true);

        dispatcher.Dispatch(pushRequest);
        dispatcher.Dispatch(qrRequest);

        await WaitUntilAsync(() => navigator.Calls.Count == 2);

        Assert.Equal([pushRequest, qrRequest], navigator.Calls);
        Assert.Contains(events, diagnosticEvent =>
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ProvenanceProvider, out var providerValue) &&
            Equals(providerValue, "firebase-push") &&
            !diagnosticEvent.Data.ContainsKey(NavigationDiagnosticDataKeys.ProvenanceCorrelationId) &&
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ProvenanceAttributes, out var attributesValue) &&
            attributesValue is IReadOnlyDictionary<string, string?> attributes &&
            attributes.TryGetValue("messageId", out var messageId) &&
            messageId is null);
        Assert.Contains(events, diagnosticEvent =>
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ProvenanceProvider, out var providerValue) &&
            Equals(providerValue, "qr-scanner") &&
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ProvenanceOriginalUri, out var originalUri) &&
            Equals(originalUri, "https://example.com"));
    }

    [Fact]
    public async Task DispatchWhileBackgrounded_IncludesProvenanceInDeduplication()
    {
        var uri = new Uri("https://example.com/orders/ready");
        var firstRequest = RouterNavigationRequest.FromUri(
            uri,
            NavigationRequestSource.Push,
            provenance: new NavigationRequestProvenance(
                provider: "firebase-push",
                correlationId: "notification-123"));
        var secondRequest = firstRequest with
        {
            Provenance = new NavigationRequestProvenance(
                provider: "firebase-push",
                correlationId: "notification-456")
        };
        var navigator = new RecordingRouterNavigator();
        var services = new ServiceCollection();
        services.AddSingleton(new NavigationDiagnostics());
        services.AddSingleton<IRouterNavigator>(navigator);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMauiExternalNavigationDispatcher>();
        var runtimeDispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        runtimeDispatcher.MarkReady();
        runtimeDispatcher.SetForegrounded(false);

        dispatcher.Dispatch(firstRequest);
        dispatcher.Dispatch(firstRequest with { Timestamp = firstRequest.Timestamp.AddMinutes(1) });
        dispatcher.Dispatch(secondRequest);
        await Task.Delay(150);
        Assert.Empty(navigator.Calls);

        runtimeDispatcher.SetForegrounded(true);
        await WaitUntilAsync(() => navigator.Calls.Count == 2);

        Assert.Equal([firstRequest, secondRequest], navigator.Calls);
    }

    [Fact]
    public async Task DispatchFailure_RemainsPendingAfterFailedAttempt()
    {
        var request = RouterNavigationRequest.FromRoute(new TestRoute("first"), NavigationRequestSource.AppLink);
        var navigator = new RecordingRouterNavigator((request, _) =>
            throw new InvalidOperationException("Dispatch failed."));
        var services = new ServiceCollection();
        services.AddSingleton(new NavigationDiagnostics());
        services.AddSingleton<IRouterNavigator>(navigator);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMauiExternalNavigationDispatcher>();
        var runtimeDispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        runtimeDispatcher.MarkReady();
        runtimeDispatcher.SetForegrounded(true);

        dispatcher.Dispatch(request);
        await WaitUntilAsync(() => navigator.Calls.Count == 1);
        await Task.Delay(150);

        Assert.Equal([request], navigator.Calls);
        Assert.True(runtimeDispatcher.HasPendingRequests);
    }

    [Fact]
    public async Task DispatchFailure_RetriesRetainedRequestOnLaterForegroundTrigger()
    {
        var request = RouterNavigationRequest.FromRoute(new TestRoute("first"), NavigationRequestSource.AppLink);
        var attempts = 0;
        var navigator = new RecordingRouterNavigator((request, _) =>
        {
            attempts++;
            return attempts == 1
                ? throw new InvalidOperationException("Dispatch failed.")
                : ValueTask.FromResult(new NavigationResult(
                    request.Route!,
                    new NavigationPlan(NavigationState.Empty),
                    NavigationState.Empty,
                    Presented: true));
        });
        var services = new ServiceCollection();
        services.AddSingleton(new NavigationDiagnostics());
        services.AddSingleton<IRouterNavigator>(navigator);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMauiExternalNavigationDispatcher>();
        var runtimeDispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        runtimeDispatcher.MarkReady();
        runtimeDispatcher.SetForegrounded(true);

        dispatcher.Dispatch(request);
        await WaitUntilAsync(() => navigator.Calls.Count == 1);
        Assert.True(runtimeDispatcher.HasPendingRequests);

        runtimeDispatcher.SetForegrounded(false);
        runtimeDispatcher.SetForegrounded(true);
        await WaitUntilAsync(() => navigator.Calls.Count == 2);

        Assert.Equal([request, request], navigator.Calls);
        Assert.False(runtimeDispatcher.HasPendingRequests);
    }

    [Fact]
    public async Task DispatchFailure_RetainedHeadRunsBeforeLaterDispatchedRequests()
    {
        var first = RouterNavigationRequest.FromRoute(new TestRoute("first"), NavigationRequestSource.AppLink);
        var second = RouterNavigationRequest.FromRoute(new TestRoute("second"), NavigationRequestSource.Push);
        var firstAttempts = 0;
        var navigator = new RecordingRouterNavigator((request, _) =>
        {
            if (Equals(request, first))
            {
                firstAttempts++;
                return firstAttempts == 1
                    ? throw new InvalidOperationException("Dispatch failed.")
                    : ValueTask.FromResult(new NavigationResult(
                        request.Route!,
                        new NavigationPlan(NavigationState.Empty),
                        NavigationState.Empty,
                        Presented: true));
            }

            return ValueTask.FromResult(new NavigationResult(
                request.Route!,
                new NavigationPlan(NavigationState.Empty),
                NavigationState.Empty,
                Presented: true));
        });
        var services = new ServiceCollection();
        services.AddSingleton(new NavigationDiagnostics());
        services.AddSingleton<IRouterNavigator>(navigator);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMauiExternalNavigationDispatcher>();
        var runtimeDispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        runtimeDispatcher.MarkReady();
        runtimeDispatcher.SetForegrounded(true);

        dispatcher.Dispatch(first);
        await WaitUntilAsync(() => navigator.Calls.Count == 1);
        await Task.Delay(150);

        Assert.Equal([first], navigator.Calls);
        Assert.True(runtimeDispatcher.HasPendingRequests);

        dispatcher.Dispatch(second);
        await WaitUntilAsync(() => navigator.Calls.Count == 3);

        Assert.Equal([first, first, second], navigator.Calls);
        Assert.False(runtimeDispatcher.HasPendingRequests);
    }

    [Fact]
    public async Task StaticSubmitBeforeHostCreation_IsDeliveredAfterRegistration()
    {
        var request = RouterNavigationRequest.FromRoute(new TestRoute("bootstrap"), NavigationRequestSource.AppLink);
        MauiExternalNavigationBridge.Submit(request);

        var navigator = new RecordingRouterNavigator();
        using var provider = CreateDispatcherProvider(navigator);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(true);

        await WaitUntilAsync(() => navigator.Calls.Count == 1);

        Assert.Equal([request], navigator.Calls);
    }

    [Fact]
    public async Task StaticSubmitAfterHostDisposal_IsDeliveredToReplacementHost()
    {
        var firstNavigator = new RecordingRouterNavigator();
        var firstProvider = CreateDispatcherProvider(firstNavigator);
        firstProvider.GetRequiredService<MauiExternalNavigationDispatcher>();
        firstProvider.Dispose();

        var request = RouterNavigationRequest.FromRoute(new TestRoute("replacement"), NavigationRequestSource.AppLink);
        MauiExternalNavigationBridge.Submit(request);

        var replacementNavigator = new RecordingRouterNavigator();
        using var replacementProvider = CreateDispatcherProvider(replacementNavigator);
        var replacement = replacementProvider.GetRequiredService<MauiExternalNavigationDispatcher>();
        replacement.MarkReady();
        replacement.SetForegrounded(true);

        await WaitUntilAsync(() => replacementNavigator.Calls.Count == 1);

        Assert.Empty(firstNavigator.Calls);
        Assert.Equal([request], replacementNavigator.Calls);
    }

    [Fact]
    public async Task DisposingOlderHost_DoesNotUnregisterNewerHost()
    {
        var olderProvider = CreateDispatcherProvider(new RecordingRouterNavigator());
        olderProvider.GetRequiredService<MauiExternalNavigationDispatcher>();

        var currentNavigator = new RecordingRouterNavigator();
        using var currentProvider = CreateDispatcherProvider(currentNavigator);
        var current = currentProvider.GetRequiredService<MauiExternalNavigationDispatcher>();
        current.MarkReady();
        current.SetForegrounded(true);

        olderProvider.Dispose();
        var request = RouterNavigationRequest.FromRoute(new TestRoute("current"), NavigationRequestSource.AppLink);
        MauiExternalNavigationBridge.Submit(request);

        await WaitUntilAsync(() => currentNavigator.Calls.Count == 1);

        Assert.Equal([request], currentNavigator.Calls);
    }

    [Fact]
    public async Task DisposingHost_DropsItsQueuedRequests()
    {
        var dropped = RouterNavigationRequest.FromRoute(new TestRoute("dropped"), NavigationRequestSource.AppLink);
        var oldNavigator = new RecordingRouterNavigator();
        var oldProvider = CreateDispatcherProvider(oldNavigator);
        var oldDispatcher = oldProvider.GetRequiredService<MauiExternalNavigationDispatcher>();
        oldDispatcher.MarkReady();
        oldDispatcher.SetForegrounded(false);
        oldDispatcher.Dispatch(dropped);
        Assert.True(oldDispatcher.HasPendingRequests);

        oldProvider.Dispose();

        var currentNavigator = new RecordingRouterNavigator();
        using var currentProvider = CreateDispatcherProvider(currentNavigator);
        var currentDispatcher = currentProvider.GetRequiredService<MauiExternalNavigationDispatcher>();
        currentDispatcher.MarkReady();
        currentDispatcher.SetForegrounded(true);
        await Task.Delay(100);
        Assert.Empty(currentNavigator.Calls);

        var current = RouterNavigationRequest.FromRoute(new TestRoute("current"), NavigationRequestSource.AppLink);
        MauiExternalNavigationBridge.Submit(current);
        await WaitUntilAsync(() => currentNavigator.Calls.Count == 1);

        Assert.Empty(oldNavigator.Calls);
        Assert.Equal([current], currentNavigator.Calls);
    }

    [Fact]
    public async Task DisposingHost_DropsRequestsRetainedAfterFailure()
    {
        var failed = RouterNavigationRequest.FromRoute(new TestRoute("failed"), NavigationRequestSource.AppLink);
        var oldNavigator = new RecordingRouterNavigator((_, _) =>
            throw new InvalidOperationException("Dispatch failed."));
        var oldProvider = CreateDispatcherProvider(oldNavigator);
        var oldDispatcher = oldProvider.GetRequiredService<MauiExternalNavigationDispatcher>();
        oldDispatcher.MarkReady();
        oldDispatcher.SetForegrounded(true);
        oldDispatcher.Dispatch(failed);
        await WaitUntilAsync(() => oldNavigator.Calls.Count == 1);
        Assert.True(oldDispatcher.HasPendingRequests);

        oldProvider.Dispose();

        var replacementNavigator = new RecordingRouterNavigator();
        using var replacementProvider = CreateDispatcherProvider(replacementNavigator);
        var replacement = replacementProvider.GetRequiredService<MauiExternalNavigationDispatcher>();
        replacement.MarkReady();
        replacement.SetForegrounded(true);

        Assert.False(replacement.HasPendingRequests);
        Assert.Empty(replacementNavigator.Calls);
    }

    [Fact]
    public async Task DisposingHost_CancelsInFlightDispatchWithoutFailureDiagnostic()
    {
        var navigationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var navigator = new RecordingRouterNavigator((_, cancellationToken) =>
        {
            navigationStarted.TrySetResult();
            return new ValueTask<NavigationResult>(WaitForCancellationAsync(cancellationToken, navigationCancelled));
        });
        var provider = CreateDispatcherProvider(navigator, diagnostics);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(true);
        dispatcher.Dispatch(RouterNavigationRequest.FromRoute(
            new TestRoute("in-flight"),
            NavigationRequestSource.AppLink));
        await navigationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        provider.Dispose();

        await navigationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.AppLinkFailed);

        var replacementNavigator = new RecordingRouterNavigator();
        using var replacementProvider = CreateDispatcherProvider(replacementNavigator);
        var replacement = replacementProvider.GetRequiredService<MauiExternalNavigationDispatcher>();
        replacement.MarkReady();
        replacement.SetForegrounded(true);

        Assert.False(replacement.HasPendingRequests);
        Assert.Empty(replacementNavigator.Calls);
    }

    [Fact]
    public async Task DisposingHost_ReleasesPendingWaiters()
    {
        var provider = CreateDispatcherProvider(new RecordingRouterNavigator());
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        Task<bool> waiting = dispatcher
            .WaitForPendingRequestAsync(TimeSpan.FromMinutes(1))
            .AsTask();

        provider.Dispose();

        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await dispatcher.WaitForPendingRequestAsync(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void DisposedDispatcher_RejectsDirectDispatchAndAllowsLifecycleCleanup()
    {
        var provider = CreateDispatcherProvider(new RecordingRouterNavigator());
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        provider.Dispose();

        dispatcher.Dispatch(null);
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(true);
        dispatcher.Dispose();

        Assert.False(dispatcher.HasPendingRequests);
        Assert.Throws<ObjectDisposedException>(() => dispatcher.Dispatch(
            RouterNavigationRequest.FromRoute(new TestRoute("disposed"), NavigationRequestSource.AppLink)));
    }

    [Fact]
    public void RepeatedHostDisposal_ReleasesProvidersAndDispatchers()
    {
        HostWeakReferences[] hosts = Enumerable.Range(0, 32)
            .Select(_ => CreateAndDisposeHost())
            .ToArray();

        ForceFullCollection();

        Assert.All(hosts, host =>
        {
            Assert.False(host.Provider.IsAlive);
            Assert.False(host.Dispatcher.IsAlive);
            Assert.False(host.Navigator.IsAlive);
        });
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var started = Environment.TickCount64;
        while (!predicate())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException("Condition was not satisfied.");
            }

            await Task.Delay(20);
        }
    }

    private static ServiceProvider CreateDispatcherProvider(
        RecordingRouterNavigator navigator,
        NavigationDiagnostics? diagnostics = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(diagnostics ?? new NavigationDiagnostics());
        services.AddSingleton<IRouterNavigator>(navigator);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());
        return services.BuildServiceProvider();
    }

    private static async Task<NavigationResult> WaitForCancellationAsync(
        CancellationToken cancellationToken,
        TaskCompletionSource navigationCancelled)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The navigation dispatch was not cancelled.");
        }
        catch (OperationCanceledException)
        {
            navigationCancelled.TrySetResult();
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static HostWeakReferences CreateAndDisposeHost()
    {
        var navigator = new RecordingRouterNavigator();
        ServiceProvider provider = CreateDispatcherProvider(navigator);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        var references = new HostWeakReferences(
            new WeakReference(provider),
            new WeakReference(dispatcher),
            new WeakReference(navigator));

        provider.Dispose();
        return references;
    }

    private static void ForceFullCollection()
    {
        for (var i = 0; i < 4; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed record HostWeakReferences(
        WeakReference Provider,
        WeakReference Dispatcher,
        WeakReference Navigator);

    private sealed record TestRoute(string Id) : AppRoute;

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

        public ValueTask<NavigationResult> NavigateAsync(Uri uri, NavigationRequestSource source = NavigationRequestSource.InAppCommand, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(Uri uri, NavigationRequestSource source, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(Uri uri, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRoute route, NavigationRequestSource source = NavigationRequestSource.InAppCommand, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRoute route, NavigationRequestSource source, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRoute route, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRouteRequest routeRequest, NavigationRequestSource source = NavigationRequestSource.InAppCommand, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRouteRequest routeRequest, NavigationRequestSource source, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRouteRequest routeRequest, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<NavigationResult> NavigateAsync(
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return _navigate(request, cancellationToken);
        }

        public ValueTask<BackNavigationResult> BackAsync(string? windowId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> ReconcileAsync(NavigationReconciliation reconciliation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

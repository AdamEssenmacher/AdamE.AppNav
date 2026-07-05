using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Maui.AppLinks;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;
using Microsoft.Extensions.DependencyInjection;

namespace AdamE.MauiRouter.Maui.Tests;

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
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ProvenanceCorrelationId, out var correlationId) &&
            Equals(correlationId, "notification-123") &&
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ProvenanceAttributes, out var attributesValue) &&
            attributesValue is IReadOnlyDictionary<string, string?> attributes &&
            attributes.TryGetValue("messageId", out var messageId) &&
            messageId == "message-456");
        Assert.Contains(events, diagnosticEvent =>
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ProvenanceProvider, out var providerValue) &&
            Equals(providerValue, "qr-scanner") &&
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ProvenanceOriginalUri, out var originalUri) &&
            Equals(originalUri, qrUri.ToString()));
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
    }
}

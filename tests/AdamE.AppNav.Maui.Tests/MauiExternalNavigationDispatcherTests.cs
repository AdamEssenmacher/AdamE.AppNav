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

        Assert.True(dispatcher.TryDispatch(request));
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

        Assert.True(dispatcher.TryDispatch(request));
        Assert.False(dispatcher.TryDispatch(request with { Timestamp = request.Timestamp.AddMinutes(1) }));
        await Task.Delay(150);
        Assert.Empty(navigator.Calls);

        runtimeDispatcher.SetForegrounded(true);
        await WaitUntilAsync(() => navigator.Calls.Count == 1);

        Assert.Equal([request], navigator.Calls);
    }

    [Fact]
    public async Task DispatchAppOwnedPushAndQrRequests_PreservesRequestsWithoutLoggingProvenance()
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
        services.AddSingleton(TrustedOptions());
        services.AddSingleton<IRouterNavigator>(navigator);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMauiExternalNavigationDispatcher>();
        var runtimeDispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        runtimeDispatcher.MarkReady();
        runtimeDispatcher.SetForegrounded(true);

        Assert.True(dispatcher.TryDispatch(pushRequest));
        Assert.True(dispatcher.TryDispatch(qrRequest));

        await WaitUntilAsync(() => navigator.Calls.Count == 2);

        Assert.Equal([pushRequest, qrRequest], navigator.Calls);
        Assert.NotEmpty(events);
        Assert.All(events, diagnosticEvent =>
        {
            Assert.False(diagnosticEvent.Data.ContainsKey(NavigationDiagnosticDataKeys.ProvenanceProvider));
            Assert.False(diagnosticEvent.Data.ContainsKey(NavigationDiagnosticDataKeys.ProvenanceCorrelationId));
            Assert.False(diagnosticEvent.Data.ContainsKey(NavigationDiagnosticDataKeys.ProvenanceAttributes));
            Assert.False(diagnosticEvent.Data.ContainsKey(NavigationDiagnosticDataKeys.ProvenanceOriginalUri));
        });
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
        services.AddSingleton(TrustedOptions());
        services.AddSingleton<IRouterNavigator>(navigator);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<IMauiExternalNavigationDispatcher>();
        var runtimeDispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        runtimeDispatcher.MarkReady();
        runtimeDispatcher.SetForegrounded(false);

        Assert.True(dispatcher.TryDispatch(firstRequest));
        Assert.False(dispatcher.TryDispatch(firstRequest with { Timestamp = firstRequest.Timestamp.AddMinutes(1) }));
        Assert.True(dispatcher.TryDispatch(secondRequest));
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
            throw new TimeoutException("Dispatch failed."));
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

        Assert.True(dispatcher.TryDispatch(request));
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
                ? throw new TimeoutException("Dispatch failed.")
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

        Assert.True(dispatcher.TryDispatch(request));
        await WaitUntilAsync(() => navigator.Calls.Count == 1);
        Assert.True(runtimeDispatcher.HasPendingRequests);

        runtimeDispatcher.SetForegrounded(false);
        runtimeDispatcher.SetForegrounded(true);
        await WaitUntilAsync(() => navigator.Calls.Count == 2);

        Assert.Equal([request, request], navigator.Calls);
        Assert.False(runtimeDispatcher.HasPendingRequests);
    }

    [Fact]
    public async Task DispatchFailure_RequeuesAtTailSoLaterRequestsContinue()
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
                    ? throw new TimeoutException("Dispatch failed.")
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

        Assert.True(dispatcher.TryDispatch(first));
        await WaitUntilAsync(() => navigator.Calls.Count == 1);
        await Task.Delay(150);

        Assert.Equal([first], navigator.Calls);
        Assert.True(runtimeDispatcher.HasPendingRequests);

        Assert.True(dispatcher.TryDispatch(second));
        await WaitUntilAsync(() => navigator.Calls.Count == 3);

        Assert.Equal([first, second, first], navigator.Calls);
        Assert.False(runtimeDispatcher.HasPendingRequests);
    }

    [Fact]
    public async Task RuntimeQueueOverflow_DropsOldestAndPreservesNewerIntent()
    {
        var first = RouterNavigationRequest.FromRoute(new TestRoute("first"), NavigationRequestSource.AppLink);
        var second = RouterNavigationRequest.FromRoute(new TestRoute("second"), NavigationRequestSource.AppLink);
        var third = RouterNavigationRequest.FromRoute(new TestRoute("third"), NavigationRequestSource.AppLink);
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var options = new MauiExternalNavigationOptions { MaximumPendingRequests = 2 };
        var navigator = new RecordingRouterNavigator();
        using ServiceProvider provider = CreateDispatcherProvider(navigator, diagnostics, options);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(false);

        Assert.True(dispatcher.TryDispatch(first));
        Assert.True(dispatcher.TryDispatch(second));
        Assert.True(dispatcher.TryDispatch(third));

        dispatcher.SetForegrounded(true);
        await WaitUntilAsync(() => navigator.Calls.Count == 2);

        Assert.Equal([second, third], navigator.Calls);
        NavigationDiagnosticEvent overflow = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationOverflowed);
        Assert.Equal("PendingLimit", overflow.Data[NavigationDiagnosticDataKeys.ExternalNavigationReason]);
        Assert.False(overflow.Data.ContainsKey(NavigationDiagnosticDataKeys.Uri));
    }

    [Fact]
    public async Task RuntimeQueueBound_AppliesToPendingRequestsBesideInFlightDispatch()
    {
        var first = RouterNavigationRequest.FromRoute(new TestRoute("first"), NavigationRequestSource.AppLink);
        var second = RouterNavigationRequest.FromRoute(new TestRoute("second"), NavigationRequestSource.AppLink);
        var third = RouterNavigationRequest.FromRoute(new TestRoute("third"), NavigationRequestSource.AppLink);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var navigator = new RecordingRouterNavigator(async (request, cancellationToken) =>
        {
            if (Equals(request, first))
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return await SuccessfulResult(request);
        });
        var options = new MauiExternalNavigationOptions { MaximumPendingRequests = 2 };
        using ServiceProvider provider = CreateDispatcherProvider(navigator, diagnostics, options);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(true);

        Assert.True(dispatcher.TryDispatch(first));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(dispatcher.TryDispatch(second));
        Assert.True(dispatcher.TryDispatch(third));
        releaseFirst.TrySetResult();

        await WaitUntilAsync(() => navigator.Calls.Count == 3);
        Assert.Equal([first, second, third], navigator.Calls);
        Assert.DoesNotContain(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationOverflowed);
    }

    [Fact]
    public async Task RuntimeQueueLimitOne_RetainsNewestIntentWhileOlderRequestIsInFlight()
    {
        var first = RouterNavigationRequest.FromRoute(new TestRoute("first"), NavigationRequestSource.AppLink);
        var second = RouterNavigationRequest.FromRoute(new TestRoute("second"), NavigationRequestSource.AppLink);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigator = new RecordingRouterNavigator(async (request, cancellationToken) =>
        {
            if (Equals(request, first))
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return await SuccessfulResult(request);
        });
        var options = new MauiExternalNavigationOptions { MaximumPendingRequests = 1 };
        using ServiceProvider provider = CreateDispatcherProvider(navigator, options: options);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(true);

        Assert.True(dispatcher.TryDispatch(first));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(dispatcher.TryDispatch(second));
        releaseFirst.TrySetResult();

        await WaitUntilAsync(() => navigator.Calls.Count == 2);
        Assert.Equal([first, second], navigator.Calls);
    }

    [Fact]
    public async Task BootstrapQueueOverflow_DropsOldestAndPreservesNewerIntent()
    {
        var options = new MauiExternalNavigationOptions { MaximumPendingRequests = 2 };
        var first = RouterNavigationRequest.FromRoute(new TestRoute("bootstrap-first"), NavigationRequestSource.AppLink);
        var second = RouterNavigationRequest.FromRoute(new TestRoute("bootstrap-second"), NavigationRequestSource.AppLink);
        var third = RouterNavigationRequest.FromRoute(new TestRoute("bootstrap-third"), NavigationRequestSource.AppLink);

        Assert.True(MauiExternalNavigationBridge.Submit(first, options));
        Assert.True(MauiExternalNavigationBridge.Submit(second, options));
        Assert.True(MauiExternalNavigationBridge.Submit(third, options));

        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var navigator = new RecordingRouterNavigator();
        using ServiceProvider provider = CreateDispatcherProvider(navigator, diagnostics, options);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(true);
        await WaitUntilAsync(() => navigator.Calls.Count == 2);

        Assert.Equal([second, third], navigator.Calls);
        NavigationDiagnosticEvent overflow = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationOverflowed);
        Assert.Equal("PendingLimit", overflow.Data[NavigationDiagnosticDataKeys.ExternalNavigationReason]);
        Assert.False(overflow.Data.ContainsKey(NavigationDiagnosticDataKeys.Uri));
    }

    [Fact]
    public async Task BootstrapRequest_IsRevalidatedAgainstRegisteredDispatcherOptions()
    {
        var request = RouterNavigationRequest.FromUri(
            new Uri("https://bootstrap.example/detail"),
            NavigationRequestSource.AppLink);
        var bootstrapOptions = new MauiExternalNavigationOptions()
            .AllowOrigin(new Uri("https://bootstrap.example"));
        Assert.True(MauiExternalNavigationBridge.Submit(request, bootstrapOptions));

        var runtimeOptions = new MauiExternalNavigationOptions()
            .AllowOrigin(new Uri("https://runtime.example"));
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var navigator = new RecordingRouterNavigator();
        using ServiceProvider provider = CreateDispatcherProvider(navigator, diagnostics, runtimeOptions);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(true);

        await Task.Delay(100);

        Assert.Empty(navigator.Calls);
        Assert.False(dispatcher.HasPendingRequests);
        NavigationDiagnosticEvent rejected = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationRejected);
        Assert.Equal("OriginNotAllowed", rejected.Data[NavigationDiagnosticDataKeys.ExternalNavigationReason]);
    }

    [Fact]
    public void BootstrapRejectionAndDeduplicationAreReportedStructurally()
    {
        var options = new MauiExternalNavigationOptions().AllowOrigin(new Uri("https://example.com"));
        var accepted = RouterNavigationRequest.FromUri(
            new Uri("https://example.com/detail?id=secret"),
            NavigationRequestSource.AppLink);
        var rejected = RouterNavigationRequest.FromUri(
            new Uri("https://untrusted.example/detail?id=secret"),
            NavigationRequestSource.AppLink);

        Assert.True(MauiExternalNavigationBridge.Submit(accepted, options));
        Assert.False(MauiExternalNavigationBridge.Submit(accepted, options));
        Assert.False(MauiExternalNavigationBridge.Submit(rejected, options));

        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        using ServiceProvider provider = CreateDispatcherProvider(
            new RecordingRouterNavigator(),
            diagnostics,
            options);
        _ = provider.GetRequiredService<MauiExternalNavigationDispatcher>();

        Assert.Contains(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationDeduplicated);
        NavigationDiagnosticEvent rejection = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationRejected);
        Assert.Equal("OriginNotAllowed", rejection.Data[NavigationDiagnosticDataKeys.ExternalNavigationReason]);
        Assert.All(events, diagnosticEvent => Assert.DoesNotContain(
            diagnosticEvent.Data.Values,
            value => value?.ToString()?.Contains("secret", StringComparison.Ordinal) == true));
        Assert.All(events, diagnosticEvent => Assert.DoesNotContain(
            NavigationDiagnosticDataKeys.Uri,
            diagnosticEvent.Data.Keys));
    }

    [Fact]
    public async Task TerminalFailure_DropsPoisonRequestWithoutBlockingNextRequest()
    {
        var poison = RouterNavigationRequest.FromRoute(new TestRoute("poison"), NavigationRequestSource.AppLink);
        var healthy = RouterNavigationRequest.FromRoute(new TestRoute("healthy"), NavigationRequestSource.AppLink);
        var navigator = new RecordingRouterNavigator((request, _) =>
            request == poison
                ? throw new NotSupportedException("Terminal request.")
                : SuccessfulResult(request));
        using ServiceProvider provider = CreateDispatcherProvider(navigator);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(false);

        Assert.True(dispatcher.TryDispatch(poison));
        Assert.True(dispatcher.TryDispatch(healthy));
        dispatcher.SetForegrounded(true);
        await WaitUntilAsync(() => navigator.Calls.Count == 2);

        Assert.Equal([poison, healthy], navigator.Calls);
        Assert.False(dispatcher.HasPendingRequests);
    }

    [Fact]
    public async Task UnknownFailure_RetriesOnlyToConfiguredAttemptLimit()
    {
        var request = RouterNavigationRequest.FromRoute(new TestRoute("retry"), NavigationRequestSource.AppLink);
        var options = new MauiExternalNavigationOptions
        {
            MaximumDispatchAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(10)
        };
        var navigator = new RecordingRouterNavigator((_, _) =>
            throw new InvalidOperationException("Retryable request."));
        using ServiceProvider provider = CreateDispatcherProvider(navigator, options: options);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(true);

        Assert.True(dispatcher.TryDispatch(request));
        await WaitUntilAsync(() => navigator.Calls.Count == 3);
        await WaitUntilAsync(() => !dispatcher.HasPendingRequests);

        Assert.Equal([request, request, request], navigator.Calls);
    }

    [Fact]
    public async Task LifecycleCancellation_PreservesRequestWithoutConsumingAttempt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var options = new MauiExternalNavigationOptions { MaximumDispatchAttempts = 1 };
        var navigator = new RecordingRouterNavigator((request, cancellationToken) =>
        {
            attempts++;
            if (attempts == 1)
            {
                started.TrySetResult();
                return new ValueTask<NavigationResult>(WaitForCancellationAsync(cancellationToken, cancelled));
            }

            return SuccessfulResult(request);
        });
        using ServiceProvider provider = CreateDispatcherProvider(navigator, options: options);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(true);

        Assert.True(dispatcher.TryDispatch(RouterNavigationRequest.FromRoute(
            new TestRoute("lifecycle"),
            NavigationRequestSource.AppLink)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        dispatcher.SetForegrounded(false);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(dispatcher.HasPendingRequests);

        dispatcher.SetForegrounded(true);
        await WaitUntilAsync(() => navigator.Calls.Count == 2);

        Assert.Equal(2, attempts);
        Assert.False(dispatcher.HasPendingRequests);
    }

    [Fact]
    public async Task LifecycleCancellation_PreservesInFlightRequestWithoutOverfillingPendingQueue()
    {
        var first = RouterNavigationRequest.FromRoute(new TestRoute("first"), NavigationRequestSource.AppLink);
        var second = RouterNavigationRequest.FromRoute(new TestRoute("second"), NavigationRequestSource.AppLink);
        var third = RouterNavigationRequest.FromRoute(new TestRoute("third"), NavigationRequestSource.AppLink);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttempts = 0;
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var navigator = new RecordingRouterNavigator((request, cancellationToken) =>
        {
            if (!Equals(request, first) || ++firstAttempts > 1)
                return SuccessfulResult(request);

            firstStarted.TrySetResult();
            return new ValueTask<NavigationResult>(WaitForCancellationAsync(cancellationToken, firstCancelled));
        });
        var options = new MauiExternalNavigationOptions { MaximumPendingRequests = 2 };
        using ServiceProvider provider = CreateDispatcherProvider(navigator, diagnostics, options);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(true);

        Assert.True(dispatcher.TryDispatch(first));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(dispatcher.TryDispatch(second));
        Assert.True(dispatcher.TryDispatch(third));
        dispatcher.SetForegrounded(false);
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        dispatcher.SetForegrounded(true);
        await WaitUntilAsync(() => navigator.Calls.Count == 3);

        Assert.Equal([first, first, third], navigator.Calls);
        Assert.False(dispatcher.HasPendingRequests);
        NavigationDiagnosticEvent overflow = Assert.Single(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationOverflowed);
        Assert.Equal("PendingLimit", overflow.Data[NavigationDiagnosticDataKeys.ExternalNavigationReason]);
    }

    [Fact]
    public void ExpiredRequest_IsRejectedBeforeQueueing()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var options = new MauiExternalNavigationOptions { MaximumRequestAge = TimeSpan.FromMinutes(1) };
        using ServiceProvider provider = CreateDispatcherProvider(
            new RecordingRouterNavigator(),
            diagnostics,
            options);
        var dispatcher = provider.GetRequiredService<MauiExternalNavigationDispatcher>();
        RouterNavigationRequest expired = RouterNavigationRequest.FromRoute(
            new TestRoute("expired"),
            NavigationRequestSource.AppLink) with
        {
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-2)
        };

        Assert.False(dispatcher.TryDispatch(expired));
        Assert.False(dispatcher.HasPendingRequests);
        Assert.Contains(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.ExternalNavigationExpired);
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
        Assert.True(oldDispatcher.TryDispatch(dropped));
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
            throw new TimeoutException("Dispatch failed."));
        var oldProvider = CreateDispatcherProvider(oldNavigator);
        var oldDispatcher = oldProvider.GetRequiredService<MauiExternalNavigationDispatcher>();
        oldDispatcher.MarkReady();
        oldDispatcher.SetForegrounded(true);
        Assert.True(oldDispatcher.TryDispatch(failed));
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
        Assert.True(dispatcher.TryDispatch(RouterNavigationRequest.FromRoute(
            new TestRoute("in-flight"),
            NavigationRequestSource.AppLink)));
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

        Assert.False(dispatcher.TryDispatch(null));
        dispatcher.MarkReady();
        dispatcher.SetForegrounded(true);
        dispatcher.Dispose();

        Assert.False(dispatcher.HasPendingRequests);
        Assert.False(dispatcher.TryDispatch(
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
        NavigationDiagnostics? diagnostics = null,
        MauiExternalNavigationOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(diagnostics ?? new NavigationDiagnostics());
        if (options is not null)
            services.AddSingleton(options);
        services.AddSingleton<IRouterNavigator>(navigator);
        services.AddSingleton<MauiExternalNavigationDispatcher>();
        services.AddSingleton<IMauiExternalNavigationDispatcher>(provider =>
            provider.GetRequiredService<MauiExternalNavigationDispatcher>());
        return services.BuildServiceProvider();
    }

    private static MauiExternalNavigationOptions TrustedOptions()
    {
        return new MauiExternalNavigationOptions()
            .AllowOrigin(new Uri("https://example.com"));
    }

    private static ValueTask<NavigationResult> SuccessfulResult(RouterNavigationRequest request)
    {
        return ValueTask.FromResult(new NavigationResult(
            request.Route!,
            new NavigationPlan(NavigationState.Empty),
            NavigationState.Empty,
            Presented: true));
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

using System.Reflection;
using System.Text.Json;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Maui.AppLinks;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui.Tests;

public sealed class MauiRouterStartupServiceTests
{
    [Fact]
    public async Task StartAsync_FallbackNavigation_UsesInjectedRouterNavigator()
    {
        var navigator = new RecordingRouterNavigator();
        var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(new NavigationDiagnostics())
            .BuildServiceProvider();
        var dispatcher = new MauiExternalNavigationDispatcher(
            services,
            services.GetRequiredService<NavigationDiagnostics>());
        var windowAttachment = new RecordingWindowAttachment();
        var startup = new MauiRouterStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new MauiRouterStartupOptions
            {
                AppLinkGracePeriod = TimeSpan.Zero,
                RestoreFromStore = false,
                FallbackRequestFactory = static (_, _) => ValueTask.FromResult<RouterNavigationRequest?>(
                    RouterNavigationRequest.FromRoute(
                        new TestRoute("fallback"),
                        NavigationRequestSource.InAppCommand))
            },
            services,
            services.GetRequiredService<NavigationDiagnostics>());

        var result = await StartOnMainThreadAsync(startup, new Window(new ContentPage()));

        Assert.Equal(MauiRouterStartupOutcome.FallbackNavigated, result.Outcome);
        Assert.Single(navigator.NavigateCalls);
        Assert.Equal("fallback", Assert.IsType<TestRoute>(navigator.NavigateCalls[0].Route).Id);
        Assert.Equal(1, windowAttachment.AttachCalls);
    }

    [Fact]
    public async Task StartAsync_InvalidDeferredRequestStore_ClearsStoreAndRunsFallback()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var navigator = new RecordingRouterNavigator();
        var deferredStore = new RecordingDeferredRequestStore
        {
            HasDeferredRequestsException = new JsonException("Deferred request JSON was corrupt.")
        };
        var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(diagnostics)
            .AddSingleton<IDeferredNavigationRequestStore>(deferredStore)
            .BuildServiceProvider();
        var dispatcher = new MauiExternalNavigationDispatcher(
            services,
            diagnostics);
        var windowAttachment = new RecordingWindowAttachment();
        var startup = new MauiRouterStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new MauiRouterStartupOptions
            {
                AppLinkGracePeriod = TimeSpan.Zero,
                RestoreFromStore = false,
                FallbackRequestFactory = static (_, _) => ValueTask.FromResult<RouterNavigationRequest?>(
                    RouterNavigationRequest.FromRoute(
                        new TestRoute("fallback"),
                        NavigationRequestSource.InAppCommand))
            },
            services,
            diagnostics);

        var result = await StartOnMainThreadAsync(startup, new Window(new ContentPage()));

        Assert.Equal(MauiRouterStartupOutcome.FallbackNavigated, result.Outcome);
        Assert.Null(result.Exception);
        Assert.Equal(1, deferredStore.HasDeferredRequestsCalls);
        Assert.Equal(1, deferredStore.ClearCalls);
        Assert.Single(navigator.NavigateCalls);
        Assert.DoesNotContain(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.StartupDeferredRequestPending);
        Assert.Contains(events, diagnosticEvent =>
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.RestoreReason, out var restoreReason) &&
            Equals(restoreReason, "deferred-request-store-invalid") &&
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ExceptionType, out var exceptionType) &&
            Equals(exceptionType, typeof(JsonException).FullName));
    }

    [Fact]
    public async Task StartAsync_InvalidDeferredRequestStoreClearFailure_ReturnsFailed()
    {
        var navigator = new RecordingRouterNavigator();
        var clearException = new IOException("Clear failed.");
        var deferredStore = new RecordingDeferredRequestStore
        {
            HasDeferredRequestsException = new JsonException("Deferred request JSON was corrupt."),
            ClearException = clearException
        };
        var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(new NavigationDiagnostics())
            .AddSingleton<IDeferredNavigationRequestStore>(deferredStore)
            .BuildServiceProvider();
        var dispatcher = new MauiExternalNavigationDispatcher(
            services,
            services.GetRequiredService<NavigationDiagnostics>());
        var windowAttachment = new RecordingWindowAttachment();
        var startup = new MauiRouterStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new MauiRouterStartupOptions
            {
                AppLinkGracePeriod = TimeSpan.Zero,
                RestoreFromStore = false,
                FallbackRequestFactory = static (_, _) => ValueTask.FromResult<RouterNavigationRequest?>(
                    RouterNavigationRequest.FromRoute(
                        new TestRoute("fallback"),
                        NavigationRequestSource.InAppCommand))
            },
            services,
            services.GetRequiredService<NavigationDiagnostics>());

        var result = await StartOnMainThreadAsync(startup, new Window(new ContentPage()));

        var exception = Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.Equal(MauiRouterStartupOutcome.Failed, result.Outcome);
        Assert.Same(clearException, exception.InnerException);
        Assert.Equal(1, deferredStore.HasDeferredRequestsCalls);
        Assert.Equal(1, deferredStore.ClearCalls);
        Assert.Empty(navigator.NavigateCalls);
    }

    [Fact]
    public async Task StartAsync_RestoreFromStore_UsesInjectedRouterNavigator()
    {
        var restoreState = new NavigationState(
            [
                new WindowNode(
                    "main",
                    new StackNode(
                        "main-stack",
                        [new RouteEntry("tests:restore", new TestRoute("restore"))]))
            ],
            "main");
        var navigator = new RecordingRouterNavigator
        {
            RestoreFromStoreResult = NavigationRestoreResult.AcceptedResult(
                restoreState,
                NavigationHistory.Empty,
                presented: false)
        };
        var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(new NavigationDiagnostics())
            .BuildServiceProvider();
        var dispatcher = new MauiExternalNavigationDispatcher(
            services,
            services.GetRequiredService<NavigationDiagnostics>());
        var windowAttachment = new RecordingWindowAttachment();
        var startup = new MauiRouterStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new MauiRouterStartupOptions
            {
                AppLinkGracePeriod = TimeSpan.Zero,
                RestoreFromStore = true
            },
            services,
            services.GetRequiredService<NavigationDiagnostics>());

        var result = await StartOnMainThreadAsync(startup, new Window(new ContentPage()));

        Assert.Equal(MauiRouterStartupOutcome.Restored, result.Outcome);
        Assert.Equal(1, navigator.RestoreFromStoreCalls);
        Assert.Empty(navigator.NavigateCalls);
        Assert.Equal(1, windowAttachment.AttachCalls);
    }

    private static Task<MauiRouterStartupResult> StartOnMainThreadAsync(
        MauiRouterStartupService startup,
        Window window)
    {
        var method = typeof(MauiRouterStartupService).GetMethod(
            "StartOnMainThreadAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(
            startup,
            [window, "main", CancellationToken.None]);

        return Assert.IsType<Task<MauiRouterStartupResult>>(task);
    }

    private sealed record TestRoute(string Id) : AppRoute;

    private sealed class RecordingWindowAttachment : IMauiWindowAttachment
    {
        public int AttachCalls { get; private set; }

        public void AttachWindow(Window window, string windowId)
        {
            Assert.NotNull(window);
            Assert.Equal("main", windowId);
            AttachCalls++;
        }
    }

    private sealed class RecordingDeferredRequestStore : IDeferredNavigationRequestStore
    {
        public Exception? HasDeferredRequestsException { get; init; }

        public Exception? ClearException { get; init; }

        public int HasDeferredRequestsCalls { get; private set; }

        public int ClearCalls { get; private set; }

        public ValueTask<bool> HasDeferredRequestsAsync(CancellationToken cancellationToken = default)
        {
            HasDeferredRequestsCalls++;
            if (HasDeferredRequestsException is not null)
            {
                throw HasDeferredRequestsException;
            }

            return ValueTask.FromResult(false);
        }

        public ValueTask EnqueueAsync(
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<RouterNavigationRequest?> TryDequeueAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<IReadOnlyList<RouterNavigationRequest>> DrainAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            ClearCalls++;
            if (ClearException is not null)
            {
                throw ClearException;
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRouterNavigator : IRouterNavigator
    {
        public List<RouterNavigationRequest> NavigateCalls { get; } = [];

        public int RestoreFromStoreCalls { get; private set; }

        public NavigationRestoreResult RestoreFromStoreResult { get; init; } =
            NavigationRestoreResult.Rejected("not-configured");

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
            NavigateCalls.Add(request);
            return ValueTask.FromResult(new NavigationResult(
                request.Route!,
                new NavigationPlan(NavigationState.Empty),
                NavigationState.Empty,
                Presented: true));
        }

        public ValueTask<BackNavigationResult> BackAsync(string? windowId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> ReconcileAsync(NavigationReconciliation reconciliation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationRestoreResult> RestoreAsync(NavigationSnapshot snapshot, NavigationRestoreOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<NavigationRestoreResult> RestoreFromStoreAsync(
            NavigationRestoreOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            RestoreFromStoreCalls++;
            return ValueTask.FromResult(RestoreFromStoreResult);
        }

        public Task WhenReconciliationIdleAsync() => Task.CompletedTask;
    }
}

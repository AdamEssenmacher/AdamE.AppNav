using System.Reflection;
using System.Text.Json;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Maui.AppLinks;
using AdamE.MauiRouter.Maui.Requests;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui.Tests;

public sealed class MauiRouterStartupServiceTests
{
    private static readonly Uri BaseUri = new("https://example.com/");

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
    public async Task StartAsync_PendingAppLink_SkipsFallbackAndAttachesWindow()
    {
        var diagnostics = new NavigationDiagnostics();
        var navigator = new RecordingRouterNavigator();
        var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(diagnostics)
            .BuildServiceProvider();
        var dispatcher = new MauiExternalNavigationDispatcher(services, diagnostics);
        dispatcher.Dispatch(RouterNavigationRequest.FromRoute(new TestRoute("pending"), NavigationRequestSource.AppLink));

        var windowAttachment = new RecordingWindowAttachment();
        var startup = new MauiRouterStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new MauiRouterStartupOptions
            {
                AppLinkGracePeriod = TimeSpan.Zero,
                FallbackRequestFactory = static (_, _) => ValueTask.FromResult<RouterNavigationRequest?>(
                    RouterNavigationRequest.FromRoute(
                        new TestRoute("fallback"),
                        NavigationRequestSource.InAppCommand))
            },
            services,
            diagnostics);

        var result = await StartOnMainThreadAsync(startup, new Window(new ContentPage()));

        Assert.Equal(MauiRouterStartupOutcome.AppLinkPending, result.Outcome);
        Assert.Empty(navigator.NavigateCalls);
        Assert.Equal(1, windowAttachment.AttachCalls);
        Assert.True(dispatcher.HasPendingRequests);
    }

    [Fact]
    public async Task StartAsync_DeferredRequestsPending_WritesDiagnosticAndRunsFallback()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var navigator = new RecordingRouterNavigator();
        var deferredStore = new RecordingDeferredRequestStore { HasDeferredRequestsResult = true };
        var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(diagnostics)
            .AddSingleton<IDeferredNavigationRequestStore>(deferredStore)
            .BuildServiceProvider();
        var dispatcher = new MauiExternalNavigationDispatcher(services, diagnostics);
        var windowAttachment = new RecordingWindowAttachment();
        var startup = new MauiRouterStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new MauiRouterStartupOptions
            {
                AppLinkGracePeriod = TimeSpan.Zero,
                FallbackRequestFactory = static (_, _) => ValueTask.FromResult<RouterNavigationRequest?>(
                    RouterNavigationRequest.FromRoute(
                        new TestRoute("fallback"),
                        NavigationRequestSource.InAppCommand))
            },
            services,
            diagnostics);

        var result = await StartOnMainThreadAsync(startup, new Window(new ContentPage()));

        Assert.Equal(MauiRouterStartupOutcome.FallbackNavigated, result.Outcome);
        Assert.Equal(1, deferredStore.HasDeferredRequestsCalls);
        Assert.Single(navigator.NavigateCalls);
        Assert.Contains(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.StartupDeferredRequestPending &&
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.StartupDeferredRequestPending, out var pending) &&
            Equals(pending, true));
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
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.StartupDeferredRequestPending, out var pending) &&
            Equals(pending, false) &&
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ExceptionType, out var exceptionType) &&
            Equals(exceptionType, typeof(JsonException).FullName));
    }

    [Fact]
    public async Task StartAsync_InvalidFileDeferredRequestStore_ClearsStoreAndRunsFallback()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var navigator = new RecordingRouterNavigator();
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, MauiFileDeferredNavigationRequestStore.DefaultFileName);

        try
        {
            await File.WriteAllTextAsync(path, "{not-json");
            var deferredStore = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri
                });
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
            Assert.Single(navigator.NavigateCalls);
            Assert.Equal("fallback", Assert.IsType<TestRoute>(navigator.NavigateCalls[0].Route).Id);
            Assert.Equal(1, windowAttachment.AttachCalls);
            Assert.False(await deferredStore.HasDeferredRequestsAsync());
            Assert.False(File.Exists(path));
            Assert.DoesNotContain(events, diagnosticEvent =>
                diagnosticEvent.Kind == NavigationDiagnosticEventKind.StartupDeferredRequestPending);
            Assert.Contains(events, diagnosticEvent =>
                diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.StartupDeferredRequestPending, out var pending) &&
                Equals(pending, false) &&
                diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ExceptionType, out var exceptionType) &&
                Equals(exceptionType, typeof(JsonException).FullName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
    public async Task StartAsync_NoFallbackRequest_AttachesWindowWithoutNavigation()
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
                AppLinkGracePeriod = TimeSpan.Zero
            },
            services,
            services.GetRequiredService<NavigationDiagnostics>());

        var result = await StartOnMainThreadAsync(startup, new Window(new ContentPage()));

        Assert.Equal(MauiRouterStartupOutcome.NoNavigation, result.Outcome);
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

        var invocationResult = method!.Invoke(
            startup,
            [window, "main", CancellationToken.None]);

        return AsStartupTask(invocationResult);
    }

    private static Task<MauiRouterStartupResult> AsStartupTask(object? invocationResult)
    {
        if (invocationResult is Task<MauiRouterStartupResult> task)
        {
            return task;
        }

        var reflectedTask = invocationResult?
            .GetType()
            .GetProperty("Task", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .GetValue(invocationResult);

        return Assert.IsAssignableFrom<Task<MauiRouterStartupResult>>(reflectedTask);
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

        public bool HasDeferredRequestsResult { get; init; }

        public int HasDeferredRequestsCalls { get; private set; }

        public int ClearCalls { get; private set; }

        public ValueTask<bool> HasDeferredRequestsAsync(CancellationToken cancellationToken = default)
        {
            HasDeferredRequestsCalls++;
            if (HasDeferredRequestsException is not null)
            {
                throw HasDeferredRequestsException;
            }

            return ValueTask.FromResult(HasDeferredRequestsResult);
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
    }

    private static string CreateStoreDirectory()
    {
#if IOS || MACCATALYST || ANDROID
        var root = Microsoft.Maui.Storage.FileSystem.CacheDirectory;
#else
        var root = Path.GetTempPath();
#endif
        return Path.Combine(root, $"maui-router-startup-store-{Guid.NewGuid():N}");
    }
}

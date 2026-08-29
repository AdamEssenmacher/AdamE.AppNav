using System.Reflection;
using System.Text.Json;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.History;
using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Maui.Requests;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

[Collection(ExternalNavigationBridgeTestCollection.Name)]
public sealed class AppNavStartupServiceTests
{
    private static readonly Uri BaseUri = new("https://example.com/");

    [Fact]
    public async Task StartAsync_FallbackNavigation_UsesInjectedRouterNavigator()
    {
        var navigator = new RecordingRouterNavigator();
        using var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(new NavigationDiagnostics())
            .BuildServiceProvider();
        using var dispatcher = new MauiExternalNavigationDispatcher(
            services,
            services.GetRequiredService<NavigationDiagnostics>());
        var windowAttachment = new RecordingWindowAttachment();
        var startup = new AppNavStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new AppNavStartupOptions
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

        Assert.Equal(AppNavStartupOutcome.FallbackNavigated, result.Outcome);
        Assert.Single(navigator.NavigateCalls);
        Assert.Equal("fallback", Assert.IsType<TestRoute>(navigator.NavigateCalls[0].Route).Id);
        Assert.Equal(1, windowAttachment.AttachCalls);
    }

    [Fact]
    public async Task StartAsync_TypedFallbackCreatesCanonicalInAppRequestForStartupWindow()
    {
        var navigator = new RecordingRouterNavigator();
        using var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(new NavigationDiagnostics())
            .BuildServiceProvider();
        using var dispatcher = new MauiExternalNavigationDispatcher(
            services,
            services.GetRequiredService<NavigationDiagnostics>());
        var startup = new AppNavStartupService(
            navigator,
            new RecordingWindowAttachment(),
            dispatcher,
            new AppNavStartupOptions
            {
                AppLinkGracePeriod = TimeSpan.Zero,
                FallbackRouteFactory = static (_, _) =>
                    ValueTask.FromResult<AppRoute?>(new TestRoute("typed-fallback"))
            },
            services,
            services.GetRequiredService<NavigationDiagnostics>());

        var result = await StartOnMainThreadAsync(startup, new Window(new ContentPage()));

        Assert.Equal(AppNavStartupOutcome.FallbackNavigated, result.Outcome);
        RouterNavigationRequest request = Assert.Single(navigator.NavigateCalls);
        Assert.Equal("typed-fallback", Assert.IsType<TestRoute>(request.Route).Id);
        Assert.Equal(NavigationRequestSource.InAppCommand, request.Source);
        Assert.Equal(RouterNavigationDisposition.Canonical, request.Disposition);
        Assert.Equal("main", request.WindowId);
        Assert.Null(request.Uri);
    }

    [Fact]
    public void StartupFallbackFactoriesAreMutuallyExclusive()
    {
        var navigator = new RecordingRouterNavigator();
        using var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(new NavigationDiagnostics())
            .BuildServiceProvider();
        using var dispatcher = new MauiExternalNavigationDispatcher(
            services,
            services.GetRequiredService<NavigationDiagnostics>());
        var options = new AppNavStartupOptions
        {
            FallbackRouteFactory = static (_, _) => ValueTask.FromResult<AppRoute?>(new TestRoute("route")),
            FallbackRequestFactory = static (_, _) => ValueTask.FromResult<RouterNavigationRequest?>(
                RouterNavigationRequest.FromRoute(
                    new TestRoute("request"),
                    NavigationRequestSource.InAppCommand))
        };

        var exception = Assert.Throws<InvalidOperationException>(() => new AppNavStartupService(
            navigator,
            new RecordingWindowAttachment(),
            dispatcher,
            options,
            services,
            services.GetRequiredService<NavigationDiagnostics>()));

        Assert.Contains(nameof(AppNavStartupOptions.FallbackRouteFactory), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(AppNavStartupOptions.FallbackRequestFactory), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_PendingAppLink_SkipsFallbackAndAttachesWindow()
    {
        var diagnostics = new NavigationDiagnostics();
        var navigator = new RecordingRouterNavigator();
        using var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(diagnostics)
            .BuildServiceProvider();
        using var dispatcher = new MauiExternalNavigationDispatcher(services, diagnostics);
        Assert.True(dispatcher.TryDispatch(
            RouterNavigationRequest.FromRoute(new TestRoute("pending"), NavigationRequestSource.AppLink)));

        var windowAttachment = new RecordingWindowAttachment();
        var startup = new AppNavStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new AppNavStartupOptions
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

        Assert.Equal(AppNavStartupOutcome.AppLinkPending, result.Outcome);
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
        using var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(diagnostics)
            .AddSingleton<IDeferredNavigationRequestStore>(deferredStore)
            .BuildServiceProvider();
        using var dispatcher = new MauiExternalNavigationDispatcher(services, diagnostics);
        var windowAttachment = new RecordingWindowAttachment();
        var startup = new AppNavStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new AppNavStartupOptions
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

        Assert.Equal(AppNavStartupOutcome.FallbackNavigated, result.Outcome);
        Assert.Equal(1, deferredStore.HasDeferredRequestsCalls);
        Assert.Single(navigator.NavigateCalls);
        Assert.Contains(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.StartupDeferredRequestPending &&
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.StartupDeferredRequestPending, out var pending) &&
            Equals(pending, true));
    }

    [Theory]
    [InlineData(false, AppNavStartupOutcome.NoNavigation)]
    [InlineData(true, AppNavStartupOutcome.FallbackNavigated)]
    public Task StartAsync_AsynchronousDeferredCheck_PreservesMainThreadForRemainingStartup(
        bool configureFallback,
        AppNavStartupOutcome expectedOutcome)
    {
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            Assert.True(MainThread.IsMainThread);

            var navigator = new RecordingRouterNavigator();
            var deferredStore = new GatedDeferredRequestStore();
            using var services = new ServiceCollection()
                .AddSingleton<IRouterNavigator>(navigator)
                .AddSingleton(new NavigationDiagnostics())
                .AddSingleton<IDeferredNavigationRequestStore>(deferredStore)
                .BuildServiceProvider();
            using var dispatcher = new MauiExternalNavigationDispatcher(
                services,
                services.GetRequiredService<NavigationDiagnostics>());
            var windowAttachment = new RecordingWindowAttachment();
            var options = new AppNavStartupOptions
            {
                AppLinkGracePeriod = TimeSpan.Zero
            };
            if (configureFallback)
            {
                options.FallbackRequestFactory = static (_, _) =>
                    ValueTask.FromResult<RouterNavigationRequest?>(RouterNavigationRequest.FromRoute(
                        new TestRoute("fallback"),
                        NavigationRequestSource.InAppCommand));
            }

            var startup = new AppNavStartupService(
                navigator,
                windowAttachment,
                dispatcher,
                options,
                services,
                services.GetRequiredService<NavigationDiagnostics>());

            Task<AppNavStartupResult> startupTask = startup
                .StartAsync(new Window(new ContentPage()))
                .AsTask();
            await deferredStore.CheckStarted;
            await Task.Run(() => deferredStore.Complete(hasDeferredRequests: false));
            AppNavStartupResult result = await startupTask;

            Assert.Equal(expectedOutcome, result.Outcome);
            Assert.True(Assert.Single(windowAttachment.AttachMainThreadChecks));
            if (configureFallback)
            {
                Assert.Single(navigator.NavigateCalls);
                Assert.True(Assert.Single(navigator.NavigateMainThreadChecks));
            }
            else
            {
                Assert.Empty(navigator.NavigateCalls);
                Assert.Empty(navigator.NavigateMainThreadChecks);
            }
        });
    }

    [Fact]
    public Task StartAsync_AsynchronousDeferredCheckFailure_AttachesWindowOnMainThread()
    {
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            Assert.True(MainThread.IsMainThread);

            var navigator = new RecordingRouterNavigator();
            var deferredStore = new GatedDeferredRequestStore();
            using var services = new ServiceCollection()
                .AddSingleton<IRouterNavigator>(navigator)
                .AddSingleton(new NavigationDiagnostics())
                .AddSingleton<IDeferredNavigationRequestStore>(deferredStore)
                .BuildServiceProvider();
            using var dispatcher = new MauiExternalNavigationDispatcher(
                services,
                services.GetRequiredService<NavigationDiagnostics>());
            var windowAttachment = new RecordingWindowAttachment();
            var startup = new AppNavStartupService(
                navigator,
                windowAttachment,
                dispatcher,
                new AppNavStartupOptions { AppLinkGracePeriod = TimeSpan.Zero },
                services,
                services.GetRequiredService<NavigationDiagnostics>());
            var expectedException = new IOException("Deferred request check failed.");

            Task<AppNavStartupResult> startupTask = startup
                .StartAsync(new Window(new ContentPage()))
                .AsTask();
            await deferredStore.CheckStarted;
            await Task.Run(() => deferredStore.Fail(expectedException));
            AppNavStartupResult result = await startupTask;

            Assert.Equal(AppNavStartupOutcome.Failed, result.Outcome);
            Assert.Same(expectedException, result.Exception);
            Assert.Empty(navigator.NavigateCalls);
            Assert.True(Assert.Single(windowAttachment.AttachMainThreadChecks));
        });
    }

    [Fact]
    public async Task StartAsync_UnexpectedCustomStoreFailureIsNonDestructiveAndFailsStartup()
    {
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        var navigator = new RecordingRouterNavigator();
        var deferredStore = new RecordingDeferredRequestStore
        {
            HasDeferredRequestsException = new JsonException("Deferred request JSON was corrupt.")
        };
        using var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(diagnostics)
            .AddSingleton<IDeferredNavigationRequestStore>(deferredStore)
            .BuildServiceProvider();
        using var dispatcher = new MauiExternalNavigationDispatcher(
            services,
            diagnostics);
        var windowAttachment = new RecordingWindowAttachment();
        var startup = new AppNavStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new AppNavStartupOptions
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

        Assert.Equal(AppNavStartupOutcome.Failed, result.Outcome);
        Assert.IsType<JsonException>(result.Exception);
        Assert.Equal(1, deferredStore.HasDeferredRequestsCalls);
        Assert.Equal(0, deferredStore.ClearCalls);
        Assert.Empty(navigator.NavigateCalls);
        Assert.DoesNotContain(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.StartupDeferredRequestPending);
        Assert.Contains(events, diagnosticEvent =>
            diagnosticEvent.Kind == NavigationDiagnosticEventKind.StartupFailed &&
            diagnosticEvent.Data.TryGetValue(NavigationDiagnosticDataKeys.ExceptionType, out var exceptionType) &&
            Equals(exceptionType, typeof(JsonException).FullName));
    }

    [Fact]
    public async Task StartAsync_InvalidFileDeferredRequestStore_QuarantinesAndRunsFallback()
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
                },
                diagnostics);
            using var services = new ServiceCollection()
                .AddSingleton<IRouterNavigator>(navigator)
                .AddSingleton(diagnostics)
                .AddSingleton<IDeferredNavigationRequestStore>(deferredStore)
                .BuildServiceProvider();
            using var dispatcher = new MauiExternalNavigationDispatcher(
                services,
                diagnostics);
            var windowAttachment = new RecordingWindowAttachment();
            var startup = new AppNavStartupService(
                navigator,
                windowAttachment,
                dispatcher,
                new AppNavStartupOptions
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

            Assert.Equal(AppNavStartupOutcome.FallbackNavigated, result.Outcome);
            Assert.Null(result.Exception);
            Assert.Single(navigator.NavigateCalls);
            Assert.Equal("fallback", Assert.IsType<TestRoute>(navigator.NavigateCalls[0].Route).Id);
            Assert.Equal(1, windowAttachment.AttachCalls);
            Assert.False(await deferredStore.HasDeferredRequestsAsync());
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(
                directory,
                $"{MauiFileDeferredNavigationRequestStore.DefaultFileName}.invalid-*"));
            Assert.DoesNotContain(events, diagnosticEvent =>
                diagnosticEvent.Kind == NavigationDiagnosticEventKind.StartupDeferredRequestPending);
            Assert.Contains(events, diagnosticEvent =>
                diagnosticEvent.Kind == NavigationDiagnosticEventKind.DeferredRequestStoreQuarantined &&
                diagnosticEvent.Phase == NavigationDiagnosticPhase.Persistence);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(1)]
    public async Task StartAsync_UnsupportedDeferredRequestSchema_ClearsStoreAndRunsFallback(int schemaVersion)
    {
        var diagnostics = new NavigationDiagnostics();
        var navigator = new RecordingRouterNavigator();
        var routes = RouteTable.Create(builder => builder.MapRoute<TestRoute>("/stores/{id}"));
        var directory = CreateStoreDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, MauiFileDeferredNavigationRequestStore.DefaultFileName);

        try
        {
            var serializer = new DeferredNavigationRequestSerializer(
                routes,
                new DeferredNavigationRequestPersistenceOptions { BaseUri = BaseUri });
            DeferredNavigationRequestStoreSnapshot unsupportedSnapshot = serializer.CreateSnapshot(
                [RouterNavigationRequest.FromRoute(new TestRoute("legacy"), NavigationRequestSource.AppLink)]) with
            {
                SchemaVersion = schemaVersion
            };
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    unsupportedSnapshot,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var deferredStore = new MauiFileDeferredNavigationRequestStore(
                routes,
                new MauiFileDeferredNavigationRequestStoreOptions
                {
                    Path = path,
                    BaseUri = BaseUri
                });
            using var services = new ServiceCollection()
                .AddSingleton<IRouterNavigator>(navigator)
                .AddSingleton(diagnostics)
                .AddSingleton<IDeferredNavigationRequestStore>(deferredStore)
                .BuildServiceProvider();
            using var dispatcher = new MauiExternalNavigationDispatcher(services, diagnostics);
            var startup = new AppNavStartupService(
                navigator,
                new RecordingWindowAttachment(),
                dispatcher,
                new AppNavStartupOptions
                {
                    AppLinkGracePeriod = TimeSpan.Zero,
                    FallbackRequestFactory = static (_, _) => ValueTask.FromResult<RouterNavigationRequest?>(
                        RouterNavigationRequest.FromRoute(
                            new TestRoute("fallback"),
                            NavigationRequestSource.InAppCommand))
                },
                services,
                diagnostics);

            AppNavStartupResult result = await StartOnMainThreadAsync(startup, new Window(new ContentPage()));

            Assert.Equal(AppNavStartupOutcome.FallbackNavigated, result.Outcome);
            Assert.Null(result.Exception);
            Assert.Equal("fallback", Assert.IsType<TestRoute>(Assert.Single(navigator.NavigateCalls).Route).Id);
            Assert.False(File.Exists(path));
            Assert.False(await deferredStore.HasDeferredRequestsAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_UnsupportedSchemaClearFailure_ReturnsFailed()
    {
        var navigator = new RecordingRouterNavigator();
        var clearException = new IOException("Clear failed.");
        var deferredStore = new RecordingDeferredRequestStore
        {
            HasDeferredRequestsException = new UnsupportedDeferredNavigationRequestSchemaException(1, 2),
            ClearException = clearException
        };
        using var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(new NavigationDiagnostics())
            .AddSingleton<IDeferredNavigationRequestStore>(deferredStore)
            .BuildServiceProvider();
        using var dispatcher = new MauiExternalNavigationDispatcher(
            services,
            services.GetRequiredService<NavigationDiagnostics>());
        var windowAttachment = new RecordingWindowAttachment();
        var startup = new AppNavStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new AppNavStartupOptions
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
        Assert.Equal(AppNavStartupOutcome.Failed, result.Outcome);
        Assert.Same(clearException, exception.InnerException);
        Assert.Equal(1, deferredStore.HasDeferredRequestsCalls);
        Assert.Equal(1, deferredStore.ClearCalls);
        Assert.Empty(navigator.NavigateCalls);
    }

    [Fact]
    public async Task StartAsync_NoFallbackRequest_AttachesWindowWithoutNavigation()
    {
        var navigator = new RecordingRouterNavigator();
        using var services = new ServiceCollection()
            .AddSingleton<IRouterNavigator>(navigator)
            .AddSingleton(new NavigationDiagnostics())
            .BuildServiceProvider();
        using var dispatcher = new MauiExternalNavigationDispatcher(
            services,
            services.GetRequiredService<NavigationDiagnostics>());
        var windowAttachment = new RecordingWindowAttachment();
        var startup = new AppNavStartupService(
            navigator,
            windowAttachment,
            dispatcher,
            new AppNavStartupOptions
            {
                AppLinkGracePeriod = TimeSpan.Zero
            },
            services,
            services.GetRequiredService<NavigationDiagnostics>());

        var result = await StartOnMainThreadAsync(startup, new Window(new ContentPage()));

        Assert.Equal(AppNavStartupOutcome.NoNavigation, result.Outcome);
        Assert.Empty(navigator.NavigateCalls);
        Assert.Equal(1, windowAttachment.AttachCalls);
    }

    private static Task<AppNavStartupResult> StartOnMainThreadAsync(
        AppNavStartupService startup,
        Window window)
    {
        var method = typeof(AppNavStartupService).GetMethod(
            "StartOnMainThreadAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var invocationResult = method!.Invoke(
            startup,
            [window, "main", CancellationToken.None]);

        return AsStartupTask(invocationResult);
    }

    private static Task<AppNavStartupResult> AsStartupTask(object? invocationResult)
    {
        if (invocationResult is Task<AppNavStartupResult> task)
        {
            return task;
        }

        var reflectedTask = invocationResult?
            .GetType()
            .GetProperty("Task", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .GetValue(invocationResult);

        return Assert.IsAssignableFrom<Task<AppNavStartupResult>>(reflectedTask);
    }

    private sealed record TestRoute(string Id) : AppRoute;

    private sealed class RecordingWindowAttachment : IMauiWindowAttachment
    {
        public int AttachCalls { get; private set; }

        public List<bool> AttachMainThreadChecks { get; } = [];

        public void AttachWindow(Window window, string windowId)
        {
            Assert.NotNull(window);
            Assert.Equal("main", windowId);
            AttachCalls++;
            AttachMainThreadChecks.Add(MainThread.IsMainThread);
        }
    }

    private sealed class GatedDeferredRequestStore : IDeferredNavigationRequestStore
    {
        private readonly TaskCompletionSource _checkStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CheckStarted => _checkStarted.Task;

        public void Complete(bool hasDeferredRequests)
        {
            _result.SetResult(hasDeferredRequests);
        }

        public void Fail(Exception exception)
        {
            _result.SetException(exception);
        }

        public ValueTask<bool> HasDeferredRequestsAsync(CancellationToken cancellationToken = default)
        {
            _checkStarted.SetResult();
            return new ValueTask<bool>(_result.Task.WaitAsync(cancellationToken));
        }

        public ValueTask EnqueueAsync(
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask<IDeferredNavigationRequestLease> AcquireReplayLeaseAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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

        public ValueTask<IDeferredNavigationRequestLease> AcquireReplayLeaseAsync(
            CancellationToken cancellationToken = default)
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

        public List<bool> NavigateMainThreadChecks { get; } = [];

        public NavigationState CurrentState => NavigationState.Empty;

        public NavigationHistory History => NavigationHistory.Empty;

        public ValueTask<NavigationResult> NavigateAsync(
            RouterNavigationRequest request,
            CancellationToken cancellationToken = default)
        {
            NavigateCalls.Add(request);
            NavigateMainThreadChecks.Add(MainThread.IsMainThread);
            return ValueTask.FromResult(new NavigationResult(
                request.Route!,
                new NavigationPlan(NavigationState.Empty),
                NavigationState.Empty,
                Presented: true));
        }

        public ValueTask<BackNavigationResult> BackAsync(string? windowId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> ReconcileAsync(NavigationReconciliation reconciliation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string CreateStoreDirectory()
    {
#if IOS || MACCATALYST || ANDROID
        var root = Microsoft.Maui.Storage.FileSystem.CacheDirectory;
#else
        var root = Path.GetTempPath();
#endif
        return Path.Combine(root, $"appnav-startup-store-{Guid.NewGuid():N}");
    }
}

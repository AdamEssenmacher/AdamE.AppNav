using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.History;
using AdamE.AppNav.Maui.AppLinks;
using AdamE.AppNav.Maui.DependencyInjection;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Planning;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

[Collection(ExternalNavigationBridgeTestCollection.Name)]
public sealed class AppNavNavigatorRegistrationTests
{
    [Fact]
    public void AddAppNavDiagnosticsConfiguresResolvedSingletonRegardlessOfRegistrationOrder()
    {
        var services = new ServiceCollection();
        services.AddAppNav<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));
        services.AddAppNavDiagnostics(options =>
            options.DataMode = NavigationDiagnosticDataMode.Full);
        using ServiceProvider provider = services.BuildServiceProvider();
        NavigationDiagnosticEvent? observed = null;
        NavigationDiagnostics diagnostics = provider.GetRequiredService<NavigationDiagnostics>();
        diagnostics.EventWritten += (_, diagnosticEvent) => observed = diagnosticEvent;

        diagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkReceived,
            "operation",
            "full-message",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.Uri] = "https://example.com/path?token=visible"
            });

        Assert.NotNull(observed);
        Assert.Equal("full-message", observed!.Message);
        Assert.Contains("token=visible", observed.Data[NavigationDiagnosticDataKeys.Uri]!.ToString());
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(MauiNavigationPresenter)));
    }

    [Fact]
    public void AddAppNavRegistersBlessedRuntimeAbstractions()
    {
        var services = new ServiceCollection();

        services.AddAppNav<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IAppNavRuntime>();
        var navigator = provider.GetRequiredService<IRouterNavigator>();
        var windowAttachment = provider.GetRequiredService<IMauiWindowAttachment>();
        var presenter = provider.GetRequiredService<MauiNavigationPresenter>();
        var presentationState = provider.GetRequiredService<IMauiPresentationState>();
        var routePresentationNavigator = provider.GetRequiredService<IMauiRoutePresentationNavigator>();
        var dispatcher = provider.GetRequiredService<IMauiExternalNavigationDispatcher>();

        Assert.Same(runtime, navigator);
        Assert.Same(runtime, windowAttachment);
        Assert.Same(presenter, presentationState);
        Assert.Same(presenter, routePresentationNavigator);
        Assert.NotNull(dispatcher);
    }

    [Fact]
    public void AddAppNavModelOverloadRegistersStandardPlanner()
    {
        var services = new ServiceCollection();
        var model = StackNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "main-stack");
            builder.Map<TestRoute>(route => route.EntryId(value => value.Id));
        });

        services.AddAppNav(
            Routes(),
            model,
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));

        using var provider = services.BuildServiceProvider();

        Assert.Same(model, provider.GetRequiredService<INavigationModel<TestRoute>>());
        Assert.IsType<NavigationModelPlanner<TestRoute>>(
            provider.GetRequiredService<IAppNavigationPlanner>());
    }

    [Fact]
    public async Task AddAppNavPlannerOverloadAppliesConfiguredFallbackAndHistoryLimit()
    {
        var services = new ServiceCollection();
        AppNavNavigatorOptions? configuredOptions = null;
        var configureCount = 0;

        services.AddAppNav<CapturingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()),
            options =>
            {
                configureCount++;
                configuredOptions = options;
                options.FallbackRouteFactory = context =>
                    new TestRoute($"fallback:{context.Request.Uri!.AbsolutePath}");
                options.MaxHistoryEntries = 2;
            });

        Assert.Equal(1, configureCount);
        Assert.NotNull(configuredOptions);

        using var provider = services.BuildServiceProvider();
        var navigator = provider.GetRequiredService<IRouterNavigator>();
        var planner = Assert.IsType<CapturingPlanner>(provider.GetRequiredService<IAppNavigationPlanner>());

        NavigationResult fallbackResult = await navigator.NavigateAsync(RouterNavigationRequest.FromUri(
            new Uri("https://example.com/missing"),
            NavigationRequestSource.Test));
        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoute("one"),
            NavigationRequestSource.Test));
        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoute("two"),
            NavigationRequestSource.Test));

        Assert.Equal(new TestRoute("fallback:/missing"), fallbackResult.Route);
        Assert.Equal(new TestRoute("two"), planner.LastRoute);
        Assert.Equal(
            [new TestRoute("one"), new TestRoute("two")],
            navigator.History.Entries.Select(entry => entry.Route));
    }

    [Fact]
    public async Task AddAppNavModelOverloadAppliesConfiguredRedirectLimit()
    {
        var services = new ServiceCollection();
        var policy = new RedirectingPolicy();
        services.AddSingleton<INavigationRequestPolicy>(policy);
        var model = StackNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "main-stack");
            builder.Map<TestRoute>(route => route.EntryId(value => value.Id));
        });

        services.AddAppNav(
            Routes(),
            model,
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()),
            options => options.MaxRedirects = 0);

        using var provider = services.BuildServiceProvider();
        var navigator = provider.GetRequiredService<IRouterNavigator>();

        RouteRedirectLoopException exception = await Assert.ThrowsAsync<RouteRedirectLoopException>(() =>
            navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
                new TestRoute("source"),
                NavigationRequestSource.Test)).AsTask());

        Assert.Contains("MaxRedirects is 0", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, policy.ApplyCount);
    }

    [Fact]
    public async Task AddAppNavWithoutNavigatorConfigurationUsesDefaultOptions()
    {
        var defaults = new AppNavNavigatorOptions();
        Assert.Null(defaults.FallbackRouteFactory);
        Assert.Equal(16, defaults.MaxRedirects);
        Assert.Equal(128, defaults.MaxHistoryEntries);

        var services = new ServiceCollection();
        services.AddAppNav<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));

        using var provider = services.BuildServiceProvider();
        var navigator = provider.GetRequiredService<IRouterNavigator>();

        await Assert.ThrowsAsync<RouteNotMatchedException>(() => navigator.NavigateAsync(
            RouterNavigationRequest.FromUri(
                new Uri("https://example.com/missing"),
                NavigationRequestSource.Test)).AsTask());
    }

    [Fact]
    public void AddAppNavOverloadsRejectNegativeNavigatorLimitsBeforeMutatingServices()
    {
        var plannerServices = new ServiceCollection();
        int plannerRegistrationCount = plannerServices.Count;

        ArgumentOutOfRangeException redirectsException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            plannerServices.AddAppNav<ThrowingPlanner>(
                Routes(),
                configurePages: null,
                configureNavigator: options => options.MaxRedirects = -1));

        Assert.Equal(nameof(AppNavNavigatorOptions.MaxRedirects), redirectsException.ParamName);
        Assert.Equal(plannerRegistrationCount, plannerServices.Count);

        var modelServices = new ServiceCollection();
        var model = StackNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "main-stack");
            builder.Map<TestRoute>(route => route.EntryId(value => value.Id));
        });
        int modelRegistrationCount = modelServices.Count;

        ArgumentOutOfRangeException historyException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            modelServices.AddAppNav(
                Routes(),
                model,
                configurePages: null,
                configureNavigator: options => options.MaxHistoryEntries = -1));

        Assert.Equal(nameof(AppNavNavigatorOptions.MaxHistoryEntries), historyException.ParamName);
        Assert.Equal(modelRegistrationCount, modelServices.Count);
    }

    [Fact]
    public void AddAppNavRejectsPreRegisteredRouterNavigatorBeforeMutatingServices()
    {
        var services = new ServiceCollection();
        var navigator = new RecordingRouterNavigator();
        services.AddSingleton<IRouterNavigator>(navigator);
        int initialRegistrationCount = services.Count;

        var exception = Assert.Throws<AppNavigationConfigurationException>(() =>
            services.AddAppNav<ThrowingPlanner>(
                Routes(),
                pages => pages.MapPage<TestRoute>((_, _) => new TestPage())));

        Assert.Equal(initialRegistrationCount, services.Count);
        Assert.Contains(nameof(IRouterNavigator), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RouterNavigatorFactory), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAppNavModelOverloadRejectsPreRegisteredRouterNavigatorBeforeMutatingServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRouterNavigator>(new RecordingRouterNavigator());
        int initialRegistrationCount = services.Count;
        var model = StackNavigationModel<TestRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "main-stack");
            builder.Map<TestRoute>(route => route.EntryId(value => value.Id));
        });

        var exception = Assert.Throws<AppNavigationConfigurationException>(() =>
            services.AddAppNav(
                Routes(),
                model,
                pages => pages.MapPage<TestRoute>((_, _) => new TestPage())));

        Assert.Equal(initialRegistrationCount, services.Count);
        Assert.Contains(nameof(IRouterNavigator), exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RouterNavigatorFactory), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddAppNavAllowsKeyedRouterNavigatorAlongsideOwnedRuntime()
    {
        var services = new ServiceCollection();
        var keyedNavigator = new RecordingRouterNavigator();
        services.AddKeyedSingleton<IRouterNavigator>("alternate", keyedNavigator);

        services.AddAppNav<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));

        using var provider = services.BuildServiceProvider();
        IAppNavRuntime runtime = provider.GetRequiredService<IAppNavRuntime>();

        Assert.Same(runtime, provider.GetRequiredService<IRouterNavigator>());
        Assert.Same(runtime, provider.GetRequiredService<IMauiWindowAttachment>());
        Assert.Same(
            keyedNavigator,
            provider.GetRequiredKeyedService<IRouterNavigator>("alternate"));
    }

    [Fact]
    public async Task AddAppNavDiscoversTransformersPoliciesAndBackNavigatorFromDi()
    {
        var services = new ServiceCollection();
        var requestTransformer = new RecordingRequestTransformer();
        var requestPolicy = new RecordingRequestPolicy();
        var backNavigator = new RecordingBackNavigator();
        services.AddSingleton<INavigationRequestTransformer>(requestTransformer);
        services.AddSingleton<INavigationRequestPolicy>(requestPolicy);
        services.AddSingleton<IBackNavigator>(backNavigator);

        services.AddAppNav<RecordingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));

        using var provider = services.BuildServiceProvider();
        var navigator = provider.GetRequiredService<IRouterNavigator>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
                new TestRoute("registered"), NavigationRequestSource.Test)).AsTask());
        await navigator.BackAsync();

        Assert.Equal(RecordingRequestPolicy.ExceptionMessage, exception.Message);
        Assert.Equal(1, requestTransformer.TransformCount);
        Assert.Equal(1, requestPolicy.ApplyCount);
        Assert.Equal(1, backNavigator.CreateCount);
    }

    [Fact]
    public async Task ServiceProviderDisposalShutsDownFactoryCreatedNavigator()
    {
        var services = new ServiceCollection();
        services.AddAppNav<RecordingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));
        ServiceProvider provider = services.BuildServiceProvider();
        var navigator = provider.GetRequiredService<IRouterNavigator>();

        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => navigator
            .NavigateAsync(RouterNavigationRequest.FromRoute(
                new TestRoute("disposed"), NavigationRequestSource.Test))
            .AsTask());
    }

    [Fact]
    public async Task AsyncServiceProviderDisposalWaitsForPresenterPageScopeCleanup()
    {
        var services = new ServiceCollection();
        services.AddScoped<AsyncDisposeMarker>();
        services.AddTransient<AsyncDisposePage>();
        services.AddAppNav<PagePlanner>(
            Routes(),
            pages => pages.MapPageFromServices<TestRoute, AsyncDisposePage>());
        ServiceProvider provider = services.BuildServiceProvider();
        var navigator = provider.GetRequiredService<IRouterNavigator>();
        var presenter = provider.GetRequiredService<MauiNavigationPresenter>();
        await navigator.NavigateAsync(RouterNavigationRequest.FromRoute(
            new TestRoute("owned"), NavigationRequestSource.Test));
        AsyncDisposeMarker marker = Assert.IsType<AsyncDisposePage>(
            Assert.IsType<NavigationPage>(presenter.CurrentPage).CurrentPage).Marker;

        await provider.DisposeAsync();

        Assert.Equal(1, marker.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => navigator
            .NavigateAsync(RouterNavigationRequest.FromRoute(
                new TestRoute("disposed"), NavigationRequestSource.Test))
            .AsTask());
    }

    [Fact]
    public async Task ResolvingPresentationSurfaceCreatesRuntimeThatOwnsPresenterShutdown()
    {
        var services = new ServiceCollection();
        services.AddAppNav<RecordingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));
        ServiceProvider provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<IMauiPresentationState>();
        IAppNavRuntime runtime = provider.GetRequiredService<IAppNavRuntime>();

        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime
            .NavigateAsync(RouterNavigationRequest.FromRoute(
                new TestRoute("disposed"), NavigationRequestSource.Test))
            .AsTask());
    }

    [Fact]
    public async Task AddAppNavPagesComposesContributorMappingsWithInlineMappings()
    {
        var services = new ServiceCollection();
        services.AddAppNavPages(pages => pages.MapPage<ContributorRoute>((_, _) => new ContributorPage()));
        services.AddAppNav<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<InlineRoute>((_, _) => new InlinePage()));

        using var provider = services.BuildServiceProvider();
        var pageFactory = provider.GetRequiredService<IMauiRoutePageFactory>();

        Assert.IsType<InlinePage>(await pageFactory.CreatePageAsync(new RouteEntry("inline", new InlineRoute("inline"))));
        Assert.IsType<ContributorPage>(await pageFactory.CreatePageAsync(new RouteEntry("contributor", new ContributorRoute("contributor"))));
    }

    [Fact]
    public void AddAppNavStartupExposesPublicExternalIngressSeam()
    {
        var services = new ServiceCollection();
        services.AddAppNav<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));
        services.AddAppNavStartup();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IMauiExternalNavigationDispatcher>());
        Assert.NotNull(provider.GetRequiredService<IAppNavStartupService>());
    }

    [Fact]
    public void AddAppNavFileDeferredNavigationRequestsRegistersStoreAndReplayer()
    {
        var services = new ServiceCollection();
        var path = Path.Combine(Path.GetTempPath(), $"appnav-deferred-{Guid.NewGuid():N}.json");
        services.AddAppNav<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));
        services.AddAppNavFileDeferredNavigationRequests(options =>
        {
            options.Path = path;
            options.BaseUri = new Uri("https://example.com/");
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IDeferredNavigationRequestStore>());
        Assert.NotNull(provider.GetRequiredService<IDeferredNavigationRequestReplayer>());
    }

    private static RouteTable Routes()
    {
        return RouteTable.Create(routes =>
        {
            routes.MapRoute<TestRoute>("/tests/{id}");
            routes.MapRoute<InlineRoute>("/inline/{id}");
            routes.MapRoute<ContributorRoute>("/contributors/{id}");
        });
    }

    private sealed record TestRoute(string Id) : AppRoute;

    private sealed record InlineRoute(string Id) : AppRoute;

    private sealed record ContributorRoute(string Id) : AppRoute;

    private sealed class TestPage : ContentPage;

    private sealed class AsyncDisposePage(AsyncDisposeMarker marker) : ContentPage
    {
        public AsyncDisposeMarker Marker { get; } = marker;
    }

    private sealed class InlinePage : ContentPage;

    private sealed class ContributorPage : ContentPage;

    private sealed class ThrowingPlanner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Registration tests do not execute navigation.");
        }
    }

    private sealed class RecordingPlanner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NavigationPlan(NavigationState.Empty));
        }
    }

    private sealed class CapturingPlanner : IAppNavigationPlanner
    {
        public AppRoute? LastRoute { get; private set; }

        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            LastRoute = context.Route;
            return ValueTask.FromResult(new NavigationPlan(NavigationState.Empty));
        }
    }

    private sealed class PagePlanner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new NavigationPlan(new NavigationState(
                [
                    new WindowNode(
                        "main",
                        new StackNode("main-stack", [new RouteEntry("page", context.Route)]))
                ],
                "main")));
        }
    }

    private sealed class AsyncDisposeMarker : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRequestPolicy : INavigationRequestPolicy
    {
        public const string ExceptionMessage = "Request policy invoked.";

        public int ApplyCount { get; private set; }

        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            throw new InvalidOperationException(ExceptionMessage);
        }
    }

    private sealed class RedirectingPolicy : INavigationRequestPolicy
    {
        public int ApplyCount { get; private set; }

        public ValueTask<RouterNavigationRequest> ApplyAsync(
            NavigationRequestPolicyContext context,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            return ValueTask.FromResult(context.Request.WithTarget(new TestRoute("redirected")));
        }
    }

    private sealed class RecordingRequestTransformer : INavigationRequestTransformer
    {
        public int TransformCount { get; private set; }

        public ValueTask<RouterNavigationRequest> TransformAsync(
            NavigationRequestTransformContext context,
            CancellationToken cancellationToken = default)
        {
            TransformCount++;
            return ValueTask.FromResult(context.Request);
        }
    }

    private sealed class RecordingBackNavigator : IBackNavigator
    {
        public int CreateCount { get; private set; }

        public NavigationPlan? CreateBackPlan(BackNavigationContext context)
        {
            CreateCount++;
            return null;
        }
    }

    private sealed class RecordingRouterNavigator : IRouterNavigator
    {
        public NavigationState CurrentState => NavigationState.Empty;

        public NavigationHistory History => NavigationHistory.Empty;

        public ValueTask<NavigationResult> NavigateAsync(RouterNavigationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<BackNavigationResult> BackAsync(string? windowId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> ReconcileAsync(NavigationReconciliation reconciliation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

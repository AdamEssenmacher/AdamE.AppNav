using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Maui.AppLinks;
using AdamE.MauiRouter.Maui.DependencyInjection;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui.Tests;

public sealed class MauiRouterNavigatorRegistrationTests
{
    [Fact]
    public void AddMauiRouterRegistersBlessedRuntimeAbstractions()
    {
        var services = new ServiceCollection();

        services.AddMauiRouter<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetRequiredService<IMauiRouterRuntime>();
        var navigator = provider.GetRequiredService<IRouterNavigator>();
        var windowAttachment = provider.GetRequiredService<IMauiWindowAttachment>();
        var presenter = provider.GetRequiredService<MauiNavigationPresenter>();
        var presentationState = provider.GetRequiredService<IMauiPresentationState>();
        var dispatcher = provider.GetRequiredService<IMauiExternalNavigationDispatcher>();

        Assert.Same(runtime, navigator);
        Assert.Same(runtime, windowAttachment);
        Assert.Same(presenter, presentationState);
        Assert.NotNull(dispatcher);
    }

    [Fact]
    public void AddMauiRouterPreservesPreRegisteredRouterNavigatorOverride()
    {
        var services = new ServiceCollection();
        var navigator = new RecordingRouterNavigator();
        services.AddSingleton<IRouterNavigator>(navigator);

        services.AddMauiRouter<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));

        using var provider = services.BuildServiceProvider();

        Assert.Same(navigator, provider.GetRequiredService<IRouterNavigator>());
        Assert.NotNull(provider.GetRequiredService<IMauiRouterRuntime>());
        Assert.NotNull(provider.GetRequiredService<IMauiWindowAttachment>());
    }

    [Fact]
    public async Task AddMauiRouterDiscoversRequestPoliciesAndBackNavigatorFromDi()
    {
        var services = new ServiceCollection();
        var requestPolicy = new RecordingRequestPolicy();
        var backNavigator = new RecordingBackNavigator();
        services.AddSingleton<INavigationRequestPolicy>(requestPolicy);
        services.AddSingleton<IBackNavigator>(backNavigator);

        services.AddMauiRouter<RecordingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));

        using var provider = services.BuildServiceProvider();
        var navigator = provider.GetRequiredService<IRouterNavigator>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            navigator.NavigateAsync(new TestRoute("registered"), NavigationRequestSource.Test).AsTask());
        await navigator.BackAsync();

        Assert.Equal(RecordingRequestPolicy.ExceptionMessage, exception.Message);
        Assert.Equal(1, requestPolicy.ApplyCount);
        Assert.Equal(1, backNavigator.CreateCount);
    }

    [Fact]
    public void AddMauiRouterPagesComposesContributorMappingsWithInlineMappings()
    {
        var services = new ServiceCollection();
        services.AddMauiRouterPages(pages => pages.MapPage<ContributorRoute>((_, _) => new ContributorPage()));
        services.AddMauiRouter<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<InlineRoute>((_, _) => new InlinePage()));

        using var provider = services.BuildServiceProvider();
        var pageFactory = provider.GetRequiredService<IMauiRoutePageFactory>();

        Assert.IsType<InlinePage>(pageFactory.CreatePage(new RouteEntry("inline", new InlineRoute("inline"))));
        Assert.IsType<ContributorPage>(pageFactory.CreatePage(new RouteEntry("contributor", new ContributorRoute("contributor"))));
    }

    [Fact]
    public void AddMauiRouterStartupExposesPublicExternalIngressSeam()
    {
        var services = new ServiceCollection();
        services.AddMauiRouter<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));
        services.AddMauiRouterStartup();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IMauiExternalNavigationDispatcher>());
        Assert.NotNull(provider.GetRequiredService<IMauiRouterStartupService>());
    }

    [Fact]
    public void AddMauiRouterFileDeferredNavigationRequestsRegistersStoreAndReplayer()
    {
        var services = new ServiceCollection();
        var path = Path.Combine(Path.GetTempPath(), $"maui-router-deferred-{Guid.NewGuid():N}.json");
        services.AddMauiRouter<ThrowingPlanner>(
            Routes(),
            pages => pages.MapPage<TestRoute>((_, _) => new TestPage()));
        services.AddMauiRouterFileDeferredNavigationRequests(options => options.Path = path);

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

        public ValueTask<NavigationResult> NavigateAsync(Uri uri, NavigationRequestSource source = NavigationRequestSource.InAppCommand, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(Uri uri, NavigationRequestSource source, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(Uri uri, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRoute route, NavigationRequestSource source = NavigationRequestSource.InAppCommand, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRoute route, NavigationRequestSource source, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRoute route, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRouteRequest routeRequest, NavigationRequestSource source = NavigationRequestSource.InAppCommand, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRouteRequest routeRequest, NavigationRequestSource source, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(AppRouteRequest routeRequest, RouterNavigationDisposition disposition, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> NavigateAsync(RouterNavigationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<BackNavigationResult> BackAsync(string? windowId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<NavigationResult> ReconcileAsync(NavigationReconciliation reconciliation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

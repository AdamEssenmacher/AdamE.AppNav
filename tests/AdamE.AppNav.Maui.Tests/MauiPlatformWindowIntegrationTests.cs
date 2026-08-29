using AdamE.AppNav.History;
using AdamE.AppNav.Maui.DependencyInjection;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Planning;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

#if ANDROID || IOS || MACCATALYST
[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class MauiPlatformWindowIntegrationCollection
{
    public const string CollectionName = "MAUI platform window integration";
}

[Collection(MauiPlatformWindowIntegrationCollection.CollectionName)]
public sealed class MauiPlatformWindowIntegrationTests
{
    [Fact]
    public Task RealWindowNavigationAndBackKeepLogicalNativeHistoryAndScopesConsistent()
    {
        return MainThread.InvokeOnMainThreadAsync(RunOnMainThreadAsync);
    }

    private static async Task RunOnMainThreadAsync()
    {
        Assert.True(MainThread.IsMainThread);

        Application app = Assert.IsAssignableFrom<Application>(Application.Current);
        Window window = Assert.Single(app.Windows);
        Page? originalPage = window.Page;

        ServiceProvider provider = CreateServices();
        PlatformPageScope? homeScope = null;
        PlatformPageScope? detailScope = null;

        try
        {
            IAppNavStartupService startup = provider.GetRequiredService<IAppNavStartupService>();
            IRouterNavigator navigator = provider.GetRequiredService<IRouterNavigator>();

            AppNavStartupResult startupResult = await startup.StartAsync(window, "main");

            Assert.True(MainThread.IsMainThread);
            Assert.Equal(AppNavStartupOutcome.FallbackNavigated, startupResult.Outcome);
            StackNode startupStack = AssertLogicalStack(navigator, typeof(PlatformHomeRoute));
            Assert.Equal("home", Assert.Single(startupStack.Entries).Id);

            var nativeStack = Assert.IsType<NavigationPage>(window.Page);
            var homePage = Assert.IsType<PlatformHomePage>(Assert.Single(nativeStack.Navigation.NavigationStack));
            homeScope = homePage.Scope;
            Assert.Equal(0, homeScope.DisposeCount);
            AssertHistory(navigator.History, 1, typeof(PlatformHomeRoute));

            await navigator.NavigateAsync(new PlatformDetailRoute("one"));

            Assert.True(MainThread.IsMainThread);
            StackNode detailStack = AssertLogicalStack(
                navigator,
                typeof(PlatformHomeRoute),
                typeof(PlatformDetailRoute));
            Assert.Equal(new[] { "home", "detail-one" }, detailStack.Entries.Select(entry => entry.Id));
            Assert.Same(nativeStack, window.Page);
            Assert.Collection(
                nativeStack.Navigation.NavigationStack,
                page => Assert.Same(homePage, page),
                page => detailScope = Assert.IsType<PlatformDetailPage>(page).Scope);
            Assert.NotNull(detailScope);
            Assert.Equal(0, detailScope!.DisposeCount);
            AssertHistory(navigator.History, 2, typeof(PlatformDetailRoute));

            BackNavigationResult backResult = await navigator.BackAsync("main");

            Assert.True(MainThread.IsMainThread);
            Assert.True(backResult.Handled);
            Assert.Equal(NavigationPlanKind.Back, backResult.HandledNavigationResult?.Plan.Kind);
            StackNode backStack = AssertLogicalStack(navigator, typeof(PlatformHomeRoute));
            Assert.Equal("home", Assert.Single(backStack.Entries).Id);
            Assert.Same(homePage, Assert.Single(nativeStack.Navigation.NavigationStack));
            AssertHistory(navigator.History, 3, typeof(PlatformHomeRoute));
            Assert.Equal(1, detailScope.DisposeCount);
            Assert.Equal(0, homeScope.DisposeCount);
        }
        finally
        {
            await provider.DisposeAsync();
            Assert.True(MainThread.IsMainThread);
            window.Page = originalPage;
        }

        Assert.Equal(1, detailScope?.DisposeCount);
        Assert.Equal(1, homeScope?.DisposeCount);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddScoped<PlatformPageScope>();
        services.AddAppNavStartup(options =>
        {
            options.AppLinkGracePeriod = TimeSpan.Zero;
            options.FallbackRouteFactory = static (_, _) =>
                ValueTask.FromResult<AppRoute?>(new PlatformHomeRoute());
        });
        services.AddAppNav(
            RouteTable.Create(routes =>
            {
                routes.MapRoute<PlatformHomeRoute>("/platform/home");
                routes.MapRoute<PlatformDetailRoute>("/platform/details/{id}");
            }),
            CreateModel(),
            pages =>
            {
                pages.MapPage<PlatformHomeRoute>((serviceProvider, _) =>
                    new PlatformHomePage(serviceProvider.GetRequiredService<PlatformPageScope>()));
                pages.MapPage<PlatformDetailRoute>((serviceProvider, _) =>
                    new PlatformDetailPage(serviceProvider.GetRequiredService<PlatformPageScope>()));
            });

        return services.BuildServiceProvider();
    }

    private static StackNavigationModel<AppRoute> CreateModel()
    {
        return StackNavigationModel<AppRoute>.Create(builder =>
        {
            builder.CanonicalSurface("main", "main-stack");
            builder.Map<PlatformHomeRoute>(recipe => recipe
                .EntryId(_ => "home")
                .ScopeKey(_ => "platform"));
            builder.Map<PlatformDetailRoute>(recipe => recipe
                .EntryId(route => $"detail-{route.Id}")
                .ScopeKey(_ => "platform")
                .Canonical((route, metadata) =>
                [
                    new StackRouteStep<AppRoute>(new PlatformHomeRoute()),
                    new StackRouteStep<AppRoute>(route, metadata)
                ]));
        });
    }

    private static StackNode AssertLogicalStack(
        IRouterNavigator navigator,
        params Type[] expectedRouteTypes)
    {
        Assert.Equal("main", navigator.CurrentState.ActiveWindowId);
        WindowNode window = Assert.IsType<WindowNode>(navigator.CurrentState.ActiveWindow);
        Assert.Equal("main", window.Id);
        var stack = Assert.IsType<StackNode>(window.Root);
        Assert.Equal("main-stack", stack.Id);
        Assert.Equal(expectedRouteTypes, stack.Entries.Select(entry => entry.Route.GetType()));
        return stack;
    }

    private static void AssertHistory(
        NavigationHistory history,
        int expectedCount,
        Type expectedCurrentRouteType)
    {
        Assert.Equal(expectedCount, history.Entries.Count);
        Assert.IsType(expectedCurrentRouteType, history.Current?.Route);
    }

    private abstract record PlatformRoute : AppRoute;

    private sealed record PlatformHomeRoute : PlatformRoute;

    private sealed record PlatformDetailRoute(string Id) : PlatformRoute;

    private sealed class PlatformHomePage(PlatformPageScope scope) : ContentPage
    {
        public PlatformPageScope Scope { get; } = scope;
    }

    private sealed class PlatformDetailPage(PlatformPageScope scope) : ContentPage
    {
        public PlatformPageScope Scope { get; } = scope;
    }

    private sealed class PlatformPageScope : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
#endif

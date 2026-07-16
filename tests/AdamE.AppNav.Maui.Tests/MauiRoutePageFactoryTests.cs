using AdamE.AppNav.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiRoutePageFactoryTests
{
    [Fact]
    public async Task CreatePage_AddModuleMapsPagesAndReturnsRegistry()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();

        var returned = options.Pages.AddModule(new ModulePages());
        var factory = new MauiRoutePageFactory(provider, options);

        var page = await factory.CreatePageAsync(new RouteEntry("module-route", new ModulePageRoute()));

        Assert.Same(options.Pages, returned);
        Assert.IsType<ModuleMappedPage>(page);
    }

    [Fact]
    public async Task CreatePage_MapPageFromServices_ResolvesPageFromDI()
    {
        var services = new ServiceCollection();
        services.AddSingleton<PageDependency>();
        services.AddTransient<ServiceResolvedPage>();
        using var provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();
        options.Pages.MapPageFromServices<TestPageRoute, ServiceResolvedPage>();
        var factory = new MauiRoutePageFactory(provider, options);

        var page = Assert.IsType<ServiceResolvedPage>(await factory.CreatePageAsync(Entry("route-1")));

        Assert.NotNull(page.Dependency);
    }

    [Fact]
    public async Task LifecycleHooks_RunForCreateUpdateAndReleaseBeforeScopedServicesAreDisposed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<LifecycleTracker>();
        services.AddScoped<ScopedMarker>();
        services.AddScoped<IMauiRoutePageLifecycleHook, RecordingLifecycleHook>();
        services.AddTransient<ScopedPage>();
        using var provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions
        {
            UseScopedPages = true
        };
        options.Pages.MapPageFromServices<TestPageRoute, ScopedPage>();
        var factory = new MauiRoutePageFactory(provider, options);

        var page = Assert.IsType<ScopedPage>(await factory.CreatePageAsync(Entry("route-1")));
        await factory.UpdatePageAsync(
            page,
            Entry("route-2"),
            new MauiRoutePageUpdateContext(MauiRoutePageReuseKind.ExplicitTarget));
        await factory.ReleasePageAsync(page);

        var tracker = provider.GetRequiredService<LifecycleTracker>();
        Assert.Equal(
            [
                "created:route-1",
                "updated:ExplicitTarget:route-2",
                "released:False"
            ],
            tracker.Events);
        Assert.True(page.Marker.IsDisposed);
    }

    [Fact]
    public async Task AsyncOnlyScopeAndLifecycleHookAreReleasedExactlyOnce()
    {
        var services = new ServiceCollection();
        services.AddSingleton<LifecycleTracker>();
        services.AddScoped<AsyncScopedMarker>();
        services.AddScoped<IMauiRoutePageLifecycleHook, AsyncRecordingLifecycleHook>();
        services.AddTransient<AsyncScopedPage>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions { UseScopedPages = true };
        options.Pages.MapPageFromServices<TestPageRoute, AsyncScopedPage>();
        var factory = new MauiRoutePageFactory(provider, options);

        var page = Assert.IsType<AsyncScopedPage>(await factory.CreatePageAsync(Entry("async")));
        page.BindingContext = new object();
        await factory.ReleasePageAsync(page);
        await factory.ReleasePageAsync(page);

        Assert.Equal(1, page.Marker.DisposeCount);
        Assert.Null(page.BindingContext);
        Assert.Equal(
            ["async-created:async", "async-released:0"],
            provider.GetRequiredService<LifecycleTracker>().Events);
    }

    [Fact]
    public async Task LifecycleHookResolutionFailureDisposesUnattachedPageScope()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FailureCleanupTracker>();
        services.AddScoped<TrackedAsyncDisposable>();
        services.AddTransient<ScopedFailurePage>();
        services.AddScoped<IMauiRoutePageLifecycleHook>(_ =>
            throw new InvalidOperationException("Injected lifecycle-hook resolution failure."));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions { UseScopedPages = true };
        options.Pages.MapPageFromServices<TestPageRoute, ScopedFailurePage>();
        var factory = new MauiRoutePageFactory(provider, options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory
            .CreatePageAsync(Entry("failure"))
            .AsTask());

        Assert.Equal(1, provider.GetRequiredService<FailureCleanupTracker>().DisposeCount);
    }

    [Fact]
    public async Task PresentationPage_InheritsOwnerBindingContextAndDisposesItsOwnScopeOnRelease()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopedMarker>();
        services.AddTransient<PresentationPage>();
        using var provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions
        {
            UseScopedPages = true
        };
        var factory = new MauiRoutePageFactory(provider, options);
        var ownerBindingContext = new object();
        var owner = new ContentPage { BindingContext = ownerBindingContext };

        var page = Assert.IsType<PresentationPage>(await factory.CreatePresentationPageAsync(
            typeof(PresentationPage),
            owner,
            inheritBindingContext: true));

        Assert.Same(ownerBindingContext, page.BindingContext);
        Assert.False(page.Marker.IsDisposed);

        await factory.ReleasePresentationPageAsync(page);

        Assert.Null(page.BindingContext);
        Assert.True(page.Marker.IsDisposed);
    }

    [Fact]
    public async Task PresentationPage_ClearsItsOwnBindingContextOnRelease()
    {
        var services = new ServiceCollection();
        services.AddTransient<IndependentBindingPage>();
        using var provider = services.BuildServiceProvider();
        var factory = new MauiRoutePageFactory(provider, new MauiRoutePresentationOptions());
        var owner = new ContentPage { BindingContext = new object() };

        var page = Assert.IsType<IndependentBindingPage>(await factory.CreatePresentationPageAsync(
            typeof(IndependentBindingPage),
            owner,
            inheritBindingContext: false));

        Assert.Same(page.OwnBindingContext, page.BindingContext);

        await factory.ReleasePresentationPageAsync(page);

        Assert.Null(page.BindingContext);
    }

    [Fact]
    public async Task CreatePage_PrefersMostSpecificMappedPageForDerivedRoute()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();
        options.Pages
            .MapPage<BaseMappedRoute>((_, _) => new BaseMappedPage())
            .MapPage<DerivedMappedRoute>((_, _) => new DerivedMappedPage());
        var factory = new MauiRoutePageFactory(provider, options);

        var page = await factory.CreatePageAsync(new RouteEntry("derived-route", new DerivedMappedRoute()));

        Assert.IsType<DerivedMappedPage>(page);
    }

    [Fact]
    public async Task CreatePage_FallsBackToBaseMappedPageWhenDerivedRouteMappingIsMissing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();
        options.Pages.MapPage<BaseMappedRoute>((_, _) => new BaseMappedPage());
        var factory = new MauiRoutePageFactory(provider, options);

        var page = await factory.CreatePageAsync(new RouteEntry("derived-route", new DerivedMappedRoute()));

        Assert.IsType<BaseMappedPage>(page);
    }

    private static RouteEntry Entry(string id)
    {
        return new RouteEntry(id, new TestPageRoute(id));
    }

    private record BaseMappedRoute : AppRoute;

    private sealed record DerivedMappedRoute : BaseMappedRoute;

    private sealed record ModulePageRoute : AppRoute;

    private sealed class PageDependency;

    private sealed class BaseMappedPage : ContentPage;

    private sealed class DerivedMappedPage : ContentPage;

    private sealed class ModuleMappedPage : ContentPage;

    private sealed class ModulePages : IMauiRoutePageModule
    {
        public void MapPages(MauiRoutePageRegistry pages)
        {
            pages.MapPage<ModulePageRoute>((_, _) => new ModuleMappedPage());
        }
    }

    private sealed class ServiceResolvedPage(PageDependency dependency) : ContentPage
    {
        public PageDependency Dependency { get; } = dependency;
    }

    private sealed class ScopedPage(ScopedMarker marker) : ContentPage
    {
        public ScopedMarker Marker { get; } = marker;
    }

    private sealed class PresentationPage(ScopedMarker marker) : ContentPage
    {
        public ScopedMarker Marker { get; } = marker;
    }

    private sealed class AsyncScopedPage(AsyncScopedMarker marker) : ContentPage
    {
        public AsyncScopedMarker Marker { get; } = marker;
    }

    private sealed class ScopedFailurePage(TrackedAsyncDisposable marker) : ContentPage
    {
        public TrackedAsyncDisposable Marker { get; } = marker;
    }

    private sealed class IndependentBindingPage : ContentPage
    {
        public IndependentBindingPage()
        {
            BindingContext = OwnBindingContext;
        }

        public object OwnBindingContext { get; } = new();
    }

    private sealed class ScopedMarker : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class AsyncScopedMarker : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailureCleanupTracker
    {
        public int DisposeCount { get; set; }
    }

    private sealed class TrackedAsyncDisposable(FailureCleanupTracker tracker) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            tracker.DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LifecycleTracker
    {
        private readonly List<string> _events = new();

        public IReadOnlyList<string> Events => _events;

        public void Add(string value)
        {
            _events.Add(value);
        }
    }

    private sealed class RecordingLifecycleHook(
        LifecycleTracker tracker,
        ScopedMarker marker)
        : IMauiRoutePageLifecycleHook
    {
        public ValueTask OnPageCreatedAsync(
            Page page,
            RouteEntry entry,
            CancellationToken cancellationToken = default)
        {
            tracker.Add($"created:{entry.Id}");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnPageUpdatedAsync(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            CancellationToken cancellationToken = default)
        {
            tracker.Add($"updated:{context.ReuseKind}:{entry.Id}");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnPageReleasedAsync(Page page, CancellationToken cancellationToken = default)
        {
            tracker.Add($"released:{marker.IsDisposed}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AsyncRecordingLifecycleHook(
        LifecycleTracker tracker,
        AsyncScopedMarker marker)
        : IMauiRoutePageLifecycleHook
    {
        public ValueTask OnPageCreatedAsync(
            Page page,
            RouteEntry entry,
            CancellationToken cancellationToken = default)
        {
            tracker.Add($"async-created:{entry.Id}");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnPageUpdatedAsync(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OnPageReleasedAsync(Page page, CancellationToken cancellationToken = default)
        {
            tracker.Add($"async-released:{marker.DisposeCount}");
            return ValueTask.CompletedTask;
        }
    }
}

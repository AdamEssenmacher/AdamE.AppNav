using AdamE.AppNav.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiRoutePageFactoryTests
{
    [Fact]
    public void CreatePage_AddModuleMapsPagesAndReturnsRegistry()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();

        var returned = options.Pages.AddModule(new ModulePages());
        var factory = new MauiRoutePageFactory(provider, options);

        var page = factory.CreatePage(new RouteEntry("module-route", new ModulePageRoute()));

        Assert.Same(options.Pages, returned);
        Assert.IsType<ModuleMappedPage>(page);
    }

    [Fact]
    public void CreatePage_MapPageFromServices_ResolvesPageFromDI()
    {
        var services = new ServiceCollection();
        services.AddSingleton<PageDependency>();
        services.AddTransient<ServiceResolvedPage>();
        using var provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();
        options.Pages.MapPageFromServices<TestPageRoute, ServiceResolvedPage>();
        var factory = new MauiRoutePageFactory(provider, options);

        var page = Assert.IsType<ServiceResolvedPage>(factory.CreatePage(Entry("route-1")));

        Assert.NotNull(page.Dependency);
    }

    [Fact]
    public void LifecycleHooks_RunForCreateUpdateAndReleaseBeforeScopedServicesAreDisposed()
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

        var page = Assert.IsType<ScopedPage>(factory.CreatePage(Entry("route-1")));
        factory.UpdatePage(
            page,
            Entry("route-2"),
            new MauiRoutePageUpdateContext(MauiRoutePageReuseKind.ExplicitTarget));
        factory.ReleasePage(page);

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
    public void PresentationPage_InheritsOwnerBindingContextAndDisposesItsOwnScopeOnRelease()
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

        var page = Assert.IsType<PresentationPage>(factory.CreatePresentationPage(
            typeof(PresentationPage),
            owner,
            inheritBindingContext: true));

        Assert.Same(ownerBindingContext, page.BindingContext);
        Assert.False(page.Marker.IsDisposed);

        factory.ReleasePresentationPage(page);

        Assert.Null(page.BindingContext);
        Assert.True(page.Marker.IsDisposed);
    }

    [Fact]
    public void PresentationPage_CanKeepItsOwnBindingContext()
    {
        var services = new ServiceCollection();
        services.AddTransient<IndependentBindingPage>();
        using var provider = services.BuildServiceProvider();
        var factory = new MauiRoutePageFactory(provider, new MauiRoutePresentationOptions());
        var owner = new ContentPage { BindingContext = new object() };

        var page = Assert.IsType<IndependentBindingPage>(factory.CreatePresentationPage(
            typeof(IndependentBindingPage),
            owner,
            inheritBindingContext: false));

        Assert.Same(page.OwnBindingContext, page.BindingContext);

        factory.ReleasePresentationPage(page);

        Assert.Same(page.OwnBindingContext, page.BindingContext);
    }

    [Fact]
    public void CreatePage_PrefersMostSpecificMappedPageForDerivedRoute()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();
        options.Pages
            .MapPage<BaseMappedRoute>((_, _) => new BaseMappedPage())
            .MapPage<DerivedMappedRoute>((_, _) => new DerivedMappedPage());
        var factory = new MauiRoutePageFactory(provider, options);

        var page = factory.CreatePage(new RouteEntry("derived-route", new DerivedMappedRoute()));

        Assert.IsType<DerivedMappedPage>(page);
    }

    [Fact]
    public void CreatePage_FallsBackToBaseMappedPageWhenDerivedRouteMappingIsMissing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();
        options.Pages.MapPage<BaseMappedRoute>((_, _) => new BaseMappedPage());
        var factory = new MauiRoutePageFactory(provider, options);

        var page = factory.CreatePage(new RouteEntry("derived-route", new DerivedMappedRoute()));

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
        public void OnPageCreated(Page page, RouteEntry entry, IServiceProvider pageServices)
        {
            tracker.Add($"created:{entry.Id}");
        }

        public void OnPageUpdated(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            IServiceProvider pageServices)
        {
            tracker.Add($"updated:{context.ReuseKind}:{entry.Id}");
        }

        public void OnPageReleased(Page page, IServiceProvider pageServices)
        {
            tracker.Add($"released:{marker.IsDisposed}");
        }
    }
}

using AdamE.MauiRouter.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui.Tests;

public sealed class MauiRoutePageFactoryTests
{
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

    private static RouteEntry Entry(string id)
    {
        return new RouteEntry(id, new TestPageRoute(id));
    }

    private sealed class PageDependency;

    private sealed class ServiceResolvedPage(PageDependency dependency) : ContentPage
    {
        public PageDependency Dependency { get; } = dependency;
    }

    private sealed class ScopedPage(ScopedMarker marker) : ContentPage
    {
        public ScopedMarker Marker { get; } = marker;
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

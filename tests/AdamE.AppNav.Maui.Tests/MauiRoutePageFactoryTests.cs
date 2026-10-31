using AdamE.AppNav.Leases;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
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
        var factory = TestEntryLeases.Factory(provider, options);

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
        var factory = TestEntryLeases.Factory(provider, options);

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
        RouteEntryLeaseRegistry leases = TestEntryLeases.Create();
        var factory = TestEntryLeases.Factory(provider, options, leases);

        var page = Assert.IsType<ScopedPage>(await factory.CreatePageAsync(Entry("route-1")));
        await factory.UpdatePageAsync(
            page,
            Entry("route-2"),
            new MauiRoutePageUpdateContext(MauiRoutePageReuseKind.ExplicitTarget));
        await factory.ReleasePageAsync(page);
        Assert.False(page.Marker.IsDisposed);
        await TestEntryLeases.RetireAndReleaseAsync(leases, "route-1");

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
        RouteEntryLeaseRegistry leases = TestEntryLeases.Create();
        var factory = TestEntryLeases.Factory(provider, options, leases);

        var page = Assert.IsType<AsyncScopedPage>(await factory.CreatePageAsync(Entry("async")));
        page.BindingContext = new object();
        await factory.ReleasePageAsync(page);
        await factory.ReleasePageAsync(page);
        await TestEntryLeases.RetireAndReleaseAsync(leases, "async");

        Assert.Equal(1, page.Marker.DisposeCount);
        Assert.Null(page.BindingContext);
        Assert.Equal(
            ["async-created:async", "async-released:0"],
            provider.GetRequiredService<LifecycleTracker>().Events);
    }

    [Fact]
    public async Task PagesOfOneEntryShareItsScopeUntilTheEntryLeaseIsReleased()
    {
        var services = new ServiceCollection();
        services.AddSingleton<LifecycleTracker>();
        services.AddScoped<AsyncScopedMarker>();
        services.AddTransient<AsyncScopedPage>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions { UseScopedPages = true };
        options.Pages.MapPageFromServices<TestPageRoute, AsyncScopedPage>();
        RouteEntryLeaseRegistry leases = TestEntryLeases.Create();
        var factory = TestEntryLeases.Factory(provider, options, leases);

        var first = Assert.IsType<AsyncScopedPage>(await factory.CreatePageAsync(Entry("entry")));
        await factory.ReleasePageAsync(first);
        var rebuilt = Assert.IsType<AsyncScopedPage>(await factory.CreatePageAsync(Entry("entry")));
        var other = Assert.IsType<AsyncScopedPage>(await factory.CreatePageAsync(Entry("other")));

        Assert.Same(first.Marker, rebuilt.Marker);
        Assert.NotSame(first.Marker, other.Marker);
        Assert.True(leases.TryGet("entry", out RouteEntryLease? lease));
        lease.Retire();
        Assert.Equal(0, first.Marker.DisposeCount);

        await factory.ReleasePageAsync(rebuilt);
        await lease.Released.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, first.Marker.DisposeCount);
        Assert.Equal(0, other.Marker.DisposeCount);
    }

    [Fact]
    public async Task RetiredCommittedEntryGetsAPlaceholderAndRetiredPendingEntryIsRefused()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();
        options.Pages.MapPage<TestPageRoute>((_, _) => new ScopedRoutePage());
        RouteEntryLeaseRegistry leases = TestEntryLeases.Create();
        var committed = new NavigationState(
            [new WindowNode("main", new StackNode("stack", [Entry("committed")]))],
            "main");
        leases.Bind(committed);
        var factory = TestEntryLeases.Factory(provider, options, leases);
        RouteEntryLease committedLease = leases.GetOrOpen("committed", out _);
        committedLease.Close();
        committedLease.Retire();
        // An uncommitted lease is current only until it is released, so tracked work keeps this one unreleased.
        var preparation = new TaskCompletionSource();
        RouteEntryLease pending = leases.Open("pending");
        pending.Track(preparation.Task);
        pending.Retire();

        Page placeholder = await factory.CreatePageAsync(Entry("committed"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.CreatePageAsync(Entry("pending")).AsTask());

        Assert.IsNotType<ScopedRoutePage>(placeholder);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory
            .CreatePresentationPageAsync(typeof(ScopedRoutePage), placeholder, inheritBindingContext: true)
            .AsTask());
        await factory.ReleasePageAsync(placeholder);
        preparation.SetResult();
        pending.Close();
        await pending.Released.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<ScopedRoutePage>(await factory.CreatePageAsync(Entry("pending")));
    }

    [Fact]
    public async Task PageFactoryThatReturnsALivePageAgainIsRejectedWithoutReleasingIt()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();
        var shared = new ScopedRoutePage();
        options.Pages.MapPage<TestPageRoute>((_, _) => shared);
        RouteEntryLeaseRegistry leases = TestEntryLeases.Create();
        var factory = TestEntryLeases.Factory(provider, options, leases);
        Page first = await factory.CreatePageAsync(Entry("first"));

        await Assert.ThrowsAsync<AppNavigationConfigurationException>(() =>
            factory.CreatePageAsync(Entry("second")).AsTask());

        Assert.Same(shared, first);
        Assert.True(leases.TryGet("first", out RouteEntryLease? lease));
        lease.Retire();
        await factory.ReleasePageAsync(first);
        await lease.Released.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ReleasingOutstandingPagesDetachesEveryArtifactAtShutdown()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();
        options.Pages.MapPage<TestPageRoute>((_, _) => new ScopedRoutePage());
        RouteEntryLeaseRegistry leases = TestEntryLeases.Create();
        var factory = TestEntryLeases.Factory(provider, options, leases);
        Page lost = await factory.CreatePageAsync(Entry("lost"));
        Assert.True(leases.TryGet("lost", out RouteEntryLease? lease));
        lease.Retire();
        await Task.Delay(20);
        Assert.False(lease.Released.IsCompleted);

        await factory.ReleaseOutstandingPagesAsync();

        await lease.Released.WaitAsync(TimeSpan.FromSeconds(10));
        await factory.ReleasePageAsync(lost);
    }

    [Fact]
    public async Task PresentationPageHoldsItsOwnersLease()
    {
        var services = new ServiceCollection();
        services.AddTransient<ScopedRoutePage>();
        services.AddTransient<ScopedRoutePresentationPage>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions { UseScopedPages = true };
        options.Pages.MapPageFromServices<TestPageRoute, ScopedRoutePage>();
        RouteEntryLeaseRegistry leases = TestEntryLeases.Create();
        var factory = TestEntryLeases.Factory(provider, options, leases);
        Page owner = await factory.CreatePageAsync(Entry("owner"));
        Page presentation = await factory.CreatePresentationPageAsync(
            typeof(ScopedRoutePresentationPage),
            owner,
            inheritBindingContext: false);
        Assert.True(leases.TryGet("owner", out RouteEntryLease? lease));
        lease.Retire();

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory
            .CreatePresentationPageAsync(typeof(ScopedRoutePresentationPage), owner, inheritBindingContext: false)
            .AsTask());
        await factory.ReleasePageAsync(owner);
        await Task.Delay(20);
        Assert.False(lease.Released.IsCompleted);

        await factory.ReleasePresentationPageAsync(presentation);
        await lease.Released.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public Task AsyncCreateHookCompletion_RestoresMainThreadBeforeNextHook()
    {
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            Assert.True(MainThread.IsMainThread);
            var gate = new GatedLifecycleHook(LifecyclePhase.Created);
            var recorder = new MainThreadRecordingLifecycleHook();
            using ServiceProvider provider = CreateThreadAffinityProvider(gate, recorder);
            MauiRoutePageFactory factory = CreateThreadAffinityFactory(provider);

            Task<Page> createTask = factory.CreatePageAsync(Entry("create-thread-affinity")).AsTask();
            await CompleteGateFromWorkerAsync(gate);
            await createTask;

            Assert.True(recorder.CreatedOnMainThread);
        });
    }

    [Fact]
    public Task AsyncUpdateHookCompletion_RestoresMainThreadBeforeNextHook()
    {
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            Assert.True(MainThread.IsMainThread);
            var gate = new GatedLifecycleHook(LifecyclePhase.Updated);
            var recorder = new MainThreadRecordingLifecycleHook();
            using ServiceProvider provider = CreateThreadAffinityProvider(gate, recorder);
            MauiRoutePageFactory factory = CreateThreadAffinityFactory(provider);
            Page page = await factory.CreatePageAsync(Entry("update-thread-affinity"));

            Task updateTask = factory.UpdatePageAsync(
                page,
                Entry("updated-thread-affinity"),
                new MauiRoutePageUpdateContext(MauiRoutePageReuseKind.ExplicitTarget)).AsTask();
            await CompleteGateFromWorkerAsync(gate);
            await updateTask;

            Assert.True(recorder.UpdatedOnMainThread);
        });
    }

    [Fact]
    public Task AsyncReleaseHookCompletion_RestoresMainThreadBeforeNextHookAndBindingContextCleanup()
    {
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            Assert.True(MainThread.IsMainThread);
            var gate = new GatedLifecycleHook(LifecyclePhase.Released);
            var recorder = new MainThreadRecordingLifecycleHook();
            using ServiceProvider provider = CreateThreadAffinityProvider(gate, recorder);
            MauiRoutePageFactory factory = CreateThreadAffinityFactory(provider);
            Page page = await factory.CreatePageAsync(Entry("release-thread-affinity"));
            page.BindingContext = new object();
            bool? bindingContextClearedOnMainThread = null;
            page.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BindableObject.BindingContext) && page.BindingContext is null)
                    bindingContextClearedOnMainThread = MainThread.IsMainThread;
            };

            Task releaseTask = factory.ReleasePageAsync(page).AsTask();
            await CompleteGateFromWorkerAsync(gate);
            await releaseTask;

            Assert.True(recorder.ReleasedOnMainThread);
            Assert.True(bindingContextClearedOnMainThread);
        });
    }

    [Fact]
    public async Task LifecycleHookResolutionFailureLeavesTheScopeToTheEntryLease()
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
        RouteEntryLeaseRegistry leases = TestEntryLeases.Create();
        var factory = TestEntryLeases.Factory(provider, options, leases);

        await Assert.ThrowsAsync<InvalidOperationException>(() => factory
            .CreatePageAsync(Entry("failure"))
            .AsTask());

        // The scope belongs to the entry's lease, which core abandons after the failed presentation.
        Assert.Equal(0, provider.GetRequiredService<FailureCleanupTracker>().DisposeCount);
        await TestEntryLeases.RetireAndReleaseAsync(leases, "failure");
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
        var factory = TestEntryLeases.Factory(provider, options);
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
        var factory = TestEntryLeases.Factory(provider, new MauiRoutePresentationOptions());
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
        var factory = TestEntryLeases.Factory(provider, options);

        var page = await factory.CreatePageAsync(new RouteEntry("derived-route", new DerivedMappedRoute()));

        Assert.IsType<DerivedMappedPage>(page);
    }

    [Fact]
    public async Task CreatePage_FallsBackToBaseMappedPageWhenDerivedRouteMappingIsMissing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var options = new MauiRoutePresentationOptions();
        options.Pages.MapPage<BaseMappedRoute>((_, _) => new BaseMappedPage());
        var factory = TestEntryLeases.Factory(provider, options);

        var page = await factory.CreatePageAsync(new RouteEntry("derived-route", new DerivedMappedRoute()));

        Assert.IsType<BaseMappedPage>(page);
    }

    private static RouteEntry Entry(string id)
    {
        return new RouteEntry(id, new TestPageRoute(id));
    }

    private static ServiceProvider CreateThreadAffinityProvider(
        GatedLifecycleHook gate,
        MainThreadRecordingLifecycleHook recorder)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMauiRoutePageLifecycleHook>(gate);
        services.AddSingleton<IMauiRoutePageLifecycleHook>(recorder);
        return services.BuildServiceProvider();
    }

    private static MauiRoutePageFactory CreateThreadAffinityFactory(ServiceProvider provider)
    {
        var options = new MauiRoutePresentationOptions();
        options.Pages.MapPage<TestPageRoute>((_, _) => new ContentPage());
        return TestEntryLeases.Factory(provider, options);
    }

    private static async Task CompleteGateFromWorkerAsync(GatedLifecycleHook gate)
    {
        await gate.Entered;
        bool completedOnMainThread = true;
        await Task.Run(() =>
        {
            completedOnMainThread = MainThread.IsMainThread;
            gate.Complete();
        });
        Assert.False(completedOnMainThread);
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

    private sealed class ScopedRoutePage : ContentPage;

    private sealed class ScopedRoutePresentationPage : ContentPage;

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

    private enum LifecyclePhase
    {
        Created,
        Updated,
        Released
    }

    private sealed class GatedLifecycleHook(LifecyclePhase gatedPhase) : IMauiRoutePageLifecycleHook
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Complete()
        {
            _completion.TrySetResult();
        }

        public ValueTask OnPageCreatedAsync(
            Page page,
            RouteEntry entry,
            CancellationToken cancellationToken = default)
        {
            return InvokeAsync(LifecyclePhase.Created);
        }

        public ValueTask OnPageUpdatedAsync(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            CancellationToken cancellationToken = default)
        {
            return InvokeAsync(LifecyclePhase.Updated);
        }

        public ValueTask OnPageReleasedAsync(Page page, CancellationToken cancellationToken = default)
        {
            return InvokeAsync(LifecyclePhase.Released);
        }

        private ValueTask InvokeAsync(LifecyclePhase phase)
        {
            if (phase != gatedPhase)
                return ValueTask.CompletedTask;

            _entered.TrySetResult();
            return new ValueTask(_completion.Task);
        }
    }

    private sealed class MainThreadRecordingLifecycleHook : IMauiRoutePageLifecycleHook
    {
        public bool CreatedOnMainThread { get; private set; }

        public bool UpdatedOnMainThread { get; private set; }

        public bool ReleasedOnMainThread { get; private set; }

        public ValueTask OnPageCreatedAsync(
            Page page,
            RouteEntry entry,
            CancellationToken cancellationToken = default)
        {
            CreatedOnMainThread = MainThread.IsMainThread;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnPageUpdatedAsync(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            CancellationToken cancellationToken = default)
        {
            UpdatedOnMainThread = MainThread.IsMainThread;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnPageReleasedAsync(Page page, CancellationToken cancellationToken = default)
        {
            ReleasedOnMainThread = MainThread.IsMainThread;
            return ValueTask.CompletedTask;
        }
    }
}

using System.Runtime.CompilerServices;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui.Tests;

public sealed class MauiPageCollectionTests
{
    [Fact]
    public async Task RepeatedRootReplacementDoesNotRetainReleasedPagesBindingsScopesOrHooks()
    {
        WeakReference[] references = await RunReplacementCyclesAsync();

        for (var attempt = 0; attempt < 8 && references.Any(static reference => reference.IsAlive); attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            await Task.Delay(10);
        }

        Assert.All(references, static reference => Assert.False(reference.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> RunReplacementCyclesAsync()
    {
        var references = new List<WeakReference>();
        var services = new ServiceCollection();
        services.AddScoped<CollectibleScope>();
        services.AddScoped<CollectibleLifecycleHook>();
        services.AddScoped<IMauiRoutePageLifecycleHook>(provider =>
            provider.GetRequiredService<CollectibleLifecycleHook>());
        await using ServiceProvider provider = services.BuildServiceProvider();
        var options = new MauiRoutePresentationOptions { UseScopedPages = true };
        options.Pages.MapPage<TestRoute>((pageServices, _) =>
        {
            var scope = pageServices.GetRequiredService<CollectibleScope>();
            var hook = pageServices.GetRequiredService<CollectibleLifecycleHook>();
            var bindingContext = new object();
            var page = new ContentPage { BindingContext = bindingContext };
            references.Add(new WeakReference(page));
            references.Add(new WeakReference(bindingContext));
            references.Add(new WeakReference(scope));
            references.Add(new WeakReference(hook));
            return page;
        });
        var presenter = new MauiNavigationPresenter(
            new MauiRoutePageFactory(provider, options),
            presentationOptions: options);
        NavigationState currentState = NavigationState.Empty;

        for (var index = 0; index < 30; index++)
        {
            var route = new TestRoute(index);
            var targetState = new NavigationState(
                [
                    new WindowNode(
                        "main",
                        new StackNode(
                            $"stack-{index}",
                            [new RouteEntry($"route-{index}", route)]))
                ],
                "main");
            await presenter.ApplyAsync(
                new NavigationPlan(targetState),
                new NavigationPresentationContext(
                    RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Test),
                    route,
                    currentState,
                    Guid.NewGuid().ToString("N")));
            currentState = targetState;
        }

        await presenter.DisposeAsync();
        return references.ToArray();
    }

    private sealed record TestRoute(int Id) : AppRoute;

    private sealed class CollectibleScope : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CollectibleLifecycleHook(CollectibleScope scope) : IMauiRoutePageLifecycleHook
    {
        private readonly CollectibleScope _scope = scope;

        public ValueTask OnPageCreatedAsync(
            Page page,
            RouteEntry entry,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OnPageUpdatedAsync(
            Page page,
            RouteEntry entry,
            MauiRoutePageUpdateContext context,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OnPageReleasedAsync(Page page, CancellationToken cancellationToken = default)
        {
            GC.KeepAlive(_scope);
            return ValueTask.CompletedTask;
        }
    }
}

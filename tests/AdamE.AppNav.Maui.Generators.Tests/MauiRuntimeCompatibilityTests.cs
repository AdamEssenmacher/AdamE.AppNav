using AdamE.AppNav.Maui.DependencyInjection;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AdamE.AppNav.Maui.Generators.Tests;

public sealed class MauiRuntimeCompatibilityTests
{
    [Fact]
    public async Task RegisteredRuntimeForwardsRefreshCancellationWithoutChangingStateOrHistory()
    {
        await using ServiceProvider provider = CreateProvider();
        IRouterNavigator runtime = provider.GetRequiredService<IRouterNavigator>();
        var state = runtime.CurrentState;
        var history = runtime.History;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.RefreshPresentationAsync(cancellation.Token).AsTask());

        Assert.Same(state, runtime.CurrentState);
        Assert.Same(history, runtime.History);
    }

    [Fact]
    public async Task RegisteredRuntimeForwardsRefreshToDisposedNavigator()
    {
        ServiceProvider provider = CreateProvider();
        IRouterNavigator runtime = provider.GetRequiredService<IRouterNavigator>();
        await provider.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            runtime.RefreshPresentationAsync().AsTask());
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddAppNav<UnreachablePlanner>(
            RouteTable.Create(routes => routes.MapRoute<TestRoute>("/runtime-test")),
            _ => { });
        return services.BuildServiceProvider();
    }

    private sealed record TestRoute : AppRoute;

    private sealed class UnreachablePlanner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refresh must not invoke the planner.");
    }
}

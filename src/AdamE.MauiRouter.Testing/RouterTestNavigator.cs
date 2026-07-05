using AdamE.MauiRouter.Back;
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Testing;

public sealed class RouterTestNavigatorOptions
{
    public NavigationState? InitialState { get; set; }

    public IReadOnlyList<INavigationRequestPolicy> RequestPolicies { get; set; } = Array.Empty<INavigationRequestPolicy>();

    public Func<NavigationFallbackContext, AppRoute?>? FallbackRouteFactory { get; set; }

    public NavigationDiagnostics? Diagnostics { get; set; }

    public IBackNavigator? BackNavigator { get; set; }

    public int MaxRedirects { get; set; } = 16;

    public int MaxHistoryEntries { get; set; } = 128;
}

public static class RouterTestNavigator
{
    public static IRouterNavigator Create(
        RouteTable routes,
        IAppNavigationPlanner planner,
        RouterTestNavigatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(planner);

        options ??= new RouterTestNavigatorOptions();
        return new RouterNavigator(
            routes,
            planner,
            NullNavigationPresenter.Instance,
            new RouterNavigatorOptions
            {
                InitialState = options.InitialState,
                RequestPolicies = options.RequestPolicies,
                FallbackRouteFactory = options.FallbackRouteFactory,
                Diagnostics = options.Diagnostics,
                BackNavigator = options.BackNavigator,
                MaxRedirects = options.MaxRedirects,
                MaxHistoryEntries = options.MaxHistoryEntries
            });
    }

    public static IRouterNavigator Create(
        RouteTable routes,
        Func<NavigationPlanningContext, CancellationToken, ValueTask<NavigationPlan>> createPlan,
        RouterTestNavigatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(createPlan);

        return Create(routes, new TestNavigationPlanner(createPlan), options);
    }
}

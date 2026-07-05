using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Routing;

namespace AdamE.MauiRouter.Navigation;

/// <summary>
/// Creates router navigator instances for host adapters and advanced composition roots.
/// </summary>
public static class RouterNavigatorFactory
{
    /// <summary>
    /// Creates a router navigator from the supplied routing table, planner, presenter, and optional
    /// composition settings.
    /// </summary>
    /// <param name="routes">The route table used to match URI requests.</param>
    /// <param name="planner">The application planner that creates navigation plans for accepted routes.</param>
    /// <param name="presenter">The host presenter that applies accepted navigation plans.</param>
    /// <param name="options">Optional navigator composition settings.</param>
    /// <returns>A router navigator backed by the core MauiRouter implementation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="routes"/>, <paramref name="planner"/>, or <paramref name="presenter"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static IRouterNavigator Create(
        RouteTable routes,
        IAppNavigationPlanner planner,
        INavigationPresenter presenter,
        RouterNavigatorFactoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(presenter);

        options ??= new RouterNavigatorFactoryOptions();
        return new RouterNavigator(
            routes,
            planner,
            presenter,
            new RouterNavigatorOptions
            {
                InitialState = options.InitialState,
                InitialHistory = options.InitialHistory,
                RequestPolicies = options.RequestPolicies ?? Array.Empty<INavigationRequestPolicy>(),
                FallbackRouteFactory = options.FallbackRouteFactory,
                Diagnostics = options.Diagnostics,
                BackNavigator = options.BackNavigator,
                MaxRedirects = options.MaxRedirects,
                MaxHistoryEntries = options.MaxHistoryEntries,
                Logger = options.Logger,
                LoggerFactory = options.LoggerFactory
            });
    }
}

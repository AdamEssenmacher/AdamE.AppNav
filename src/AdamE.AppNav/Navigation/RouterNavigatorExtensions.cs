using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Navigation;

/// <summary>
/// Provides the golden-path typed navigation operations for application code.
/// </summary>
public static class RouterNavigatorExtensions
{
#pragma warning disable RS0026 // The preview deliberately exposes exactly four paired typed convenience overloads.
    public static ValueTask<NavigationResult> NavigateAsync(
        this IRouterNavigator navigator,
        AppRoute route,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        return navigator.NavigateAsync(
            RouterNavigationRequest.FromRoute(
                route,
                NavigationRequestSource.InAppCommand,
                disposition: RouterNavigationDisposition.Auto),
            cancellationToken);
    }

    public static ValueTask<NavigationResult> NavigateAsync(
        this IRouterNavigator navigator,
        AppRoute route,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        return navigator.NavigateAsync(
            RouterNavigationRequest.FromRoute(
                route,
                NavigationRequestSource.InAppCommand,
                disposition: disposition),
            cancellationToken);
    }

    public static ValueTask<NavigationResult> NavigateAsync(
        this IRouterNavigator navigator,
        AppRouteRequest routeRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        return navigator.NavigateAsync(
            RouterNavigationRequest.FromRouteRequest(
                routeRequest,
                NavigationRequestSource.InAppCommand,
                disposition: RouterNavigationDisposition.Auto),
            cancellationToken);
    }

    public static ValueTask<NavigationResult> NavigateAsync(
        this IRouterNavigator navigator,
        AppRouteRequest routeRequest,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        return navigator.NavigateAsync(
            RouterNavigationRequest.FromRouteRequest(
                routeRequest,
                NavigationRequestSource.InAppCommand,
                disposition: disposition),
            cancellationToken);
    }
#pragma warning restore RS0026
}

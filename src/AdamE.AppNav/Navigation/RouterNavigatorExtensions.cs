using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Navigation;

/// <summary>
/// Provides the golden-path typed navigation operations for application code.
/// </summary>
public static class RouterNavigatorExtensions
{
#pragma warning disable RS0026 // The preview deliberately exposes exactly four paired typed convenience overloads.
    /// <summary>Navigates to a typed route.</summary>
    /// <remarks>Await this operation. Blocking the UI thread can deadlock if presentation needs that thread.</remarks>
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

    /// <summary>Navigates to a typed route with the specified disposition.</summary>
    /// <remarks>Await this operation. Blocking the UI thread can deadlock if presentation needs that thread.</remarks>
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

    /// <summary>Navigates using a typed route request.</summary>
    /// <remarks>Await this operation. Blocking the UI thread can deadlock if presentation needs that thread.</remarks>
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

    /// <summary>Navigates using a typed route request with the specified disposition.</summary>
    /// <remarks>Await this operation. Blocking the UI thread can deadlock if presentation needs that thread.</remarks>
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

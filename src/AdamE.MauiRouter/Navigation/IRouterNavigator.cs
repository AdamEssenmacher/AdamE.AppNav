using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Navigation;

/// <summary>
/// Executes navigation requests and exposes the router's current navigation state.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IRouterNavigator"/> is the primary app-facing runtime API for issuing navigation
/// commands after routes, policies, and presentation services have been configured.
/// </para>
/// <para>
/// The returned <see cref="ValueTask{TResult}"/> values represent single navigation operations.
/// Await them directly, or call <see cref="ValueTask{TResult}.AsTask"/> before storing, caching,
/// or composing an operation with multiple awaits.
/// </para>
/// </remarks>
public interface IRouterNavigator
{
    /// <summary>
    /// Gets the router's latest accepted navigation state.
    /// </summary>
    NavigationState CurrentState { get; }

    /// <summary>
    /// Gets the logical navigation history captured by completed navigation operations.
    /// </summary>
    NavigationHistory History { get; }

    /// <summary>
    /// Occurs after the router accepts an operation and commits the resulting state and history.
    /// </summary>
    /// <remarks>
    /// The event is raised once per accepted commit, after the router operation lock has been
    /// released. It is not raised for rejected, unhandled, cancelled, or failed operations.
    /// </remarks>
    event EventHandler<NavigationCommittedEventArgs>? NavigationCommitted;

    /// <summary>
    /// Navigates from a URI using the specified request source.
    /// </summary>
    /// <param name="uri">The URI to match against the configured route table.</param>
    /// <param name="source">The origin of the navigation request.</param>
    /// <param name="cancellationToken">A token that can cancel the navigation operation.</param>
    /// <returns>A value task that completes with the final navigation result.</returns>
    ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates from a URI using the specified request source and presentation disposition.
    /// </summary>
    /// <param name="uri">The URI to match against the configured route table.</param>
    /// <param name="source">The origin of the navigation request.</param>
    /// <param name="disposition">A hint that controls how the resolved route should be presented.</param>
    /// <param name="cancellationToken">A token that can cancel the navigation operation.</param>
    /// <returns>A value task that completes with the final navigation result.</returns>
    ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates from a URI using the default in-app request source and the specified presentation disposition.
    /// </summary>
    /// <param name="uri">The URI to match against the configured route table.</param>
    /// <param name="disposition">A hint that controls how the resolved route should be presented.</param>
    /// <param name="cancellationToken">A token that can cancel the navigation operation.</param>
    /// <returns>A value task that completes with the final navigation result.</returns>
    ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates directly to a typed application route using the specified request source.
    /// </summary>
    /// <param name="route">The typed route to present.</param>
    /// <param name="source">The origin of the navigation request.</param>
    /// <param name="cancellationToken">A token that can cancel the navigation operation.</param>
    /// <returns>A value task that completes with the final navigation result.</returns>
    ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates directly to a typed application route using the specified request source and presentation disposition.
    /// </summary>
    /// <param name="route">The typed route to present.</param>
    /// <param name="source">The origin of the navigation request.</param>
    /// <param name="disposition">A hint that controls how the route should be presented.</param>
    /// <param name="cancellationToken">A token that can cancel the navigation operation.</param>
    /// <returns>A value task that completes with the final navigation result.</returns>
    ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates directly to a typed application route using the default in-app request source and the
    /// specified presentation disposition.
    /// </summary>
    /// <param name="route">The typed route to present.</param>
    /// <param name="disposition">A hint that controls how the route should be presented.</param>
    /// <param name="cancellationToken">A token that can cancel the navigation operation.</param>
    /// <returns>A value task that completes with the final navigation result.</returns>
    ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates using an app-facing route request and the specified request source.
    /// </summary>
    /// <param name="routeRequest">The route request, including route metadata supplied by application code.</param>
    /// <param name="source">The origin of the navigation request.</param>
    /// <param name="cancellationToken">A token that can cancel the navigation operation.</param>
    /// <returns>A value task that completes with the final navigation result.</returns>
    ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates using an app-facing route request, request source, and presentation disposition.
    /// </summary>
    /// <param name="routeRequest">The route request, including route metadata supplied by application code.</param>
    /// <param name="source">The origin of the navigation request.</param>
    /// <param name="disposition">A hint that controls how the route should be presented.</param>
    /// <param name="cancellationToken">A token that can cancel the navigation operation.</param>
    /// <returns>A value task that completes with the final navigation result.</returns>
    ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates using an app-facing route request, the default in-app request source, and a presentation disposition.
    /// </summary>
    /// <param name="routeRequest">The route request, including route metadata supplied by application code.</param>
    /// <param name="disposition">A hint that controls how the route should be presented.</param>
    /// <param name="cancellationToken">A token that can cancel the navigation operation.</param>
    /// <returns>A value task that completes with the final navigation result.</returns>
    ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates using the full router request envelope.
    /// </summary>
    /// <param name="request">
    /// The complete navigation request, including URI or route data, source, window scope,
    /// metadata, disposition, and provenance.
    /// </param>
    /// <param name="cancellationToken">A token that can cancel the navigation operation.</param>
    /// <returns>A value task that completes with the final navigation result.</returns>
    /// <remarks>
    /// This overload is intended for integrations that already have runtime navigation metadata,
    /// such as app-link dispatchers, foreground scanners, or other boundary adapters.
    /// </remarks>
    ValueTask<NavigationResult> NavigateAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to perform logical back navigation.
    /// </summary>
    /// <param name="windowId">
    /// The window to navigate within, or <see langword="null"/> to use the router's active window.
    /// </param>
    /// <param name="cancellationToken">A token that can cancel the back-navigation operation.</param>
    /// <returns>
    /// A value task that completes with the back-navigation result, including whether the request
    /// was handled.
    /// </returns>
    ValueTask<BackNavigationResult> BackAsync(
        string? windowId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the router with navigation state observed from the presentation layer.
    /// </summary>
    /// <param name="reconciliation">The observed state, source, route, and reason for reconciliation.</param>
    /// <param name="cancellationToken">A token that can cancel the reconciliation operation.</param>
    /// <returns>A value task that completes with the navigation result produced by reconciliation.</returns>
    /// <remarks>
    /// Use this when native UI or host-level navigation changes state outside a normal router-issued
    /// navigation command.
    /// </remarks>
    ValueTask<NavigationResult> ReconcileAsync(
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores router state from a previously captured navigation snapshot.
    /// </summary>
    /// <param name="snapshot">The persisted navigation snapshot to restore.</param>
    /// <param name="options">
    /// Optional restore behavior, or <see langword="null"/> to use default restore behavior.
    /// </param>
    /// <param name="cancellationToken">A token that can cancel the restore operation.</param>
    /// <returns>A value task that completes with the restore result.</returns>
    ValueTask<NavigationRestoreResult> RestoreAsync(
        NavigationSnapshot snapshot,
        NavigationRestoreOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores router state by loading the latest snapshot from the configured navigation state store.
    /// </summary>
    /// <param name="options">
    /// Optional restore behavior, or <see langword="null"/> to use default restore behavior.
    /// </param>
    /// <param name="cancellationToken">A token that can cancel the restore operation.</param>
    /// <returns>A value task that completes with the restore result.</returns>
    ValueTask<NavigationRestoreResult> RestoreFromStoreAsync(
        NavigationRestoreOptions? options = null,
        CancellationToken cancellationToken = default);
}

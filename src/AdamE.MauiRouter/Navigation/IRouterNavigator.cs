using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Presentation;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Navigation;

/// <summary>
/// Executes navigation requests and exposes the router's current navigation state.
/// </summary>
public interface IRouterNavigator
{
    NavigationState CurrentState { get; }

    NavigationHistory History { get; }

    ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default);

    ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    ValueTask<NavigationResult> NavigateAsync(
        Uri uri,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default);

    ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    ValueTask<NavigationResult> NavigateAsync(
        AppRoute route,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates using an app-facing route request and an explicit request source.
    /// </summary>
    ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        NavigationRequestSource source = NavigationRequestSource.InAppCommand,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates using an app-facing route request, explicit request source, and explicit disposition.
    /// </summary>
    ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        NavigationRequestSource source,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Navigates using an app-facing route request and an explicit disposition.
    /// </summary>
    ValueTask<NavigationResult> NavigateAsync(
        AppRouteRequest routeRequest,
        RouterNavigationDisposition disposition,
        CancellationToken cancellationToken = default);

    ValueTask<NavigationResult> NavigateAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<BackNavigationResult> BackAsync(
        string? windowId = null,
        CancellationToken cancellationToken = default);

    ValueTask<NavigationResult> ReconcileAsync(
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken = default);

    ValueTask<NavigationRestoreResult> RestoreAsync(
        NavigationSnapshot snapshot,
        NavigationRestoreOptions? options = null,
        CancellationToken cancellationToken = default);

    ValueTask<NavigationRestoreResult> RestoreFromStoreAsync(
        NavigationRestoreOptions? options = null,
        CancellationToken cancellationToken = default);

    Task WhenReconciliationIdleAsync();
}

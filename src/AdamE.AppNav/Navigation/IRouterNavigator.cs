using AdamE.AppNav.History;
using AdamE.AppNav.Back;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Navigation;

/// <summary>
/// Executes complete navigation requests and exposes the router's accepted state and history.
/// </summary>
/// <remarks>
/// Typed in-app navigation conveniences are provided by <see cref="RouterNavigatorExtensions"/>.
/// External boundaries construct a complete <see cref="RouterNavigationRequest"/> so source,
/// disposition, provenance, and trust decisions remain explicit.
/// </remarks>
public interface IRouterNavigator : IDisposable, IAsyncDisposable
{
    NavigationState CurrentState { get; }

    NavigationHistory History { get; }

    ValueTask<NavigationResult> NavigateAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<BackNavigationResult> BackAsync(
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        return BackAsync(
            new BackNavigationRequest(windowId, BackNavigationSource.ApplicationCommand),
            cancellationToken);
    }

#pragma warning disable RS0026 // The preview intentionally exposes paired legacy and request-backed Back overloads.
    ValueTask<BackNavigationResult> BackAsync(
        BackNavigationRequest request,
        CancellationToken cancellationToken = default);
#pragma warning restore RS0026

    ValueTask<NavigationResult> ReconcileAsync(
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken = default);
}

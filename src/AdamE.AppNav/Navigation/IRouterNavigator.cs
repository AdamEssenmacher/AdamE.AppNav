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

    /// <summary>Executes a navigation request and returns its outcome.</summary>
    /// <remarks>Await this operation. Blocking the UI thread can deadlock if presentation needs that thread.</remarks>
    ValueTask<NavigationResult> NavigateAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Requests app-authored Back navigation for the specified window.</summary>
    /// <remarks>Await this operation. Blocking the UI thread can deadlock if presentation needs that thread.</remarks>
    ValueTask<BackNavigationResult> BackAsync(
        string? windowId = null,
        CancellationToken cancellationToken = default)
    {
        return BackAsync(
            new BackNavigationRequest(windowId, BackNavigationSource.ApplicationCommand),
            cancellationToken);
    }

#pragma warning disable RS0026 // The preview intentionally exposes paired legacy and request-backed Back overloads.
    /// <summary>Executes a Back navigation request and returns its outcome.</summary>
    /// <remarks>Await this operation. Blocking the UI thread can deadlock if presentation needs that thread.</remarks>
    ValueTask<BackNavigationResult> BackAsync(
        BackNavigationRequest request,
        CancellationToken cancellationToken = default);
#pragma warning restore RS0026

    /// <summary>Records host-observed state the host already shows, ahead of any navigation still queued.</summary>
    /// <remarks>
    /// The reconciliation takes its place in line before this call returns, and it never starts inline in its caller:
    /// presenters call this while holding their own lock. An implementation or decorator must keep both guarantees.
    /// </remarks>
    ValueTask<NavigationResult> ReconcileAsync(
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken = default);

    /// <summary>Reapplies accepted state after native host recreation, without replanning or changing history.</summary>
    /// <remarks>Serialized with navigation and shutdown. The presenter must still verify native completion.</remarks>
    ValueTask RefreshPresentationAsync(CancellationToken cancellationToken = default);
}

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

    /// <summary>Records host-observed state the host already shows, ahead of any navigation still queued.</summary>
    /// <remarks>
    /// The reconciliation takes its place in line before this call returns, and it never starts inline in its caller:
    /// presenters call this while holding their own lock. An implementation or decorator must keep both guarantees.
    /// </remarks>
    ValueTask<NavigationResult> ReconcileAsync(
        NavigationReconciliation reconciliation,
        CancellationToken cancellationToken = default);

    /// <summary>Records how the presenter now shows the split columns of window <paramref name="windowId"/>.</summary>
    /// <remarks>
    /// <para>
    /// This is for presenters and adapters, not application code. A presenter that shows <see cref="SplitNode"/>
    /// columns reports a window's layout before its first apply to that window and whenever the layout changes, such
    /// as when the window is resized or a foldable opens. A window never reported is
    /// <see cref="SplitLayout.Collapsed"/>.
    /// </para>
    /// <para>
    /// The layout is presentation, not logical state: it never changes <see cref="CurrentState"/> or
    /// <see cref="History"/> and is not queued behind navigation. A Back planned after this call returns, including
    /// one already queued, uses it unless its <see cref="BackNavigationRequest.SplitLayout"/> overrides it. A result
    /// and its history entry report the route shown in the layout reported when the operation was planned, which for
    /// a navigation or reconciliation is before the presenter applies it.
    /// </para>
    /// <para>
    /// A layout may be reported for a window the state does not have yet, such as before the first navigation; it is
    /// kept until the next commit, and forgotten then unless the committed state has the window. A layout is also
    /// forgotten once a commit removes its window, so a later window with the same ID starts collapsed until it is
    /// reported again.
    /// </para>
    /// <para>
    /// It is safe to call from any thread. A report admitted before shutdown starts applies, and writes its
    /// <see cref="Diagnostics.NavigationDiagnosticEventKind.SplitLayoutReported"/> diagnostic when the layout changed,
    /// before disposal completes; a report after shutdown starts is ignored. Diagnostics are written outside the
    /// router's locks, so reports from different threads may produce their diagnostics in either order; report from
    /// one thread, normally the UI thread.
    /// </para>
    /// </remarks>
    /// <param name="windowId">The <see cref="WindowNode"/> ID of the window whose layout changed.</param>
    /// <param name="layout">The layout the presenter now shows.</param>
    void ReportSplitLayout(string windowId, SplitLayout layout);

    /// <summary>Reapplies accepted state after native host recreation, without replanning or changing history.</summary>
    /// <remarks>Serialized with navigation and shutdown. The presenter must still verify native completion.</remarks>
    ValueTask RefreshPresentationAsync(CancellationToken cancellationToken = default);
}

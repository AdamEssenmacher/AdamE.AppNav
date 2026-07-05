namespace AdamE.MauiRouter.Diagnostics;

/// <summary>
/// Identifies the logical router phase associated with a diagnostic event.
/// </summary>
public enum NavigationDiagnosticPhase
{
    /// <summary>
    /// A general navigation event that does not belong to a more specific phase.
    /// </summary>
    Navigation,

    /// <summary>
    /// Route table matching, fallback selection, or route parse/validation work.
    /// </summary>
    RouteMatching,

    /// <summary>
    /// Request policy evaluation before a route is planned.
    /// </summary>
    RequestPolicy,

    /// <summary>
    /// App route planning into a target navigation state.
    /// </summary>
    Planning,

    /// <summary>
    /// Presentation of a navigation plan by an adapter or host integration.
    /// </summary>
    Presentation,

    /// <summary>
    /// Deferred request persistence or other storage-backed router work.
    /// </summary>
    Persistence,

    /// <summary>
    /// Startup routing and deferred startup navigation decisions.
    /// </summary>
    Startup,

    /// <summary>
    /// Reconciliation from native or host-observed navigation state back into router state.
    /// </summary>
    Reconciliation,

    /// <summary>
    /// Logical back navigation planning and presentation.
    /// </summary>
    Back,

    /// <summary>
    /// App-link buffering, dispatch, and failure handling.
    /// </summary>
    AppLink,

    /// <summary>
    /// Diagnostics infrastructure, including observer failures.
    /// </summary>
    Diagnostics
}

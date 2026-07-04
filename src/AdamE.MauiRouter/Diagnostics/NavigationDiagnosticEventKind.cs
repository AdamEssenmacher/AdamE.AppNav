namespace AdamE.MauiRouter.Diagnostics;

/// <summary>
/// Identifies the kind of router diagnostic event that occurred.
/// </summary>
public enum NavigationDiagnosticEventKind
{
    /// <summary>
    /// Route matching has started for a URI or route request.
    /// </summary>
    RouteMatchingStarted,

    /// <summary>
    /// A route definition matched the incoming request.
    /// </summary>
    RouteMatched,

    /// <summary>
    /// No route definition matched the incoming request.
    /// </summary>
    RouteNotMatched,

    /// <summary>
    /// A fallback route was selected after route matching did not produce a normal match.
    /// </summary>
    RouteFallbackSelected,

    /// <summary>
    /// Route matching failed because an exception or unrecoverable route diagnostic occurred.
    /// </summary>
    RouteMatchingFailed,

    /// <summary>
    /// Request policy evaluation has started.
    /// </summary>
    RequestPolicyStarted,

    /// <summary>
    /// A request policy redirected the navigation request.
    /// </summary>
    RequestRedirected,

    /// <summary>
    /// Request policy redirects exceeded the allowed redirect limit or formed a redirect loop.
    /// </summary>
    RequestRedirectLoopDetected,

    /// <summary>
    /// Request policy evaluation completed without failure.
    /// </summary>
    RequestPolicyCompleted,

    /// <summary>
    /// Request policy evaluation failed.
    /// </summary>
    RequestPolicyFailed,

    /// <summary>
    /// Planning has started for the resolved route.
    /// </summary>
    PlanningStarted,

    /// <summary>
    /// Planning completed and produced a navigation plan.
    /// </summary>
    PlanningCompleted,

    /// <summary>
    /// Planning failed before a navigation plan could be produced.
    /// </summary>
    PlanningFailed,

    /// <summary>
    /// Plan policy evaluation has started.
    /// </summary>
    PlanPolicyStarted,

    /// <summary>
    /// Plan policy evaluation completed without failure.
    /// </summary>
    PlanPolicyCompleted,

    /// <summary>
    /// Plan policy evaluation failed.
    /// </summary>
    PlanPolicyFailed,

    /// <summary>
    /// Presentation of a navigation plan has started.
    /// </summary>
    PresentationStarted,

    /// <summary>
    /// A presentation adapter created a page or host object.
    /// </summary>
    PresentationPageCreated,

    /// <summary>
    /// A presentation adapter released a page or host object.
    /// </summary>
    PresentationPageReleased,

    /// <summary>
    /// A presentation adapter attached an event handler or lifecycle callback.
    /// </summary>
    PresentationHandlerAttached,

    /// <summary>
    /// A presentation adapter detached an event handler or lifecycle callback.
    /// </summary>
    PresentationHandlerDetached,

    /// <summary>
    /// A presentation adapter or presenter was disposed.
    /// </summary>
    PresentationPresenterDisposed,

    /// <summary>
    /// A presentation transition has started.
    /// </summary>
    PresentationTransitionStarted,

    /// <summary>
    /// A presentation transition completed.
    /// </summary>
    PresentationTransitionCompleted,

    /// <summary>
    /// A presentation transition failed.
    /// </summary>
    PresentationTransitionFailed,

    /// <summary>
    /// Presentation fell back from a requested transition to a simpler native operation.
    /// </summary>
    PresentationTransitionFallback,

    /// <summary>
    /// Presentation completed successfully.
    /// </summary>
    PresentationCompleted,

    /// <summary>
    /// Presentation failed.
    /// </summary>
    PresentationFailed,

    /// <summary>
    /// Saving a navigation snapshot has started.
    /// </summary>
    SnapshotSaveStarted,

    /// <summary>
    /// A navigation snapshot was saved successfully.
    /// </summary>
    SnapshotSaved,

    /// <summary>
    /// Saving a navigation snapshot failed.
    /// </summary>
    SnapshotSaveFailed,

    /// <summary>
    /// Loading a navigation snapshot has started.
    /// </summary>
    SnapshotLoadStarted,

    /// <summary>
    /// A navigation snapshot was loaded successfully.
    /// </summary>
    SnapshotLoaded,

    /// <summary>
    /// Loading a navigation snapshot failed.
    /// </summary>
    SnapshotLoadFailed,

    /// <summary>
    /// Restoring router state from a snapshot has started.
    /// </summary>
    RestoreStarted,

    /// <summary>
    /// Router state was restored from a snapshot.
    /// </summary>
    RestoreCompleted,

    /// <summary>
    /// A restore policy rejected a loaded snapshot.
    /// </summary>
    RestoreRejected,

    /// <summary>
    /// Restoring router state from a snapshot failed.
    /// </summary>
    RestoreFailed,

    /// <summary>
    /// Startup navigation orchestration has started.
    /// </summary>
    StartupStarted,

    /// <summary>
    /// Startup detected an app link that should be handled before restore or fallback navigation.
    /// </summary>
    StartupAppLinkPending,

    /// <summary>
    /// Startup detected a deferred request that should be handled before restore or fallback navigation.
    /// </summary>
    StartupDeferredRequestPending,

    /// <summary>
    /// Startup skipped restoring persisted router state.
    /// </summary>
    StartupRestoreSkipped,

    /// <summary>
    /// Startup navigated to a fallback request.
    /// </summary>
    StartupFallbackNavigated,

    /// <summary>
    /// Startup navigation orchestration completed.
    /// </summary>
    StartupCompleted,

    /// <summary>
    /// Startup navigation orchestration failed.
    /// </summary>
    StartupFailed,

    /// <summary>
    /// Reconciliation from host-observed navigation state has started.
    /// </summary>
    ReconciliationStarted,

    /// <summary>
    /// Reconciliation from host-observed navigation state completed.
    /// </summary>
    ReconciliationCompleted,

    /// <summary>
    /// Reconciliation from host-observed navigation state failed.
    /// </summary>
    ReconciliationFailed,

    /// <summary>
    /// Logical back navigation has started.
    /// </summary>
    BackStarted,

    /// <summary>
    /// A back navigator evaluated whether the current state can handle back navigation.
    /// </summary>
    BackEvaluated,

    /// <summary>
    /// Logical back navigation completed and was handled by the router.
    /// </summary>
    BackCompleted,

    /// <summary>
    /// Logical back navigation was not handled by the router.
    /// </summary>
    BackUnhandled,

    /// <summary>
    /// Logical back navigation failed.
    /// </summary>
    BackFailed,

    /// <summary>
    /// Navigation failed at the top-level operation boundary.
    /// </summary>
    NavigationFailed,

    /// <summary>
    /// An app link was received by a platform adapter or app-link dispatcher.
    /// </summary>
    AppLinkReceived,

    /// <summary>
    /// An app link was buffered until the router can dispatch it.
    /// </summary>
    AppLinkBuffered,

    /// <summary>
    /// An app link was dispatched to the router.
    /// </summary>
    AppLinkDispatched,

    /// <summary>
    /// App-link handling failed.
    /// </summary>
    AppLinkFailed,

    /// <summary>
    /// A diagnostic observer or event handler threw while handling another diagnostic event.
    /// </summary>
    DiagnosticObserverFailed
}

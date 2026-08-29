namespace AdamE.AppNav.Diagnostics;

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
    /// A request transformer or policy redirected the navigation request.
    /// </summary>
    RequestRedirected,

    /// <summary>
    /// Request redirects exceeded the allowed redirect limit or formed a redirect loop.
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
    /// Pre-match request transformation has started.
    /// </summary>
    RequestTransformStarted,

    /// <summary>
    /// Pre-match request transformation completed without failure.
    /// </summary>
    RequestTransformCompleted,

    /// <summary>
    /// Pre-match request transformation failed.
    /// </summary>
    RequestTransformFailed,

    /// <summary>
    /// Presentation of a navigation plan has started.
    /// </summary>
    PresentationStarted = 16,

    /// <summary>
    /// A presentation adapter created a page or host object.
    /// </summary>
    PresentationPageCreated,

    /// <summary>
    /// A presentation adapter released a page or host object.
    /// </summary>
    PresentationPageReleased,

    /// <summary>
    /// A presentation adapter attached an event handler or lifecycle callback to a page or host object.
    /// </summary>
    PresentationHandlerAttached,

    /// <summary>
    /// A presentation adapter detached an event handler or lifecycle callback.
    /// </summary>
    PresentationHandlerDetached,

    /// <summary>
    /// A presentation adapter or presenter was disposed of.
    /// </summary>
    PresentationPresenterDisposed,

    /// <summary>
    /// Presentation completed native operations, but the live host tree did not match the target navigation state.
    /// </summary>
    PresentationVerificationFailed = 60,

    /// <summary>
    /// Presentation completed successfully.
    /// </summary>
    PresentationCompleted = 26,

    /// <summary>
    /// Presentation failed.
    /// </summary>
    PresentationFailed,

    /// <summary>
    /// Startup navigation orchestration has started.
    /// </summary>
    StartupStarted = 38,

    /// <summary>
    /// Startup detected an app link that should be handled before fallback navigation.
    /// </summary>
    StartupAppLinkPending,

    /// <summary>
    /// Startup detected a pending deferred navigation request.
    /// </summary>
    StartupDeferredRequestPending,

    /// <summary>
    /// Startup navigated to a fallback request.
    /// </summary>
    StartupFallbackNavigated = 42,

    /// <summary>
    /// Startup navigation orchestration completed.
    /// </summary>
    StartupCompleted,

    /// <summary>
    /// Startup navigation orchestration failed.
    /// </summary>
    StartupFailed,

    /// <summary>
    /// Reconciliation from the host-observed navigation state has started.
    /// </summary>
    ReconciliationStarted,

    /// <summary>
    /// Reconciliation from host-observed navigation state completed.
    /// </summary>
    ReconciliationCompleted,

    /// <summary>
    /// Reconciliation from the host-observed navigation state failed.
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
    /// The router did not handle logical back navigation.
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
    AppLinkReceived = 54,

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
    DiagnosticObserverFailed,

    /// <summary>
    /// Presentation rollback started after an uncommitted mutation failed or was cancelled.
    /// </summary>
    PresentationRollbackStarted = 61,

    /// <summary>
    /// Presentation rollback restored the previous native and logical state.
    /// </summary>
    PresentationRollbackCompleted,

    /// <summary>
    /// Presentation rollback could not restore the previous state.
    /// </summary>
    PresentationRollbackFailed,

    /// <summary>
    /// A retired page could not be fully released after presentation committed.
    /// </summary>
    PresentationPageReleaseFailed,

    /// <summary>
    /// Invalid deferred-navigation data was quarantined instead of replayed.
    /// </summary>
    DeferredRequestStoreQuarantined,

    /// <summary>
    /// An external navigation request was rejected before entering the router pipeline.
    /// </summary>
    ExternalNavigationRejected,

    /// <summary>
    /// A retryable external navigation request was moved to the tail of the pending queue.
    /// </summary>
    ExternalNavigationRetrying,

    /// <summary>
    /// An external navigation request expired before it could be dispatched.
    /// </summary>
    ExternalNavigationExpired,

    /// <summary>
    /// The oldest external navigation request was dropped because the pending queue was full.
    /// </summary>
    ExternalNavigationOverflowed,

    /// <summary>
    /// An equivalent external navigation request was already pending.
    /// </summary>
    ExternalNavigationDeduplicated,

    /// <summary>
    /// An external navigation request was dropped after a terminal failure or its final attempt.
    /// </summary>
    ExternalNavigationTerminalDrop,

    /// <summary>
    /// Legacy preview deferred-navigation data was deliberately reset during a schema transition.
    /// </summary>
    DeferredRequestStoreReset = 72,

    /// <summary>
    /// Expired deferred-navigation requests were pruned from persistence.
    /// </summary>
    DeferredRequestStorePruned,

    /// <summary>
    /// The oldest deferred-navigation requests were dropped to preserve configured store bounds.
    /// </summary>
    DeferredRequestStoreOverflowed
}

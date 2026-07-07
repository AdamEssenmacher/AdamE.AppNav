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
    DiagnosticObserverFailed
}

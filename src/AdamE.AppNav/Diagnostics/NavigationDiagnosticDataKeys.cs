namespace AdamE.AppNav.Diagnostics;

/// <summary>
/// Defines stable keys used in <see cref="NavigationDiagnosticEvent.Data"/>.
/// </summary>
/// <remarks>
/// These keys are intended for structured logging, test assertions, and telemetry adapters.
/// Prefer these constants over ad hoc strings when writing diagnostics so downstream consumers
/// can reliably query event metadata across router versions.
/// </remarks>
public static class NavigationDiagnosticDataKeys
{
    /// <summary>
    /// Decision produced by a policy.
    /// </summary>
    public const string Decision = "decision";

    /// <summary>
    /// Source that initiated a logical back request.
    /// </summary>
    public const string BackSource = "backSource";

    /// <summary>
    /// Number of items affected by an operation.
    /// </summary>
    public const string Count = "count";

    /// <summary>
    /// Stable structural reason for an event.
    /// </summary>
    public const string Reason = "reason";

    /// <summary>
    /// Persistence schema version associated with an event.
    /// </summary>
    public const string SchemaVersion = "schemaVersion";

    /// <summary>
    /// Elapsed duration for an operation or phase, in milliseconds.
    /// </summary>
    public const string DurationMs = "durationMs";

    /// <summary>
    /// Fully qualified type name of an exception associated with the event.
    /// </summary>
    public const string ExceptionType = "exceptionType";

    /// <summary>
    /// Message from an exception associated with the event.
    /// </summary>
    public const string ExceptionMessage = "exceptionMessage";

    /// <summary>
    /// URI associated with a navigation request, app link, or route match.
    /// </summary>
    public const string Uri = "uri";

    /// <summary>
    /// Path component associated with a navigation request or route match.
    /// </summary>
    public const string Path = "path";

    /// <summary>
    /// Source that initiated a navigation request.
    /// </summary>
    public const string RequestSource = "requestSource";

    /// <summary>
    /// Requested navigation disposition, such as push or replace.
    /// </summary>
    public const string RequestDisposition = "requestDisposition";

    /// <summary>
    /// Provider that supplied request provenance metadata.
    /// </summary>
    public const string ProvenanceProvider = "provenanceProvider";

    /// <summary>
    /// Original URI captured before a request was normalized or redirected.
    /// </summary>
    public const string ProvenanceOriginalUri = "provenanceOriginalUri";

    /// <summary>
    /// Referrer URI associated with an external navigation request.
    /// </summary>
    public const string ProvenanceReferrerUri = "provenanceReferrerUri";

    /// <summary>
    /// Correlation identifier supplied by request provenance metadata.
    /// </summary>
    public const string ProvenanceCorrelationId = "provenanceCorrelationId";

    /// <summary>
    /// Indicates whether provenance metadata marks the request as a cold-start request.
    /// </summary>
    public const string ProvenanceIsColdStart = "provenanceIsColdStart";

    /// <summary>
    /// Additional request provenance attributes.
    /// </summary>
    public const string ProvenanceAttributes = "provenanceAttributes";

    /// <summary>
    /// Fully qualified type name of an app route.
    /// </summary>
    public const string RouteType = "routeType";

    /// <summary>
    /// Route template that matched, failed, or produced a diagnostic.
    /// </summary>
    public const string RouteTemplate = "routeTemplate";

    /// <summary>
    /// Stable diagnostic code from route matching or route value conversion.
    /// </summary>
    public const string RouteDiagnosticCode = "routeDiagnosticCode";

    /// <summary>
    /// Human-readable route diagnostic message.
    /// </summary>
    public const string RouteDiagnosticMessage = "routeDiagnosticMessage";

    /// <summary>
    /// Number of candidate route definitions considered during matching.
    /// </summary>
    public const string CandidateCount = "candidateCount";

    /// <summary>
    /// Kind of navigation plan produced or presented.
    /// </summary>
    public const string PlanKind = "planKind";

    /// <summary>
    /// Fully qualified type name of a request or plan policy.
    /// </summary>
    public const string PolicyType = "policyType";

    /// <summary>
    /// Fully qualified type name of a pre-match request transformer.
    /// </summary>
    public const string RequestTransformerType = "requestTransformerType";

    /// <summary>
    /// The number of request-target redirects followed by the router.
    /// </summary>
    public const string RedirectCount = "redirectCount";

    /// <summary>
    /// Route or request description before a redirect.
    /// </summary>
    public const string RedirectFrom = "redirectFrom";

    /// <summary>
    /// Route or request description after a redirect.
    /// </summary>
    public const string RedirectTo = "redirectTo";

    /// <summary>
    /// Textual trace of request-target redirects followed by the router.
    /// </summary>
    public const string RedirectTrace = "redirectTrace";

    /// <summary>
    /// Router window identifier associated with the event.
    /// </summary>
    public const string WindowId = "windowId";

    /// <summary>
    /// Source that triggered navigation reconciliation.
    /// </summary>
    public const string ReconciliationSource = "reconciliationSource";

    /// <summary>
    /// Original event kind that was being handled when a diagnostic observer failed.
    /// </summary>
    public const string OriginalKind = "originalKind";

    /// <summary>
    /// Fully qualified type name of a presented page or host object.
    /// </summary>
    public const string PageType = "pageType";

    /// <summary>
    /// Logical host identifier associated with a presented element.
    /// </summary>
    public const string HostId = "hostId";

    /// <summary>
    /// Logical branch identifier associated with a tab or other branch host.
    /// </summary>
    public const string BranchId = "branchId";

    /// <summary>
    /// Route entry identifier associated with a presented page.
    /// </summary>
    public const string RouteEntryId = "routeEntryId";

    /// <summary>
    /// Logical route entry identifier that owns a presentation-only page.
    /// </summary>
    public const string PresentationOwnerRouteEntryId = "presentationOwnerRouteEntryId";

    /// <summary>
    /// Route-local key assigned to a presentation-only page.
    /// </summary>
    public const string PresentationPageKey = "presentationPageKey";

    /// <summary>
    /// Modal identifier associated with a presented modal.
    /// </summary>
    public const string ModalId = "modalId";

    /// <summary>
    /// Logical presentation tree path where verification found a mismatch.
    /// </summary>
    public const string PresentationPath = "presentationPath";

    /// <summary>
    /// Expected presentation value when verifying a target navigation state.
    /// </summary>
    public const string PresentationExpected = "presentationExpected";

    /// <summary>
    /// Actual presentation value observed in the host tree during verification.
    /// </summary>
    public const string PresentationActual = "presentationActual";

    /// <summary>
    /// Name of a lifecycle or presentation handler.
    /// </summary>
    public const string HandlerName = "handlerName";

    /// <summary>
    /// Platform name associated with an adapter or app-link event.
    /// </summary>
    public const string Platform = "platform";

    /// <summary>
    /// Final startup outcome.
    /// </summary>
    public const string StartupOutcome = "startupOutcome";

    /// <summary>
    /// Indicates whether the startup detected pending deferred navigation requests.
    /// </summary>
    public const string StartupDeferredRequestPending = "startupDeferredRequestPending";

    /// <summary>
    /// App-link grace period used during startup, in milliseconds.
    /// </summary>
    public const string AppLinkGraceMs = "appLinkGraceMs";

    /// <summary>
    /// Structural reason for rejecting, retrying, expiring, or dropping an external request.
    /// </summary>
    public const string ExternalNavigationReason = "externalNavigationReason";

    /// <summary>
    /// One-based dispatch attempt number for an external request.
    /// </summary>
    public const string DispatchAttempt = "dispatchAttempt";

    /// <summary>
    /// Configured maximum number of dispatch attempts for an external request.
    /// </summary>
    public const string MaximumDispatchAttempts = "maximumDispatchAttempts";

    /// <summary>
    /// Number of external requests waiting for dispatch.
    /// </summary>
    public const string PendingRequestCount = "pendingRequestCount";

    /// <summary>
    /// Delay before the next external navigation dispatch attempt, in milliseconds.
    /// </summary>
    public const string RetryDelayMs = "retryDelayMs";
}

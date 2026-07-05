using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AdamE.MauiRouter.Diagnostics;

/// <summary>
/// Emits navigation diagnostics to observers, an optional logger, and the current
/// <see cref="Activity"/>.
/// </summary>
/// <remarks>
/// A diagnostics instance is intentionally lightweight and can be shared by router services.
/// Events are delivered synchronously to observers after they are mirrored to logging and
/// tracing. Observer failures do not interrupt navigation; they are converted into
/// <see cref="NavigationDiagnosticEventKind.DiagnosticObserverFailed"/> events.
/// </remarks>
public sealed class NavigationDiagnostics
{
    /// <summary>
    /// Gets a disabled diagnostics instance that ignores subscribers, observers, and write calls.
    /// </summary>
    public static NavigationDiagnostics None { get; } = new(enabled: false, logger: null);

    private readonly Lock _gate = new();
    private readonly List<INavigationDiagnosticObserver> _observers = new();
    private readonly bool _enabled;
    private readonly ILogger? _logger;
    private EventHandler<NavigationDiagnosticEvent>? _eventWritten;

    /// <summary>
    /// Initializes a diagnostics instance that emits events and optionally mirrors them to a logger.
    /// </summary>
    /// <param name="logger">The logger that should receive diagnostic events, or <see langword="null"/> to skip logging.</param>
    public NavigationDiagnostics(ILogger? logger = null)
        : this(enabled: true, logger)
    {
    }

    private NavigationDiagnostics(bool enabled, ILogger? logger)
    {
        _enabled = enabled;
        _logger = logger;
    }

    /// <summary>
    /// Occurs when a navigation diagnostic event is written.
    /// </summary>
    /// <remarks>
    /// Handlers are invoked synchronously. Exceptions thrown by handlers are isolated from
    /// navigation and reported as diagnostic observer failures.
    /// </remarks>
    public event EventHandler<NavigationDiagnosticEvent>? EventWritten
    {
        add
        {
            if (!_enabled || value is null)
            {
                return;
            }

            lock (_gate)
            {
                _eventWritten += value;
            }
        }

        remove
        {
            if (!_enabled || value is null)
            {
                return;
            }

            lock (_gate)
            {
                _eventWritten -= value;
            }
        }
    }

    /// <summary>
    /// Adds an observer that will receive subsequent navigation diagnostic events.
    /// </summary>
    /// <param name="observer">The observer to notify when events are written.</param>
    /// <remarks>
    /// Observers are retained for the lifetime of this diagnostics instance. Use
    /// <see cref="EventWritten"/> instead when subscription removal is required.
    /// </remarks>
    public void AddObserver(INavigationDiagnosticObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        if (!_enabled)
        {
            return;
        }

        lock (_gate)
        {
            _observers.Add(observer);
        }
    }

    /// <summary>
    /// Writes a navigation diagnostic event.
    /// </summary>
    /// <param name="kind">The event kind that identifies what happened.</param>
    /// <param name="operationId">The correlation identifier for the navigation operation.</param>
    /// <param name="message">A human-readable message for logs and diagnostics.</param>
    /// <param name="data">Optional structured metadata for the event.</param>
    /// <param name="severity">An explicit severity, or <see langword="null"/> to infer one from <paramref name="kind"/>.</param>
    /// <param name="phase">An explicit phase, or <see langword="null"/> to infer one from <paramref name="kind"/>.</param>
    public void Write(
        NavigationDiagnosticEventKind kind,
        string operationId,
        string message,
        IReadOnlyDictionary<string, object?>? data = null,
        LogLevel? severity = null,
        NavigationDiagnosticPhase? phase = null)
    {
        if (!_enabled)
        {
            return;
        }

        LogLevel effectiveSeverity = severity ?? InferSeverity(kind);
        NavigationDiagnosticPhase effectivePhase = phase ?? InferPhase(kind);
        var diagnosticEvent = new NavigationDiagnosticEvent(
            kind,
            operationId,
            message,
            DateTimeOffset.UtcNow,
            data ?? EmptyData.Value,
            effectiveSeverity,
            effectivePhase);

        MirrorToLogger(diagnosticEvent);
        MirrorToActivity(diagnosticEvent);

        EventHandler<NavigationDiagnosticEvent>? eventWritten;
        INavigationDiagnosticObserver[] observers;
        lock (_gate)
        {
            eventWritten = _eventWritten;
            observers = _observers.ToArray();
        }

        // Observer callbacks must never become part of the navigation control flow.
        if (eventWritten is not null)
        {
            foreach (Delegate handler in eventWritten.GetInvocationList())
            {
                if (handler is not EventHandler<NavigationDiagnosticEvent> eventHandler)
                {
                    continue;
                }

                try
                {
                    eventHandler(this, diagnosticEvent);
                }
                catch (Exception ex)
                {
                    WriteObserverFailure(kind, operationId, ex);
                }
            }
        }

        foreach (INavigationDiagnosticObserver observer in observers)
        {
            try
            {
                observer.OnNavigationDiagnosticEvent(diagnosticEvent);
            }
            catch (Exception ex)
            {
                WriteObserverFailure(kind, operationId, ex);
            }
        }
    }

    // Observer failure events are mirrored to logger/activity and event handlers only. Sending
    // them to INavigationDiagnosticObserver instances could recursively call the observer that just failed.
    private void WriteObserverFailure(
        NavigationDiagnosticEventKind originalKind,
        string operationId,
        Exception exception)
    {
        var failureEvent = new NavigationDiagnosticEvent(
            NavigationDiagnosticEventKind.DiagnosticObserverFailed,
            operationId,
            $"A navigation diagnostic observer failed while handling '{originalKind}'.",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.OriginalKind] = originalKind.ToString(),
                [NavigationDiagnosticDataKeys.ExceptionType] = exception.GetType().FullName,
                [NavigationDiagnosticDataKeys.ExceptionMessage] = exception.Message
            },
            LogLevel.Error,
            NavigationDiagnosticPhase.Diagnostics);

        MirrorToLogger(failureEvent);
        MirrorToActivity(failureEvent);

        EventHandler<NavigationDiagnosticEvent>? eventWritten;
        lock (_gate)
        {
            eventWritten = _eventWritten;
        }

        if (eventWritten is null)
        {
            return;
        }

        foreach (Delegate handler in eventWritten.GetInvocationList())
        {
            if (handler is not EventHandler<NavigationDiagnosticEvent> eventHandler)
            {
                continue;
            }

            try
            {
                eventHandler(this, failureEvent);
            }
            catch
            {
                // Diagnostic observers are intentionally isolated from navigation.
            }
        }
    }

    private void MirrorToLogger(NavigationDiagnosticEvent diagnosticEvent)
    {
        if (_logger is null)
        {
            return;
        }

        _logger.Log(
            diagnosticEvent.Severity,
            "Navigation {Kind} ({Phase}) operation {OperationId}: {Message} {@Data}",
            diagnosticEvent.Kind,
            diagnosticEvent.Phase,
            diagnosticEvent.OperationId,
            diagnosticEvent.Message,
            diagnosticEvent.Data);
    }

    // Activity tags keep the latest value queryable on Activity.Current, while ActivityEvent tags
    // preserve the values that belonged to this specific diagnostic event.
    private static void MirrorToActivity(NavigationDiagnosticEvent diagnosticEvent)
    {
        Activity? activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            ["navigation.kind"] = diagnosticEvent.Kind.ToString(),
            ["navigation.phase"] = diagnosticEvent.Phase.ToString(),
            ["navigation.severity"] = diagnosticEvent.Severity.ToString(),
            ["navigation.message"] = diagnosticEvent.Message
        };

        foreach (var pair in diagnosticEvent.Data)
        {
            var tagName = ToActivityTagName(pair.Key);
            tags[tagName] = pair.Value;
            activity.SetTag(tagName, pair.Value);
        }

        activity.AddEvent(new ActivityEvent(
            diagnosticEvent.Kind.ToString(),
            diagnosticEvent.Timestamp,
            tags));
    }

    private static string ToActivityTagName(string dataKey)
    {
        return dataKey switch
        {
            NavigationDiagnosticDataKeys.RouteType => "navigation.route_type",
            NavigationDiagnosticDataKeys.RouteTemplate => "navigation.route_template",
            NavigationDiagnosticDataKeys.RequestSource => "navigation.source",
            NavigationDiagnosticDataKeys.RequestDisposition => "navigation.disposition",
            NavigationDiagnosticDataKeys.ProvenanceProvider => "navigation.provenance.provider",
            NavigationDiagnosticDataKeys.ProvenanceOriginalUri => "navigation.provenance.original_uri",
            NavigationDiagnosticDataKeys.ProvenanceReferrerUri => "navigation.provenance.referrer_uri",
            NavigationDiagnosticDataKeys.ProvenanceCorrelationId => "navigation.provenance.correlation_id",
            NavigationDiagnosticDataKeys.ProvenanceIsColdStart => "navigation.provenance.is_cold_start",
            NavigationDiagnosticDataKeys.ProvenanceAttributes => "navigation.provenance.attributes",
            NavigationDiagnosticDataKeys.PlanKind => "navigation.plan_kind",
            NavigationDiagnosticDataKeys.TransitionType => "navigation.transition_type",
            NavigationDiagnosticDataKeys.TransitionOperation => "navigation.transition_operation",
            NavigationDiagnosticDataKeys.TransitionDurationMs => "navigation.transition_duration_ms",
            NavigationDiagnosticDataKeys.TransitionElementIds => "navigation.transition_element_ids",
            NavigationDiagnosticDataKeys.TransitionFallbackReason => "navigation.transition_fallback_reason",
            NavigationDiagnosticDataKeys.Platform => "navigation.platform",
            NavigationDiagnosticDataKeys.ExceptionType => "navigation.exception_type",
            NavigationDiagnosticDataKeys.ExceptionMessage => "navigation.exception_message",
            _ => $"navigation.{dataKey}"
        };
    }

    private static LogLevel InferSeverity(NavigationDiagnosticEventKind kind)
    {
        return kind.ToString().EndsWith("Failed", StringComparison.Ordinal) ||
               kind is NavigationDiagnosticEventKind.NavigationFailed or
                   NavigationDiagnosticEventKind.RequestRedirectLoopDetected or
                   NavigationDiagnosticEventKind.DiagnosticObserverFailed
            ? LogLevel.Error
            : kind is NavigationDiagnosticEventKind.RouteNotMatched or NavigationDiagnosticEventKind.BackUnhandled
                ? LogLevel.Warning
                : kind is NavigationDiagnosticEventKind.RestoreRejected
                    ? LogLevel.Warning
                : kind is NavigationDiagnosticEventKind.PresentationPageCreated or
                    NavigationDiagnosticEventKind.PresentationPageReleased or
                    NavigationDiagnosticEventKind.PresentationHandlerAttached or
                    NavigationDiagnosticEventKind.PresentationHandlerDetached
                    ? LogLevel.Debug
                : kind.ToString().EndsWith("Started", StringComparison.Ordinal)
                    ? LogLevel.Debug
                    : LogLevel.Information;
    }

    private static NavigationDiagnosticPhase InferPhase(NavigationDiagnosticEventKind kind)
    {
        return kind switch
        {
            NavigationDiagnosticEventKind.RouteMatchingStarted or
                NavigationDiagnosticEventKind.RouteMatched or
                NavigationDiagnosticEventKind.RouteNotMatched or
                NavigationDiagnosticEventKind.RouteFallbackSelected or
                NavigationDiagnosticEventKind.RouteMatchingFailed =>
                NavigationDiagnosticPhase.RouteMatching,
            NavigationDiagnosticEventKind.RequestPolicyStarted or
                NavigationDiagnosticEventKind.RequestRedirected or
                NavigationDiagnosticEventKind.RequestRedirectLoopDetected or
                NavigationDiagnosticEventKind.RequestPolicyCompleted or
                NavigationDiagnosticEventKind.RequestPolicyFailed =>
                NavigationDiagnosticPhase.RequestPolicy,
            NavigationDiagnosticEventKind.PlanningStarted or
                NavigationDiagnosticEventKind.PlanningCompleted or
                NavigationDiagnosticEventKind.PlanningFailed =>
                NavigationDiagnosticPhase.Planning,
            NavigationDiagnosticEventKind.PlanPolicyStarted or
                NavigationDiagnosticEventKind.PlanPolicyCompleted or
                NavigationDiagnosticEventKind.PlanPolicyFailed =>
                NavigationDiagnosticPhase.PlanPolicy,
            NavigationDiagnosticEventKind.PresentationStarted or
                NavigationDiagnosticEventKind.PresentationPageCreated or
                NavigationDiagnosticEventKind.PresentationPageReleased or
                NavigationDiagnosticEventKind.PresentationHandlerAttached or
                NavigationDiagnosticEventKind.PresentationHandlerDetached or
                NavigationDiagnosticEventKind.PresentationPresenterDisposed or
                NavigationDiagnosticEventKind.PresentationTransitionStarted or
                NavigationDiagnosticEventKind.PresentationTransitionCompleted or
                NavigationDiagnosticEventKind.PresentationTransitionFailed or
                NavigationDiagnosticEventKind.PresentationTransitionFallback or
                NavigationDiagnosticEventKind.PresentationVerificationFailed or
                NavigationDiagnosticEventKind.PresentationCompleted or
                NavigationDiagnosticEventKind.PresentationFailed =>
                NavigationDiagnosticPhase.Presentation,
            NavigationDiagnosticEventKind.SnapshotSaveStarted or
                NavigationDiagnosticEventKind.SnapshotSaved or
                NavigationDiagnosticEventKind.SnapshotSaveFailed or
                NavigationDiagnosticEventKind.SnapshotLoadStarted or
                NavigationDiagnosticEventKind.SnapshotLoaded or
                NavigationDiagnosticEventKind.SnapshotLoadFailed or
                NavigationDiagnosticEventKind.RestoreStarted or
                NavigationDiagnosticEventKind.RestoreCompleted or
                NavigationDiagnosticEventKind.RestoreRejected or
                NavigationDiagnosticEventKind.RestoreFailed =>
                NavigationDiagnosticPhase.Persistence,
            NavigationDiagnosticEventKind.StartupStarted or
                NavigationDiagnosticEventKind.StartupAppLinkPending or
                NavigationDiagnosticEventKind.StartupRestoreSkipped or
                NavigationDiagnosticEventKind.StartupFallbackNavigated or
                NavigationDiagnosticEventKind.StartupCompleted or
                NavigationDiagnosticEventKind.StartupFailed =>
                NavigationDiagnosticPhase.Startup,
            NavigationDiagnosticEventKind.ReconciliationStarted or
                NavigationDiagnosticEventKind.ReconciliationCompleted or
                NavigationDiagnosticEventKind.ReconciliationFailed =>
                NavigationDiagnosticPhase.Reconciliation,
            NavigationDiagnosticEventKind.BackStarted or
                NavigationDiagnosticEventKind.BackEvaluated or
                NavigationDiagnosticEventKind.BackCompleted or
                NavigationDiagnosticEventKind.BackUnhandled or
                NavigationDiagnosticEventKind.BackFailed =>
                NavigationDiagnosticPhase.Back,
            NavigationDiagnosticEventKind.AppLinkReceived or
                NavigationDiagnosticEventKind.AppLinkBuffered or
                NavigationDiagnosticEventKind.AppLinkDispatched or
                NavigationDiagnosticEventKind.AppLinkFailed =>
                NavigationDiagnosticPhase.AppLink,
            NavigationDiagnosticEventKind.NavigationCommittedHandlerFailed =>
                NavigationDiagnosticPhase.Navigation,
            NavigationDiagnosticEventKind.DiagnosticObserverFailed =>
                NavigationDiagnosticPhase.Diagnostics,
            _ => NavigationDiagnosticPhase.Navigation
        };
    }

    private static class EmptyData
    {
        public static readonly IReadOnlyDictionary<string, object?> Value = new Dictionary<string, object?>();
    }
}

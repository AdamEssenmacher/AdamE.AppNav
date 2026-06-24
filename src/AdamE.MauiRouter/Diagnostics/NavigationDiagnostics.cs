using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AdamE.MauiRouter.Diagnostics;

public sealed class NavigationDiagnostics
{
    public static NavigationDiagnostics None { get; } = new(enabled: false, logger: null);

    private readonly object _gate = new();
    private readonly List<INavigationObserver> _observers = new();
    private readonly bool _enabled;
    private readonly ILogger? _logger;
    private EventHandler<NavigationDiagnosticEvent>? _eventWritten;

    public NavigationDiagnostics(ILogger? logger = null)
        : this(enabled: true, logger)
    {
    }

    private NavigationDiagnostics(bool enabled, ILogger? logger)
    {
        _enabled = enabled;
        _logger = logger;
    }

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

    public void AddObserver(INavigationObserver observer)
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

    public void Write(
        NavigationDiagnosticEventKind kind,
        string operationId,
        string message,
        IReadOnlyDictionary<string, object?>? data = null,
        NavigationDiagnosticSeverity? severity = null,
        NavigationDiagnosticPhase? phase = null)
    {
        if (!_enabled)
        {
            return;
        }

        var effectiveSeverity = severity ?? InferSeverity(kind);
        var effectivePhase = phase ?? InferPhase(kind);
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
        INavigationObserver[] observers;
        lock (_gate)
        {
            eventWritten = _eventWritten;
            observers = _observers.ToArray();
        }

        if (eventWritten is not null)
        {
            foreach (EventHandler<NavigationDiagnosticEvent> handler in eventWritten.GetInvocationList())
            {
                try
                {
                    handler(this, diagnosticEvent);
                }
                catch (Exception ex)
                {
                    WriteObserverFailure(kind, operationId, ex);
                }
            }
        }

        foreach (var observer in observers)
        {
            try
            {
                observer.OnNavigationEvent(diagnosticEvent);
            }
            catch (Exception ex)
            {
                WriteObserverFailure(kind, operationId, ex);
            }
        }
    }

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
            NavigationDiagnosticSeverity.Error,
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

        foreach (EventHandler<NavigationDiagnosticEvent> handler in eventWritten.GetInvocationList())
        {
            try
            {
                handler(this, failureEvent);
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
            ToLogLevel(diagnosticEvent.Severity),
            "Navigation {Kind} ({Phase}) operation {OperationId}: {Message} {@Data}",
            diagnosticEvent.Kind,
            diagnosticEvent.Phase,
            diagnosticEvent.OperationId,
            diagnosticEvent.Message,
            diagnosticEvent.Data);
    }

    private static void MirrorToActivity(NavigationDiagnosticEvent diagnosticEvent)
    {
        var activity = Activity.Current;
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

    private static LogLevel ToLogLevel(NavigationDiagnosticSeverity severity)
    {
        return severity switch
        {
            NavigationDiagnosticSeverity.Trace => LogLevel.Trace,
            NavigationDiagnosticSeverity.Debug => LogLevel.Debug,
            NavigationDiagnosticSeverity.Information => LogLevel.Information,
            NavigationDiagnosticSeverity.Warning => LogLevel.Warning,
            NavigationDiagnosticSeverity.Error => LogLevel.Error,
            NavigationDiagnosticSeverity.Critical => LogLevel.Critical,
            _ => LogLevel.Information
        };
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

    private static NavigationDiagnosticSeverity InferSeverity(NavigationDiagnosticEventKind kind)
    {
        return kind.ToString().EndsWith("Failed", StringComparison.Ordinal) ||
               kind is NavigationDiagnosticEventKind.NavigationFailed or
                   NavigationDiagnosticEventKind.RequestRedirectLoopDetected or
                   NavigationDiagnosticEventKind.DiagnosticObserverFailed
            ? NavigationDiagnosticSeverity.Error
            : kind is NavigationDiagnosticEventKind.RouteNotMatched or NavigationDiagnosticEventKind.BackUnhandled
                ? NavigationDiagnosticSeverity.Warning
                : kind is NavigationDiagnosticEventKind.RestoreRejected
                    ? NavigationDiagnosticSeverity.Warning
                : kind is NavigationDiagnosticEventKind.PresentationPageCreated or
                    NavigationDiagnosticEventKind.PresentationPageReleased or
                    NavigationDiagnosticEventKind.PresentationHandlerAttached or
                    NavigationDiagnosticEventKind.PresentationHandlerDetached
                    ? NavigationDiagnosticSeverity.Debug
                : kind.ToString().EndsWith("Started", StringComparison.Ordinal)
                    ? NavigationDiagnosticSeverity.Debug
                    : NavigationDiagnosticSeverity.Information;
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

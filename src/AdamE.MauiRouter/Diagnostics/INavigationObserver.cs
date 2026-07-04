namespace AdamE.MauiRouter.Diagnostics;

/// <summary>
/// Observes navigation diagnostic events emitted by a <see cref="NavigationDiagnostics"/> instance.
/// </summary>
/// <remarks>
/// Observers are intended for in-process diagnostics such as test recorders, telemetry bridges,
/// or app-specific troubleshooting tools. Exceptions thrown by observers are isolated from
/// navigation and are reported as <see cref="NavigationDiagnosticEventKind.DiagnosticObserverFailed"/>
/// events.
/// </remarks>
public interface INavigationObserver
{
    /// <summary>
    /// Handles a diagnostic event emitted by the router.
    /// </summary>
    /// <param name="diagnosticEvent">The event that describes a router operation, phase, and related data.</param>
    void OnNavigationEvent(NavigationDiagnosticEvent diagnosticEvent);
}

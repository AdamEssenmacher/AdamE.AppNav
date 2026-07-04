using Microsoft.Extensions.Logging;

namespace AdamE.MauiRouter.Diagnostics;

/// <summary>
/// Describes a single diagnostic event emitted while MauiRouter processes navigation work.
/// </summary>
/// <param name="Kind">The specific event that occurred.</param>
/// <param name="OperationId">A correlation identifier shared by events from the same router operation.</param>
/// <param name="Message">A human-readable diagnostic message suitable for logs and test assertions.</param>
/// <param name="Timestamp">The UTC timestamp at which the diagnostic event was created.</param>
/// <param name="Data">Structured event metadata keyed by values from <see cref="NavigationDiagnosticDataKeys"/> when possible.</param>
/// <param name="Severity">The diagnostic severity used for logging and observer filtering.</param>
/// <param name="Phase">The logical router phase associated with the event.</param>
public sealed record NavigationDiagnosticEvent(
    NavigationDiagnosticEventKind Kind,
    string OperationId,
    string Message,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object?> Data,
    LogLevel Severity = LogLevel.Information,
    NavigationDiagnosticPhase Phase = NavigationDiagnosticPhase.Navigation);

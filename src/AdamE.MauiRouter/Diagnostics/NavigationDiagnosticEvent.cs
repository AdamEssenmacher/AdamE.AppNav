namespace AdamE.MauiRouter.Diagnostics;

public sealed record NavigationDiagnosticEvent(
    NavigationDiagnosticEventKind Kind,
    string OperationId,
    string Message,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, object?> Data,
    NavigationDiagnosticSeverity Severity = NavigationDiagnosticSeverity.Information,
    NavigationDiagnosticPhase Phase = NavigationDiagnosticPhase.Navigation);

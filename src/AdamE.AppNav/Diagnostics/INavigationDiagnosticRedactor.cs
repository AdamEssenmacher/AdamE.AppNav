namespace AdamE.AppNav.Diagnostics;

/// <summary>
/// Applies application-specific redaction before a diagnostic event reaches any sink.
/// </summary>
public interface INavigationDiagnosticRedactor
{
    /// <summary>
    /// Returns the event that should be emitted.
    /// </summary>
    /// <remarks>
    /// Safe mode supplies built-in sanitized input; Full mode supplies raw input. Throwing or returning
    /// <see langword="null"/> causes AppNav to emit its built-in Safe representation.
    /// </remarks>
    /// <param name="diagnosticEvent">The event to redact.</param>
    /// <returns>The event to emit.</returns>
    NavigationDiagnosticEvent Redact(NavigationDiagnosticEvent diagnosticEvent);
}

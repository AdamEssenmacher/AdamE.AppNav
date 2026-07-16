namespace AdamE.AppNav.Diagnostics;

/// <summary>
/// Controls how much request data is exposed by navigation diagnostics.
/// </summary>
public enum NavigationDiagnosticDataMode
{
    /// <summary>
    /// Removes or sanitizes values that may contain application or user data.
    /// </summary>
    Safe,

    /// <summary>
    /// Exposes the values supplied by diagnostic producers without built-in redaction.
    /// </summary>
    Full
}

namespace AdamE.AppNav.Diagnostics;

/// <summary>
/// Configures navigation diagnostics data handling.
/// </summary>
public sealed class NavigationDiagnosticsOptions
{
    /// <summary>
    /// Gets or sets the diagnostic data mode. Safe mode is the default.
    /// </summary>
    public NavigationDiagnosticDataMode DataMode { get; set; } = NavigationDiagnosticDataMode.Safe;
}

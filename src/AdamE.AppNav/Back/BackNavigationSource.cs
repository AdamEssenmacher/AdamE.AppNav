namespace AdamE.AppNav.Back;

/// <summary>
/// Identifies whether a back request originated in application code or a presentation host.
/// </summary>
public enum BackNavigationSource
{
    ApplicationCommand = 0,
    Host = 1
}

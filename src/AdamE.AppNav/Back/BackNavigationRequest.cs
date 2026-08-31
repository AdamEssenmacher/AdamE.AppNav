namespace AdamE.AppNav.Back;

/// <summary>
/// Describes a logical back-navigation request before planning and policy evaluation.
/// </summary>
public sealed class BackNavigationRequest
{
    public BackNavigationRequest(
        string? windowId = null,
        BackNavigationSource source = BackNavigationSource.ApplicationCommand)
    {
        WindowId = windowId;
        Source = source;
    }

    /// <summary>
    /// Gets the window to navigate within, or <see langword="null"/> to use the active window.
    /// </summary>
    public string? WindowId { get; }

    /// <summary>
    /// Gets the source that initiated the back request.
    /// </summary>
    public BackNavigationSource Source { get; }
}

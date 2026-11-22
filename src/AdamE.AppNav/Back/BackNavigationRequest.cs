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

    /// <summary>
    /// When set, Back is canceled unless this entry is still the top modal in the target window when the router admits
    /// the request. Use this for a native dismissal attempt that can queue behind another operation.
    /// </summary>
    public string? ExpectedModalEntryId { get; init; }

    /// <summary>
    /// Gets whether the presenter shows the columns of every <see cref="State.SplitNode"/> side by side, rather than
    /// collapsed to one stack. The default, <see langword="false"/>, plans Back as if every split were collapsed.
    /// </summary>
    /// <remarks>
    /// Core does not know the presented layout; the adapter that shows the splits says it here. Side by side, Back goes
    /// back inside the secondary column, then inside the primary column, and never plans a Back that only changes
    /// <see cref="State.SplitNode.CompactColumn"/>.
    /// </remarks>
    public bool PresentsSplitColumnsSideBySide { get; init; }
}

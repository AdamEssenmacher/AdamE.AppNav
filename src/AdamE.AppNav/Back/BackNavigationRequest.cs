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
    /// Gets the split layout to plan this Back for, or <see langword="null"/> (the default) to use the layout the
    /// presenter last reported for the target window with <see cref="Navigation.IRouterNavigator.ReportSplitLayout"/>.
    /// </summary>
    /// <remarks>
    /// Set it only to override what the presenter reported, such as a host Back raised for a layout the presenter is
    /// changing to. Side by side, Back goes back inside the secondary column, then inside the primary column, and
    /// never plans a Back that only changes <see cref="State.SplitNode.CompactColumn"/>.
    /// </remarks>
    public State.SplitLayout? SplitLayout
    {
        get;
        init => field = value is not { } layout || Enum.IsDefined(layout)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(SplitLayout), value, "Unknown split layout.");
    }
}

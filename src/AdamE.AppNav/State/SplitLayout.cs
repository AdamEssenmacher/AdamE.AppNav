namespace AdamE.AppNav.State;

/// <summary>
/// Describes how a presenter currently shows the <see cref="SplitNode"/> columns of one window.
/// </summary>
/// <remarks>
/// The layout is presentation, not logical state: reporting it never changes <see cref="NavigationState"/> or history.
/// The router uses it to plan Back and to report the route the user sees.
/// </remarks>
public enum SplitLayout
{
    /// <summary>
    /// Each split is collapsed to one stack: its primary column's tree, with its secondary column's tree above it when
    /// <see cref="SplitNode.CompactColumn"/> is <see cref="SplitColumn.Secondary"/>.
    /// </summary>
    Collapsed = 0,

    /// <summary>
    /// Each split shows its primary and secondary columns side by side.
    /// </summary>
    SideBySide = 1
}

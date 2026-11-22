namespace AdamE.AppNav.State;

/// <summary>
/// Identifies one column of a <see cref="SplitNode"/>.
/// </summary>
public enum SplitColumn
{
    /// <summary>
    /// The primary column, such as a list of items.
    /// </summary>
    Primary = 0,

    /// <summary>
    /// The secondary column, such as the navigation tree of the item selected in the primary column.
    /// </summary>
    Secondary = 1
}

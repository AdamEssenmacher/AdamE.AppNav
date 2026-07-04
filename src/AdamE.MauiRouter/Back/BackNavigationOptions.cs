namespace AdamE.MauiRouter.Back;

/// <summary>
/// Configures the host-local fallback behavior used by <see cref="DefaultBackNavigator"/>.
/// </summary>
/// <remarks>
/// These options apply to MauiRouter's logical navigation host nodes. In the core router,
/// tabs and flyouts are semantic containers with selected/default branches; they do not
/// imply a dependency on MAUI pages, controls, Shell, or platform UI concepts.
///
/// The options do not define a global ordering between different host types. Back navigation
/// is resolved by walking the active navigation tree: the selected child branch is asked to
/// handle back first, and only when that branch cannot handle back does the owning host apply
/// the relevant fallback configured here.
/// </remarks>
public sealed record BackNavigationOptions
{
    /// <summary>
    /// Gets the default back navigation options used when no explicit options are supplied.
    /// </summary>
    public static BackNavigationOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether back navigation should return a logical tab host to
    /// its default tab before the tab host declines to handle back navigation.
    /// </summary>
    /// <remarks>
    /// When enabled, the default back navigator changes the selected tab to the host's
    /// configured default tab if the selected tab branch has no back action, the selected
    /// tab is not already the default tab, and the default tab still exists. When disabled,
    /// a tab host whose selected branch cannot go back declines to handle back navigation.
    /// </remarks>
    public bool ReturnToDefaultTabBeforeLeaving { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether back navigation should return a logical flyout host to
    /// its default item before the flyout host declines to handle back navigation.
    /// </summary>
    /// <remarks>
    /// When enabled, the default back navigator changes the selected flyout item to the host's
    /// configured default item if the selected item branch has no back action, the selected
    /// item is not already the default item, and the default item still exists. When disabled,
    /// a flyout host whose selected branch cannot go back declines to handle back navigation.
    /// </remarks>
    public bool ReturnToDefaultFlyoutItemBeforeLeaving { get; init; } = true;
}

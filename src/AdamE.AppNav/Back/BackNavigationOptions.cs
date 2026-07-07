namespace AdamE.AppNav.Back;

/// <summary>
/// Configures the host-local fallback behavior used by <see cref="DefaultBackNavigator"/>.
/// </summary>
/// <remarks>
/// These options apply to AppNav's logical branch-host nodes. In the core router,
/// branch hosts are semantic containers with selected/default branches; they do not
/// imply a dependency on MAUI pages, controls, Shell, or platform UI concepts.
///
/// The options do not define a global ordering between different host types. Back navigation
/// is resolved by walking the active navigation tree: the selected child branch is asked to
/// handle back first, and only when that branch cannot handle back does the owning host apply
/// the default-branch fallback configured here.
/// </remarks>
public sealed record BackNavigationOptions
{
    /// <summary>
    /// Gets the default back navigation options used when no explicit options are supplied.
    /// </summary>
    public static BackNavigationOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether back navigation should return a logical branch host to
    /// its default branch before the branch host declines to handle back navigation.
    /// </summary>
    /// <remarks>
    /// When enabled, the default back navigator changes the selected branch to the host's
    /// configured default branch if the selected branch has no back action, the selected
    /// branch is not yet the default branch, and the default branch still exists. When disabled,
    /// a branch host whose selected branch cannot go back declines to handle back navigation.
    /// </remarks>
    public bool ReturnToDefaultBranchBeforeLeaving { get; init; } = true;
}

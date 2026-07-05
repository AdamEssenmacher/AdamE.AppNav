namespace AdamE.MauiRouter.Navigation;

/// <summary>
/// Identifies the router operation kind that committed navigation state.
/// </summary>
public enum NavigationCommitKind
{
    /// <summary>
    /// A forward or direct navigation request committed state.
    /// </summary>
    Navigate,

    /// <summary>
    /// A logical back navigation request committed state.
    /// </summary>
    Back,

    /// <summary>
    /// Host-observed navigation state was reconciled into the router state.
    /// </summary>
    Reconcile,

    /// <summary>
    /// Persisted navigation state was restored into the router state.
    /// </summary>
    Restore
}

namespace AdamE.AppNav.Navigation;

/// <summary>
/// Identifies the terminal outcome of a logical back-navigation request.
/// </summary>
public enum BackNavigationStatus
{
    Unhandled = 0,
    Completed = 1,
    Canceled = 2
}

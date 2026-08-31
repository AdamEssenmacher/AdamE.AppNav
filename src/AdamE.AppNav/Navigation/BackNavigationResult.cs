namespace AdamE.AppNav.Navigation;

/// <summary>
/// Describes the outcome of a logical back navigation request.
/// </summary>
public readonly record struct BackNavigationResult
{
    private BackNavigationResult(BackNavigationStatus status, NavigationResult? navigationResult)
    {
        if (status == BackNavigationStatus.Completed && navigationResult is null)
            throw new ArgumentNullException(nameof(navigationResult));
        if (status != BackNavigationStatus.Completed && navigationResult is not null)
            throw new ArgumentException("Only a completed back result can carry a navigation result.", nameof(navigationResult));

        Status = status;
        NavigationResult = navigationResult;
    }

    public BackNavigationStatus Status { get; }

    public NavigationResult? NavigationResult { get; }

    /// <summary>
    /// Gets a result for a back request that no router host could handle.
    /// </summary>
    public static BackNavigationResult Unhandled { get; } = new(BackNavigationStatus.Unhandled, null);

    /// <summary>
    /// Gets a result for a candidate back request canceled by a policy.
    /// </summary>
    public static BackNavigationResult Canceled { get; } = new(BackNavigationStatus.Canceled, null);

    /// <summary>
    /// Creates a completed back-navigation result from the navigation result produced by the router.
    /// </summary>
    /// <param name="result">The navigation result produced while handling back navigation.</param>
    /// <returns>A handled back-navigation result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static BackNavigationResult CompletedBy(NavigationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new BackNavigationResult(BackNavigationStatus.Completed, result);
    }
}

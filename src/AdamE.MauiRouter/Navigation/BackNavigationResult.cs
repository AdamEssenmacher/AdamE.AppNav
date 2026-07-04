using JetBrains.Annotations;

namespace AdamE.MauiRouter.Navigation;

/// <summary>
/// Describes the outcome of a logical back navigation request.
/// </summary>
/// <param name="Handled">
/// <see langword="true"/> when the router handled the back request by presenting a new
/// navigation state; otherwise, <see langword="false"/>.
/// </param>
/// <param name="HandledNavigationResult">
/// The navigation result produced by handled back navigation, or <see langword="null"/> when
/// the back request was not handled.
/// </param>
/// <remarks>
/// Prefer <see cref="Unhandled"/> and <see cref="HandledBy"/> when creating results so the
/// handled flag and result payload stay consistent.
/// </remarks>
public readonly record struct BackNavigationResult(
    bool Handled,
    [UsedImplicitly] NavigationResult? HandledNavigationResult = null)
{
    /// <summary>
    /// Gets a result for a back request that no router host could handle.
    /// </summary>
    public static BackNavigationResult Unhandled { get; } = new(false);

    /// <summary>
    /// Creates a handled back-navigation result from the navigation result produced by the router.
    /// </summary>
    /// <param name="result">The navigation result produced while handling back navigation.</param>
    /// <returns>A handled back-navigation result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="result"/> is <see langword="null"/>.</exception>
    public static BackNavigationResult HandledBy(NavigationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new BackNavigationResult(true, result);
    }
}

using AdamE.MauiRouter.Plans;

namespace AdamE.MauiRouter.Back;

/// <summary>
/// Creates logical back-navigation plans for a router state.
/// </summary>
/// <remarks>
/// Implement this interface when an application needs custom semantic back behavior. Returning
/// <see langword="null"/> tells the router that the request was not handled so the host can
/// delegate to another back mechanism, such as platform navigation.
/// </remarks>
public interface IBackNavigator
{
    /// <summary>
    /// Creates a navigation plan that represents one logical back action.
    /// </summary>
    /// <param name="context">The current state, target window, and diagnostic operation metadata.</param>
    /// <returns>
    /// A back-navigation plan, or <see langword="null"/> when the current state cannot handle back navigation.
    /// </returns>
    NavigationPlan? CreateBackPlan(BackNavigationContext context);
}

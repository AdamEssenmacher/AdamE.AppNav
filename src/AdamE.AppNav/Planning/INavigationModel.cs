using AdamE.AppNav.State;

namespace AdamE.AppNav.Planning;

/// <summary>
/// Describes canonical and contextual topology planning for one application route family.
/// </summary>
/// <typeparam name="TRoute">The application route base type handled by the model.</typeparam>
public interface INavigationModel<TRoute>
    where TRoute : AppRoute
{
    /// <summary>
    /// Creates the declared canonical topology for a route.
    /// </summary>
    NavigationState CreateCanonicalState(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata = null,
        string? windowId = null,
        string? hostId = null);

    /// <summary>
    /// Attempts to apply a contextual stack mutation to the current topology.
    /// </summary>
    NavigationState? TryCreateContextualState(
        NavigationState currentState,
        TRoute route,
        ContextualStackMutationKind mutation,
        IReadOnlyDictionary<string, object?>? metadata = null);
}

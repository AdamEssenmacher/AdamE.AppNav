using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;
using JetBrains.Annotations;

namespace AdamE.AppNav.Navigation;

/// <summary>
/// Represents a reconciliation whose base state is no longer the router's current state.
/// </summary>
/// <param name="reconciliation">The rejected reconciliation.</param>
/// <param name="currentState">The router state at the time the reconciliation was evaluated.</param>
/// <param name="message">The message that describes why the reconciliation was rejected.</param>
/// <remarks>
/// The router throws this exception before presentation, and before mutating navigation state or history, when
/// another operation committed after the host observed <see cref="NavigationReconciliation.BaseState"/>. The host
/// should re-read the router's current state instead of retrying the same target.
/// </remarks>
public sealed class StaleNavigationReconciliationException(
    NavigationReconciliation reconciliation,
    NavigationState currentState,
    string message)
    : Exception(message)
{
    /// <summary>
    /// Gets the rejected reconciliation.
    /// </summary>
    [UsedImplicitly]
    public NavigationReconciliation Reconciliation { get; } = reconciliation;

    /// <summary>
    /// Gets the router state at the time the reconciliation was evaluated.
    /// </summary>
    [UsedImplicitly]
    public NavigationState CurrentState { get; } = currentState;
}

using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Navigation;

/// <summary>
/// Provides data for a committed router navigation operation.
/// </summary>
public sealed class NavigationCommittedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes committed navigation event data.
    /// </summary>
    public NavigationCommittedEventArgs(
        string operationId,
        NavigationCommitKind kind,
        RouterNavigationRequest request,
        AppRoute route,
        NavigationPlan plan,
        NavigationState previousState,
        NavigationState currentState,
        NavigationHistory currentHistory,
        bool presented)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        OperationId = operationId;
        Kind = kind;
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Route = route ?? throw new ArgumentNullException(nameof(route));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        PreviousState = previousState ?? throw new ArgumentNullException(nameof(previousState));
        CurrentState = currentState ?? throw new ArgumentNullException(nameof(currentState));
        CurrentHistory = currentHistory ?? throw new ArgumentNullException(nameof(currentHistory));
        Presented = presented;
    }

    /// <summary>
    /// Gets the correlation identifier shared by diagnostics and history for the operation.
    /// </summary>
    public string OperationId { get; }

    /// <summary>
    /// Gets the router operation kind that committed state.
    /// </summary>
    public NavigationCommitKind Kind { get; }

    /// <summary>
    /// Gets the final request envelope accepted by the router.
    /// </summary>
    public RouterNavigationRequest Request { get; }

    /// <summary>
    /// Gets the final route accepted by the router.
    /// </summary>
    public AppRoute Route { get; }

    /// <summary>
    /// Gets the navigation plan that produced the committed state.
    /// </summary>
    public NavigationPlan Plan { get; }

    /// <summary>
    /// Gets the router state before the operation committed.
    /// </summary>
    public NavigationState PreviousState { get; }

    /// <summary>
    /// Gets the router state after the operation committed.
    /// </summary>
    public NavigationState CurrentState { get; }

    /// <summary>
    /// Gets the logical navigation history after the operation committed.
    /// </summary>
    public NavigationHistory CurrentHistory { get; }

    /// <summary>
    /// Gets the presentation status reported for the committed operation.
    /// </summary>
    public bool Presented { get; }
}

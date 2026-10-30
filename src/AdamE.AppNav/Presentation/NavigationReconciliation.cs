using AdamE.AppNav.Internal;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Presentation;

public sealed record NavigationReconciliation(
    NavigationState TargetState,
    NavigationReconciliationSource Source,
    AppRoute? Route = null,
    string? Reason = null)
{
    public NavigationState TargetState
    {
        get;
        init => field = NavigationIdentity.Required(value, nameof(TargetState));
    } = NavigationIdentity.Required(TargetState, nameof(TargetState));

    /// <summary>
    /// Gets the router state the host observed before the change it is reconciling, or <see langword="null"/> to
    /// reconcile against whatever state is current when the reconciliation runs.
    /// </summary>
    /// <remarks>
    /// When set, the router compares it with <c>CurrentState</c> after the reconciliation is admitted. If they differ,
    /// another operation committed first and the router throws <see cref="Navigation.StaleNavigationReconciliationException"/>
    /// without presenting or changing state or history. States compare by record equality, so pass the committed
    /// state instance the host observed, such as the <c>CurrentState</c> from its last presentation context.
    /// </remarks>
    public NavigationState? ExpectedState { get; init; }
}

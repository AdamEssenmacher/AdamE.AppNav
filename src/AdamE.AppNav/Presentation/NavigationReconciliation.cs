using AdamE.AppNav.Internal;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Presentation;

/// <summary>
/// Describes a host-observed change from a committed router state to a new logical state.
/// </summary>
/// <param name="BaseState">
/// The committed router state the host observed before its change. The router applies the reconciliation only while
/// this exact instance is still its <c>CurrentState</c>.
/// </param>
/// <param name="TargetState">The logical state that matches the host after its change.</param>
/// <param name="Source">The host-neutral origin of the change.</param>
/// <param name="Route">The route to record in history, or <see langword="null"/> to use the presented route.</param>
/// <param name="Reason">An optional diagnostic reason.</param>
/// <remarks>
/// Reconciliations wait behind any in-flight navigation. If another operation commits first, the router rejects the
/// reconciliation with <see cref="Navigation.StaleNavigationReconciliationException"/> instead of overwriting the newer
/// state. <paramref name="BaseState"/> is compared by reference, so pass a state the router issued, such as the
/// <c>CurrentState</c> or plan target of a presentation context, rather than an equivalent rebuilt state.
/// </remarks>
public sealed record NavigationReconciliation(
    NavigationState BaseState,
    NavigationState TargetState,
    NavigationReconciliationSource Source,
    AppRoute? Route = null,
    string? Reason = null)
{
    public NavigationState BaseState
    {
        get;
        init => field = NavigationIdentity.Required(value, nameof(BaseState));
    } = NavigationIdentity.Required(BaseState, nameof(BaseState));

    public NavigationState TargetState
    {
        get;
        init => field = NavigationIdentity.Required(value, nameof(TargetState));
    } = NavigationIdentity.Required(TargetState, nameof(TargetState));
}

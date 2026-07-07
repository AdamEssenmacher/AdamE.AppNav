using AdamE.AppNav.Internal;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Plans;

public sealed record NavigationPlan(
    NavigationState TargetState,
    NavigationPlanKind Kind = NavigationPlanKind.Navigate,
    string? Reason = null)
{
    public NavigationState TargetState
    {
        get;
        init => field = NavigationIdentity.Required(value, nameof(TargetState));
    } = NavigationIdentity.Required(TargetState, nameof(TargetState));
}

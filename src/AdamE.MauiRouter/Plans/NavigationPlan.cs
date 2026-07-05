using AdamE.MauiRouter.Internal;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Plans;

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

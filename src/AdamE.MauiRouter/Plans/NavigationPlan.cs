using AdamE.MauiRouter.Internal;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Plans;

public sealed record NavigationPlan(
    NavigationState TargetState,
    NavigationPlanKind Kind = NavigationPlanKind.Navigate,
    string? Reason = null)
{
    private readonly NavigationState _targetState = NavigationIdentity.Required(TargetState, nameof(TargetState));

    public NavigationState TargetState
    {
        get => _targetState;
        init => _targetState = NavigationIdentity.Required(value, nameof(TargetState));
    }
}

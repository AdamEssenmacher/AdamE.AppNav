using AdamE.MauiRouter.Internal;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Presentation;

public sealed record NavigationReconciliation(
    NavigationState TargetState,
    NavigationReconciliationSource Source,
    AppRoute? Route = null,
    string? Reason = null)
{
    private readonly NavigationState _targetState = NavigationIdentity.Required(TargetState, nameof(TargetState));

    public NavigationState TargetState
    {
        get => _targetState;
        init => _targetState = NavigationIdentity.Required(value, nameof(TargetState));
    }
}

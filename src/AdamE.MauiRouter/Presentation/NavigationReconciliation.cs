using AdamE.MauiRouter.Internal;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Presentation;

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
}

using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Plans;

public sealed record NavigationPlan(
    NavigationState TargetState,
    NavigationPlanKind Kind = NavigationPlanKind.Navigate,
    string? Reason = null,
    NavigationTransition? Transition = null);

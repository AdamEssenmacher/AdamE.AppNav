using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Presentation;

public sealed record NavigationReconciliation(
    NavigationState TargetState,
    NavigationReconciliationSource Source,
    AppRoute? Route = null,
    string? Reason = null);

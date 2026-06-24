using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Navigation;

public sealed record NavigationResult(
    AppRoute Route,
    NavigationPlan Plan,
    NavigationState State,
    bool Presented);

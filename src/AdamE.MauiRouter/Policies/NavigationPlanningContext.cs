using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Policies;

public sealed record NavigationPlanningContext(
    RouterNavigationRequest Request,
    AppRoute Route,
    NavigationState CurrentState,
    string OperationId);

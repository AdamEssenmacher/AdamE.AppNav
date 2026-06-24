using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Policies;

internal sealed record NavigationPlanningContext<TRoute>(
    RouterNavigationRequest Request,
    TRoute Route,
    NavigationState CurrentState,
    string OperationId)
    where TRoute : AppRoute;

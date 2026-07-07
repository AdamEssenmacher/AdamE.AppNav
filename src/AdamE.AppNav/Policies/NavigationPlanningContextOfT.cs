using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Policies;

internal sealed record NavigationPlanningContext<TRoute>(
    RouterNavigationRequest Request,
    TRoute Route,
    NavigationState CurrentState,
    string OperationId)
    where TRoute : AppRoute;

using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Policies;

public sealed record NavigationPlanningContext(
    RouterNavigationRequest Request,
    AppRoute Route,
    NavigationState CurrentState,
    string OperationId);

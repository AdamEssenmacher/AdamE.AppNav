using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Policies;

public sealed record NavigationRequestPolicyContext(
    RouterNavigationRequest Request,
    AppRoute Route,
    NavigationState CurrentState,
    string OperationId);

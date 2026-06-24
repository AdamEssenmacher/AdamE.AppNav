using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Policies;

public sealed record NavigationRequestPolicyContext(
    RouterNavigationRequest Request,
    AppRoute Route,
    NavigationState CurrentState,
    string OperationId);

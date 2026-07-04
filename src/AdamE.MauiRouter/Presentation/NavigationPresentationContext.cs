using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Presentation;

/// <summary>
/// Provides runtime context for a host presentation operation.
/// </summary>
/// <param name="Request">The finalized navigation request being presented.</param>
/// <param name="Route">The route that the router resolved as the presented route.</param>
/// <param name="CurrentState">The router state before the plan is committed.</param>
/// <param name="OperationId">The diagnostic correlation identifier for the active navigation operation.</param>
public sealed record NavigationPresentationContext(
    RouterNavigationRequest Request,
    AppRoute Route,
    NavigationState CurrentState,
    string OperationId);

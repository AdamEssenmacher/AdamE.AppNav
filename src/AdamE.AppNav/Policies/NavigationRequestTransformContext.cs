using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Policies;

/// <summary>
/// Provides request and router state to a pre-match navigation request transformer.
/// </summary>
public sealed record NavigationRequestTransformContext(
    RouterNavigationRequest Request,
    NavigationState CurrentState,
    string OperationId);

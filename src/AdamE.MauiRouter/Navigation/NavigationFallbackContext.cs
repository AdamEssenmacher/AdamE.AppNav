using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Navigation;

public sealed record NavigationFallbackContext(
    RouterNavigationRequest Request,
    IReadOnlyList<RouteDiagnostic> Diagnostics,
    NavigationState CurrentState,
    string OperationId);

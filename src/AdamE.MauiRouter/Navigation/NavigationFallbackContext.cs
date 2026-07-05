using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;
using JetBrains.Annotations;

namespace AdamE.MauiRouter.Navigation;

/// <summary>
/// Provides route-matching details to a fallback route factory.
/// </summary>
/// <param name="Request">The original navigation request whose URI did not match a configured route.</param>
/// <param name="Diagnostics">Diagnostics produced while attempting to match the request URI.</param>
/// <param name="CurrentState">The router state before a fallback route is selected.</param>
/// <param name="OperationId">The diagnostic correlation identifier for the active navigation operation.</param>
/// <remarks>
/// The router creates this context only after URI route matching reports a plain unmatched route.
/// A fallback route selected from this context continues through the normal router pipeline.
/// </remarks>
public sealed record NavigationFallbackContext(
    [UsedImplicitly] RouterNavigationRequest Request,
    IReadOnlyList<RouteDiagnostic> Diagnostics,
    NavigationState CurrentState,
    string OperationId);

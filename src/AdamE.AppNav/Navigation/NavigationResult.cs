using AdamE.AppNav.Plans;
using AdamE.AppNav.State;
using JetBrains.Annotations;

namespace AdamE.AppNav.Navigation;

/// <summary>
/// Describes the accepted result of a router navigation operation.
/// </summary>
/// <param name="Route">The final route accepted by the router after matching, fallback, redirects, and policies.</param>
/// <param name="Plan">The navigation plan produced for the accepted route.</param>
/// <param name="State">The router state after the operation completed.</param>
/// <param name="Presented">
/// <see langword="true"/> when the operation presented the plan through the configured presenter;
/// otherwise, <see langword="false"/>.
/// </param>
/// <remarks>
/// A result can be accepted without presenter-driven navigation. For example, reconciliation can
/// update router state from host-observed UI state and return a result with <paramref name="Presented"/>
/// set to <see langword="false"/>.
/// </remarks>
public sealed record NavigationResult(
    [UsedImplicitly] AppRoute Route,
    NavigationPlan Plan,
    NavigationState State,
    bool Presented);

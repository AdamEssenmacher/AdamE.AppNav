using AdamE.AppNav.Internal;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Plans;

public sealed record NavigationPlan(
    NavigationState TargetState,
    NavigationPlanKind Kind = NavigationPlanKind.Navigate,
    string? Reason = null)
{
    public NavigationState TargetState
    {
        get;
        init => field = NavigationIdentity.Required(value, nameof(TargetState));
    } = NavigationIdentity.Required(TargetState, nameof(TargetState));

    /// <summary>
    /// Gets a value indicating whether a contextual disposition could not be satisfied, so the plan
    /// rebuilt canonical topology instead of extending the current state.
    /// </summary>
    /// <remarks>
    /// A fallback discards the accumulated stack and branch state that <see
    /// cref="Requests.RouterNavigationDisposition.Contextual"/> and <see
    /// cref="Requests.RouterNavigationDisposition.ReplaceCurrent"/> exist to preserve. This is a
    /// structural signal, so it is safe to report in diagnostics at any data mode. An explicitly
    /// requested canonical plan is not a fallback.
    /// </remarks>
    public bool ContextualFallback { get; init; }
}

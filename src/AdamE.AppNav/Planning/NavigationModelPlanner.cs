using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Planning;

/// <summary>
/// Applies AppNav's standard disposition behavior to a navigation model.
/// </summary>
public sealed class NavigationModelPlanner<TRoute> : IAppNavigationPlanner
    where TRoute : AppRoute
{
    private readonly INavigationModel<TRoute> _model;

    /// <summary>
    /// Creates a planner backed by the supplied topology model.
    /// </summary>
    public NavigationModelPlanner(INavigationModel<TRoute> model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <inheritdoc />
    public ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Route is not TRoute route)
            throw new RoutePlannerNotFoundException(context.Route.GetType());

        RouterNavigationDisposition disposition = ResolveDisposition(context.Request);
        NavigationState? targetState = disposition switch
        {
            RouterNavigationDisposition.Contextual => TryCreateContextualState(
                context,
                route,
                ContextualStackMutationKind.Push),
            RouterNavigationDisposition.ReplaceCurrent => TryCreateContextualState(
                context,
                route,
                ContextualStackMutationKind.ReplaceTop),
            RouterNavigationDisposition.Canonical => null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(context),
                context.Request.Disposition,
                "Unsupported navigation disposition.")
        };

        // A contextual disposition that cannot be satisfied silently produces a fresh canonical
        // topology, which discards any accumulated stack and branch state. Report that in the plan
        // reason so the fallback is observable instead of looking like an ordinary contextual plan.
        bool fellBackToCanonical = targetState is null &&
            disposition is not RouterNavigationDisposition.Canonical;

        targetState ??= _model.CreateCanonicalState(
            route,
            context.Request.Metadata,
            context.Request.WindowId);

        string reason = fellBackToCanonical
            ? $"{disposition} navigation through {typeof(TRoute).Name} model fell back to canonical " +
              "topology because the current state could not be extended contextually; accumulated " +
              "stack and branch state was discarded."
            : $"{disposition} navigation through {typeof(TRoute).Name} model.";

        return ValueTask.FromResult(new NavigationPlan(
            targetState,
            NavigationPlanKind.Navigate,
            reason));
    }

    private NavigationState? TryCreateContextualState(
        NavigationPlanningContext context,
        TRoute route,
        ContextualStackMutationKind mutation)
    {
        if (context.Request.WindowId is not null &&
            !StringComparer.Ordinal.Equals(context.Request.WindowId, context.CurrentState.ActiveWindow?.Id))
            return null;

        return _model.TryCreateContextualState(
            context.CurrentState,
            route,
            mutation,
            context.Request.Metadata);
    }

    private static RouterNavigationDisposition ResolveDisposition(RouterNavigationRequest request)
    {
        if (request.Disposition is not RouterNavigationDisposition.Auto)
            return request.Disposition;

        return request.Source is NavigationRequestSource.InAppCommand or NavigationRequestSource.Test
            ? RouterNavigationDisposition.Contextual
            : RouterNavigationDisposition.Canonical;
    }
}

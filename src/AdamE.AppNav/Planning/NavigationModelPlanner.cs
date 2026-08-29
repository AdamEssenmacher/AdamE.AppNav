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

        targetState ??= _model.CreateCanonicalState(
            route,
            context.Request.Metadata,
            context.Request.WindowId);

        return ValueTask.FromResult(new NavigationPlan(
            targetState,
            NavigationPlanKind.Navigate,
            $"{disposition} navigation through {typeof(TRoute).Name} model."));
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

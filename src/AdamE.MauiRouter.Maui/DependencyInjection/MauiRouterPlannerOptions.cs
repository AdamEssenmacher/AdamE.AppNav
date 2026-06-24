using AdamE.MauiRouter.Policies;

namespace AdamE.MauiRouter.Maui.DependencyInjection;

internal sealed class MauiRouterPlannerOptions
{
    private readonly List<PlannerRegistration> _registrations = new();

    internal IReadOnlyList<PlannerRegistration> Registrations => _registrations;

    public MauiRouterPlannerOptions Map<TRoute, TPlanner>()
        where TRoute : AppRoute
        where TPlanner : class, IAppRoutePlanner<TRoute>
    {
        _registrations.Add(new PlannerRegistration(typeof(TRoute), typeof(TPlanner)));
        return this;
    }

    internal sealed record PlannerRegistration(Type RouteType, Type PlannerType);
}

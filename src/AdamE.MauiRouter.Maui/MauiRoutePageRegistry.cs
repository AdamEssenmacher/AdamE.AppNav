using AdamE.MauiRouter.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

public sealed class MauiRoutePageRegistry
{
    private readonly Dictionary<Type, Func<IServiceProvider, RouteEntry, Page>> _pageFactories = new();

    public MauiRoutePageRegistry MapPage<TRoute, TPage>()
        where TRoute : AppRoute
        where TPage : Page
    {
        _pageFactories[typeof(TRoute)] = (services, entry) =>
            ActivatorUtilities.CreateInstance<TPage>(services, (TRoute)entry.Route);

        return this;
    }

    public MauiRoutePageRegistry MapPageFromServices<TRoute, TPage>()
        where TRoute : AppRoute
        where TPage : Page
    {
        _pageFactories[typeof(TRoute)] = (services, _) => services.GetRequiredService<TPage>();
        return this;
    }

    public MauiRoutePageRegistry MapPage<TRoute>(Func<IServiceProvider, TRoute, Page> factory)
        where TRoute : AppRoute
    {
        ArgumentNullException.ThrowIfNull(factory);

        _pageFactories[typeof(TRoute)] = (services, entry) => factory(services, (TRoute)entry.Route);
        return this;
    }

    internal void Apply(MauiRoutePageRegistry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        foreach (var (routeType, factory) in other._pageFactories)
        {
            _pageFactories[routeType] = factory;
        }
    }

    internal bool TryCreatePage(IServiceProvider services, RouteEntry entry, out Page page)
    {
        var routeType = entry.Route.GetType();
        if (!TypeHierarchyRegistrationLookup.TryGetMostSpecific(_pageFactories, routeType, out var factory))
        {
            page = null!;
            return false;
        }

        page = factory(services, entry);
        return true;
    }
}

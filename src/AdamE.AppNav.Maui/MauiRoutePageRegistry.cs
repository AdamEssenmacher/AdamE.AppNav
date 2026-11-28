using AdamE.AppNav.State;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

/// <summary>
/// Registers one MAUI page factory per exact route type, with fallback to a mapped base route type.
/// </summary>
public sealed class MauiRoutePageRegistry
{
    private readonly Dictionary<Type, Func<IServiceProvider, RouteEntry, Page>> _pageFactories = new();

    /// <summary>
    /// Adds page mappings from a reusable module.
    /// </summary>
    /// <param name="module">The module that will add page mappings to this registry.</param>
    /// <returns>The same registry instance for mapping chaining.</returns>
    public MauiRoutePageRegistry AddModule(IMauiRoutePageModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        module.MapPages(this);
        return this;
    }

    public MauiRoutePageRegistry MapPage<
        TRoute,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    TPage>()
        where TRoute : AppRoute
        where TPage : Page
    {
        AddMapping(typeof(TRoute), (services, entry) =>
            ActivatorUtilities.CreateInstance<TPage>(services, (TRoute)entry.Route));

        return this;
    }

    public MauiRoutePageRegistry MapPageFromServices<TRoute, TPage>()
        where TRoute : AppRoute
        where TPage : Page
    {
        AddMapping(typeof(TRoute), (services, _) => services.GetRequiredService<TPage>());
        return this;
    }

    public MauiRoutePageRegistry MapPage<TRoute>(Func<IServiceProvider, TRoute, Page> factory)
        where TRoute : AppRoute
    {
        ArgumentNullException.ThrowIfNull(factory);

        AddMapping(typeof(TRoute), (services, entry) => factory(services, (TRoute)entry.Route));
        return this;
    }

    internal void Apply(MauiRoutePageRegistry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        foreach (var (routeType, factory) in other._pageFactories)
        {
            AddMapping(routeType, factory);
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

    private void AddMapping(Type routeType, Func<IServiceProvider, RouteEntry, Page> factory)
    {
        if (!_pageFactories.TryAdd(routeType, factory))
        {
            throw new InvalidOperationException(
                $"A MAUI page is already mapped for route type '{routeType.FullName ?? routeType.Name}'.");
        }
    }
}

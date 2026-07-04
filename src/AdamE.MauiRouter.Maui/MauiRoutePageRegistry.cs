using AdamE.MauiRouter.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace AdamE.MauiRouter.Maui;

public sealed class MauiRoutePageRegistry
{
    private readonly Dictionary<Type, Func<IServiceProvider, RouteEntry, Page>> _pageFactories = new();
    private readonly Dictionary<string, MauiBranchHostPresentation> _branchHostPresentations = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Configures how the MAUI presenter should materialize a branch host with the specified id.
    /// </summary>
    /// <param name="branchHostId">The id of the branch host in router state.</param>
    /// <param name="presentation">The MAUI presentation to use for the branch host.</param>
    /// <returns>The same registry instance for mapping chaining.</returns>
    public MauiRoutePageRegistry MapBranchHost(string branchHostId, MauiBranchHostPresentation presentation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchHostId);

        if (!Enum.IsDefined(presentation))
        {
            throw new ArgumentOutOfRangeException(nameof(presentation), presentation, "The branch host presentation is not supported.");
        }

        _branchHostPresentations[branchHostId] = presentation;
        return this;
    }

    /// <summary>
    /// Configures the MAUI presenter to materialize a branch host as a <see cref="TabbedPage"/>.
    /// </summary>
    /// <param name="branchHostId">The id of the branch host in router state.</param>
    /// <returns>The same registry instance for mapping chaining.</returns>
    public MauiRoutePageRegistry MapBranchHostAsTabs(string branchHostId)
    {
        return MapBranchHost(branchHostId, MauiBranchHostPresentation.Tabs);
    }

    /// <summary>
    /// Configures the MAUI presenter to materialize a branch host as a <see cref="FlyoutPage"/>.
    /// </summary>
    /// <param name="branchHostId">The id of the branch host in router state.</param>
    /// <returns>The same registry instance for mapping chaining.</returns>
    public MauiRoutePageRegistry MapBranchHostAsFlyout(string branchHostId)
    {
        return MapBranchHost(branchHostId, MauiBranchHostPresentation.Flyout);
    }

    internal void Apply(MauiRoutePageRegistry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        foreach (var (routeType, factory) in other._pageFactories)
        {
            _pageFactories[routeType] = factory;
        }

        foreach (var (branchHostId, presentation) in other._branchHostPresentations)
        {
            _branchHostPresentations[branchHostId] = presentation;
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

    internal MauiBranchHostPresentation GetBranchHostPresentation(string branchHostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchHostId);

        return _branchHostPresentations.TryGetValue(branchHostId, out var presentation)
            ? presentation
            : MauiBranchHostPresentation.Tabs;
    }
}

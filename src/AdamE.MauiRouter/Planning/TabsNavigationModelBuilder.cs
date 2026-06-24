namespace AdamE.MauiRouter.Planning;

/// <summary>
/// Configures a tabs-navigation model for an application's semantic routes.
/// </summary>
public sealed class TabsNavigationModelBuilder<TRoute>
    where TRoute : AppRoute
{
    private readonly List<TabsBranchDefinition<TRoute>> _branches = [];
    private readonly Dictionary<Type, ITabsRouteRecipeBuilder<TRoute>> _recipes = new();
    private string? _windowId;
    private string? _tabsId;

    /// <summary>
    /// Sets the default canonical window and tabs identifiers used by this model.
    /// </summary>
    public TabsNavigationModelBuilder<TRoute> CanonicalSurface(string windowId, string tabsId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tabsId);

        _windowId = windowId;
        _tabsId = tabsId;
        return this;
    }

    /// <summary>
    /// Registers a canonical tab branch and its sanitized root-route factory.
    /// </summary>
    public TabsNavigationModelBuilder<TRoute> Branch(
        string branchId,
        string title,
        Func<TRoute, TRoute> rootRouteFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(rootRouteFactory);

        if (_branches.Any(branch => StringComparer.Ordinal.Equals(branch.Id, branchId)))
        {
            throw new InvalidOperationException(
                $"A tabs branch is already registered for branch id '{branchId}'.");
        }

        _branches.Add(new TabsBranchDefinition<TRoute>(branchId, title, rootRouteFactory));
        return this;
    }

    /// <summary>
    /// Registers tab-planning behavior for a concrete route type and owning branch.
    /// </summary>
    public TabsNavigationModelBuilder<TRoute> Map<TRouteSpecific>(
        string branchId,
        Action<TabsRouteRecipeBuilder<TRoute, TRouteSpecific>> configure)
        where TRouteSpecific : TRoute
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        ArgumentNullException.ThrowIfNull(configure);

        var routeType = typeof(TRouteSpecific);
        if (_recipes.ContainsKey(routeType))
        {
            throw new InvalidOperationException(
                $"A tabs route recipe is already registered for route type '{routeType.FullName}'.");
        }

        var builder = new TabsRouteRecipeBuilder<TRoute, TRouteSpecific>(branchId);
        configure(builder);
        _recipes.Add(routeType, builder);
        return this;
    }

    internal TabsNavigationModel<TRoute> Build()
    {
        if (string.IsNullOrWhiteSpace(_windowId))
        {
            throw new InvalidOperationException("A tabs navigation model must define a canonical window id.");
        }

        if (string.IsNullOrWhiteSpace(_tabsId))
        {
            throw new InvalidOperationException("A tabs navigation model must define a canonical tabs id.");
        }

        if (_branches.Count == 0)
        {
            throw new InvalidOperationException("A tabs navigation model must define at least one tab branch.");
        }

        var branchesById = _branches.ToDictionary(static branch => branch.Id, StringComparer.Ordinal);
        var recipes = _recipes.Values
            .Select(static builder => builder.Build())
            .ToDictionary(static recipe => recipe.RouteType);

        foreach (var recipe in recipes.Values)
        {
            if (!branchesById.ContainsKey(recipe.BranchId))
            {
                throw new InvalidOperationException(
                    $"Tabs route recipe '{recipe.RouteType.FullName}' must reference a registered branch id. Missing branch '{recipe.BranchId}'.");
            }
        }

        return new TabsNavigationModel<TRoute>(_windowId, _tabsId, _branches, recipes);
    }
}

internal sealed record TabsBranchDefinition<TRoute>(
    string Id,
    string Title,
    Func<TRoute, TRoute> RootRouteFactory)
    where TRoute : AppRoute;

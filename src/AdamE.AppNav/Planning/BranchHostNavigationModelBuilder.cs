namespace AdamE.AppNav.Planning;

/// <summary>
/// Configures a branch-host navigation model for an application's semantic routes.
/// </summary>
public sealed class BranchHostNavigationModelBuilder<TRoute>
    where TRoute : AppRoute
{
    private readonly List<BranchHostBranchDefinition<TRoute>> _branches = [];
    private readonly Dictionary<Type, IBranchHostRouteRecipeBuilder<TRoute>> _recipes = new();
    private string? _windowId;
    private string? _branchHostId;

    /// <summary>
    /// Sets the default canonical window and branch-host identifiers used by this model.
    /// </summary>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public BranchHostNavigationModelBuilder<TRoute> CanonicalSurface(string windowId, string branchHostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchHostId);

        _windowId = windowId;
        _branchHostId = branchHostId;

        return this;
    }

    /// <summary>
    /// Registers a canonical branch and its sanitized root-route factory.
    /// </summary>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public BranchHostNavigationModelBuilder<TRoute> Branch(
        string branchId,
        string title,
        Func<TRoute, TRoute> rootRouteFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(rootRouteFactory);

        if (_branches.Any(branch => StringComparer.Ordinal.Equals(branch.Id, branchId)))
            throw new InvalidOperationException(
                $"A branch-host branch is already registered for branch id '{branchId}'.");

        _branches.Add(new BranchHostBranchDefinition<TRoute>(branchId, title, rootRouteFactory));

        return this;
    }

    /// <summary>
    /// Registers branch-host planning behavior for a concrete route type and owning branch.
    /// </summary>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public BranchHostNavigationModelBuilder<TRoute> Map<TRouteSpecific>(
        string branchId,
        Action<BranchHostRouteRecipeBuilder<TRoute, TRouteSpecific>> configure)
        where TRouteSpecific : TRoute
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        ArgumentNullException.ThrowIfNull(configure);

        Type routeType = typeof(TRouteSpecific);
        if (_recipes.ContainsKey(routeType))
            throw new InvalidOperationException(
                $"A branch-host route recipe is already registered for route type '{routeType.FullName}'.");

        var builder = new BranchHostRouteRecipeBuilder<TRoute, TRouteSpecific>(branchId);
        configure(builder);
        _recipes.Add(routeType, builder);

        return this;
    }

    internal BranchHostNavigationModel<TRoute> Build()
    {
        if (string.IsNullOrWhiteSpace(_windowId))
            throw new InvalidOperationException("A branch-host navigation model must define a canonical window id.");

        if (string.IsNullOrWhiteSpace(_branchHostId))
            throw new InvalidOperationException(
                "A branch-host navigation model must define a canonical branch-host id.");

        if (_branches.Count == 0)
            throw new InvalidOperationException("A branch-host navigation model must define at least one branch.");

        Dictionary<string, BranchHostBranchDefinition<TRoute>> branchesById =
            _branches.ToDictionary(static branch => branch.Id, StringComparer.Ordinal);
        Dictionary<Type, BranchHostRouteRecipe<TRoute>> recipes = _recipes.Values
            .Select(static builder => builder.Build())
            .ToDictionary(static recipe => recipe.RouteType);

        foreach (BranchHostRouteRecipe<TRoute> recipe in recipes.Values.Where(recipe =>
                     !branchesById.ContainsKey(recipe.BranchId)))
            throw new InvalidOperationException(
                $"Branch-host route recipe '{recipe.RouteType.FullName}' must reference a registered branch id. Missing branch '{recipe.BranchId}'.");

        return new BranchHostNavigationModel<TRoute>(_windowId, _branchHostId, _branches, recipes);
    }
}

internal sealed record BranchHostBranchDefinition<TRoute>(
    string Id,
    string Title,
    Func<TRoute, TRoute> RootRouteFactory)
    where TRoute : AppRoute;

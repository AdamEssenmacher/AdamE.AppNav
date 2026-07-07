namespace AdamE.AppNav.Planning;

/// <summary>
/// Configures a stack-navigation model for an application's semantic routes.
/// </summary>
public sealed class StackNavigationModelBuilder<TRoute>
    where TRoute : AppRoute
{
    private readonly Dictionary<Type, IStackRouteRecipeBuilder<TRoute>> _recipes = new();
    private string? _windowId;
    private string? _stackId;

    /// <summary>
    /// Sets the default canonical window and stack identifiers used by this model.
    /// </summary>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public StackNavigationModelBuilder<TRoute> CanonicalSurface(string windowId, string stackId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stackId);

        _windowId = windowId;
        _stackId = stackId;

        return this;
    }

    /// <summary>
    /// Registers stack-planning behavior for a concrete route type.
    /// </summary>
    // ReSharper disable once UnusedMethodReturnValue.Global
    public StackNavigationModelBuilder<TRoute> Map<TRouteSpecific>(
        Action<StackRouteRecipeBuilder<TRoute, TRouteSpecific>> configure)
        where TRouteSpecific : TRoute
    {
        ArgumentNullException.ThrowIfNull(configure);

        Type routeType = typeof(TRouteSpecific);
        if (_recipes.ContainsKey(routeType))
            throw new InvalidOperationException(
                $"A stack route recipe is already registered for route type '{routeType.FullName}'.");

        var builder = new StackRouteRecipeBuilder<TRoute, TRouteSpecific>();
        configure(builder);
        _recipes.Add(routeType, builder);

        return this;
    }

    internal StackNavigationModel<TRoute> Build()
    {
        if (string.IsNullOrWhiteSpace(_windowId))
            throw new InvalidOperationException("A stack navigation model must define a canonical window id.");

        if (string.IsNullOrWhiteSpace(_stackId))
            throw new InvalidOperationException("A stack navigation model must define a canonical stack id.");

        return new StackNavigationModel<TRoute>(
            _windowId,
            _stackId,
            _recipes.Values
                .Select(static builder => builder.Build())
                .ToDictionary(static recipe => recipe.RouteType));
    }
}

namespace AdamE.MauiRouter.Planning;

/// <summary>
/// Configures canonical and contextual stack behavior for a specific application route type.
/// </summary>
public sealed class StackRouteRecipeBuilder<TRouteBase, TRoute> : IStackRouteRecipeBuilder<TRouteBase>
    where TRouteBase : AppRoute
    where TRoute : TRouteBase
{
    private Func<TRoute, string>? _entryIdFactory;
    private Func<TRoute, string?>? _scopeKeyFactory;
    private Func<TRoute, string?>? _slotIdFactory;
    private Func<TRoute, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>>? _canonicalFactory;
    private Func<TRoute, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>>? _contextualTailFactory;
    private ContextualStackEligibility _contextualEligibility = ContextualStackEligibility.MatchingScope;
    private ContextualStackPushBehavior _contextualPushBehavior = ContextualStackPushBehavior.AppendTail;

    /// <summary>
    /// Sets the route-entry identifier factory for this route type.
    /// </summary>
    public StackRouteRecipeBuilder<TRouteBase, TRoute> EntryId(Func<TRoute, string> entryIdFactory)
    {
        _entryIdFactory = entryIdFactory ?? throw new ArgumentNullException(nameof(entryIdFactory));
        return this;
    }

    /// <summary>
    /// Sets the contextual scope key factory for this route type.
    /// </summary>
    public StackRouteRecipeBuilder<TRouteBase, TRoute> ScopeKey(Func<TRoute, string?> scopeKeyFactory)
    {
        _scopeKeyFactory = scopeKeyFactory ?? throw new ArgumentNullException(nameof(scopeKeyFactory));
        return this;
    }

    /// <summary>
    /// Sets the contextual slot identifier factory for this route type.
    /// </summary>
    public StackRouteRecipeBuilder<TRouteBase, TRoute> SlotId(Func<TRoute, string?> slotIdFactory)
    {
        _slotIdFactory = slotIdFactory ?? throw new ArgumentNullException(nameof(slotIdFactory));
        return this;
    }

    /// <summary>
    /// Sets the canonical stack recipe for this route type.
    /// </summary>
    public StackRouteRecipeBuilder<TRouteBase, TRoute> Canonical(
        Func<TRoute, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>> canonicalFactory)
    {
        _canonicalFactory = canonicalFactory ?? throw new ArgumentNullException(nameof(canonicalFactory));
        return this;
    }

    /// <summary>
    /// Sets the contextual tail recipe for this route type.
    /// </summary>
    public StackRouteRecipeBuilder<TRouteBase, TRoute> ContextualTail(
        Func<TRoute, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>> contextualTailFactory)
    {
        _contextualTailFactory = contextualTailFactory ?? throw new ArgumentNullException(nameof(contextualTailFactory));
        return this;
    }

    /// <summary>
    /// Sets whether contextual planning requires a matching scope.
    /// </summary>
    public StackRouteRecipeBuilder<TRouteBase, TRoute> ContextualEligibility(ContextualStackEligibility contextualEligibility)
    {
        _contextualEligibility = contextualEligibility;
        return this;
    }

    /// <summary>
    /// Sets how contextual planning applies this route when the current stack is eligible.
    /// </summary>
    public StackRouteRecipeBuilder<TRouteBase, TRoute> ContextualPushBehavior(ContextualStackPushBehavior contextualPushBehavior)
    {
        _contextualPushBehavior = contextualPushBehavior;
        return this;
    }

    Type IStackRouteRecipeBuilder<TRouteBase>.RouteType => typeof(TRoute);

    StackRouteRecipe<TRouteBase> IStackRouteRecipeBuilder<TRouteBase>.Build()
    {
        if (_entryIdFactory is null)
        {
            throw new InvalidOperationException(
                $"Stack route recipe '{typeof(TRoute).FullName}' must define an entry id factory.");
        }

        return new StackRouteRecipe<TRouteBase>(
            typeof(TRoute),
            route => _entryIdFactory((TRoute)route),
            _scopeKeyFactory is null ? null : route => _scopeKeyFactory((TRoute)route),
            _slotIdFactory is null ? null : route => _slotIdFactory((TRoute)route),
            _canonicalFactory is null
                ? (route, metadata) => [new StackRouteStep<TRouteBase>(route, metadata)]
                : (route, metadata) => _canonicalFactory((TRoute)route, metadata),
            _contextualTailFactory is null
                ? (route, metadata) => [new StackRouteStep<TRouteBase>(route, metadata)]
                : (route, metadata) => _contextualTailFactory((TRoute)route, metadata),
            _contextualEligibility,
            _contextualPushBehavior);
    }
}

internal interface IStackRouteRecipeBuilder<TRouteBase>
    where TRouteBase : AppRoute
{
    Type RouteType { get; }

    StackRouteRecipe<TRouteBase> Build();
}

internal sealed record StackRouteRecipe<TRouteBase>(
    Type RouteType,
    Func<TRouteBase, string> EntryIdFactory,
    Func<TRouteBase, string?>? ScopeKeyFactory,
    Func<TRouteBase, string?>? SlotIdFactory,
    Func<TRouteBase, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>> CanonicalFactory,
    Func<TRouteBase, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>> ContextualTailFactory,
    ContextualStackEligibility ContextualEligibility,
    ContextualStackPushBehavior ContextualPushBehavior)
    where TRouteBase : AppRoute;

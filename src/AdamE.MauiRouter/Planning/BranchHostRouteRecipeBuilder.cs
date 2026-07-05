namespace AdamE.MauiRouter.Planning;

/// <summary>
/// Configures canonical and contextual branch-host behavior for a specific application route type.
/// </summary>
public sealed class BranchHostRouteRecipeBuilder<TRouteBase, TRoute> : IBranchHostRouteRecipeBuilder<TRouteBase>
    where TRouteBase : AppRoute
    where TRoute : TRouteBase
{
    private readonly string _branchId;
    private Func<TRoute, string>? _entryIdFactory;
    private Func<TRoute, string?>? _scopeKeyFactory;
    private Func<TRoute, string?>? _slotIdFactory;
    private Func<TRoute, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>>? _canonicalFactory;
    private Func<TRoute, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>>? _contextualTailFactory;
    private ContextualStackEligibility _contextualEligibility = ContextualStackEligibility.MatchingScope;

    internal BranchHostRouteRecipeBuilder(string branchId)
    {
        _branchId = branchId;
    }

    /// <summary>
    /// Sets the presenter reuse identifier factory for entries of this route type.
    /// </summary>
    public BranchHostRouteRecipeBuilder<TRouteBase, TRoute> EntryId(Func<TRoute, string> entryIdFactory)
    {
        _entryIdFactory = entryIdFactory ?? throw new ArgumentNullException(nameof(entryIdFactory));
        return this;
    }

    /// <summary>
    /// Sets the contextual eligibility key factory for this route type.
    /// </summary>
    public BranchHostRouteRecipeBuilder<TRouteBase, TRoute> ScopeKey(Func<TRoute, string?> scopeKeyFactory)
    {
        _scopeKeyFactory = scopeKeyFactory ?? throw new ArgumentNullException(nameof(scopeKeyFactory));
        return this;
    }

    /// <summary>
    /// Sets the contextual replacement/merge identifier factory for this route type.
    /// </summary>
    public BranchHostRouteRecipeBuilder<TRouteBase, TRoute> SlotId(Func<TRoute, string?> slotIdFactory)
    {
        _slotIdFactory = slotIdFactory ?? throw new ArgumentNullException(nameof(slotIdFactory));
        return this;
    }

    /// <summary>
    /// Sets the canonical branch-stack recipe for this route type.
    /// </summary>
    public BranchHostRouteRecipeBuilder<TRouteBase, TRoute> Canonical(
        Func<TRoute, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>> canonicalFactory)
    {
        _canonicalFactory = canonicalFactory ?? throw new ArgumentNullException(nameof(canonicalFactory));
        return this;
    }

    /// <summary>
    /// Sets the contextual branch-tail recipe for this route type.
    /// </summary>
    public BranchHostRouteRecipeBuilder<TRouteBase, TRoute> ContextualTail(
        Func<TRoute, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>> contextualTailFactory)
    {
        _contextualTailFactory = contextualTailFactory ?? throw new ArgumentNullException(nameof(contextualTailFactory));
        return this;
    }

    /// <summary>
    /// Sets whether contextual planning requires a matching scope.
    /// </summary>
    public BranchHostRouteRecipeBuilder<TRouteBase, TRoute> ContextualEligibility(ContextualStackEligibility contextualEligibility)
    {
        _contextualEligibility = contextualEligibility;
        return this;
    }

    Type IBranchHostRouteRecipeBuilder<TRouteBase>.RouteType => typeof(TRoute);

    BranchHostRouteRecipe<TRouteBase> IBranchHostRouteRecipeBuilder<TRouteBase>.Build()
    {
        if (_entryIdFactory is null)
        {
            throw new InvalidOperationException(
                $"Branch-host route recipe '{typeof(TRoute).FullName}' must define an entry id factory.");
        }

        return new BranchHostRouteRecipe<TRouteBase>(
            typeof(TRoute),
            _branchId,
            route => _entryIdFactory((TRoute)route),
            _scopeKeyFactory is null ? null : route => _scopeKeyFactory((TRoute)route),
            _slotIdFactory is null ? null : route => _slotIdFactory((TRoute)route),
            _canonicalFactory is null
                ? (route, metadata) => [new StackRouteStep<TRouteBase>(route, metadata)]
                : (route, metadata) => _canonicalFactory((TRoute)route, metadata),
            _contextualTailFactory is null
                ? (route, metadata) => [new StackRouteStep<TRouteBase>(route, metadata)]
                : (route, metadata) => _contextualTailFactory((TRoute)route, metadata),
            _contextualEligibility);
    }
}

internal interface IBranchHostRouteRecipeBuilder<TRouteBase>
    where TRouteBase : AppRoute
{
    Type RouteType { get; }

    BranchHostRouteRecipe<TRouteBase> Build();
}

internal sealed record BranchHostRouteRecipe<TRouteBase>(
    Type RouteType,
    string BranchId,
    Func<TRouteBase, string> EntryIdFactory,
    Func<TRouteBase, string?>? ScopeKeyFactory,
    Func<TRouteBase, string?>? SlotIdFactory,
    Func<TRouteBase, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>> CanonicalFactory,
    Func<TRouteBase, IReadOnlyDictionary<string, object?>?, IReadOnlyList<StackRouteStep<TRouteBase>>> ContextualTailFactory,
    ContextualStackEligibility ContextualEligibility)
    where TRouteBase : AppRoute;

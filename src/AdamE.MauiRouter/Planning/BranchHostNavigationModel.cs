using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Planning;

/// <summary>
/// Provides canonical branch-host creation and contextual branch mutation for an application's semantic routes.
/// </summary>
public sealed class BranchHostNavigationModel<TRoute>
    where TRoute : AppRoute
{
    private readonly IReadOnlyList<BranchHostBranchDefinition<TRoute>> _branches;
    private readonly IReadOnlyDictionary<string, BranchHostBranchDefinition<TRoute>> _branchesById;
    private readonly IReadOnlyDictionary<Type, BranchHostRouteRecipe<TRoute>> _recipes;
    private readonly string _defaultWindowId;
    private readonly string _defaultBranchHostId;

    internal BranchHostNavigationModel(
        string defaultWindowId,
        string defaultBranchHostId,
        IReadOnlyList<BranchHostBranchDefinition<TRoute>> branches,
        IReadOnlyDictionary<Type, BranchHostRouteRecipe<TRoute>> recipes)
    {
        _defaultWindowId = defaultWindowId;
        _defaultBranchHostId = defaultBranchHostId;
        _branches = branches.ToArray();
        _branchesById = _branches.ToDictionary(static branch => branch.Id, StringComparer.Ordinal);
        _recipes = recipes;
    }

    /// <summary>
    /// Creates a branch-host navigation model from the supplied configuration.
    /// </summary>
    public static BranchHostNavigationModel<TRoute> Create(Action<BranchHostNavigationModelBuilder<TRoute>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new BranchHostNavigationModelBuilder<TRoute>();
        configure(builder);
        return builder.Build();
    }

    /// <summary>
    /// Creates a route entry using the registered entry-id and metadata behavior for the supplied route.
    /// </summary>
    public RouteEntry CreateEntry(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        var recipe = GetRecipe(route);
        var entryId = recipe.EntryIdFactory(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        return new RouteEntry(entryId, route, Metadata: metadata);
    }

    /// <summary>
    /// Creates the canonical branch-host navigation state for the supplied route and metadata.
    /// </summary>
    public NavigationState CreateCanonicalState(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata = null,
        string? windowId = null,
        string? branchHostId = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        var resolvedWindowId = string.IsNullOrWhiteSpace(windowId) ? _defaultWindowId : windowId;
        var resolvedBranchHostId = string.IsNullOrWhiteSpace(branchHostId) ? _defaultBranchHostId : branchHostId;

        return new NavigationState(
            [
                new WindowNode(resolvedWindowId, CreateCanonicalBranchHostNode(route, metadata, resolvedBranchHostId))
            ],
            resolvedWindowId);
    }

    /// <summary>
    /// Attempts to create a contextual branch-host navigation state for the supplied route and current branch-host tree.
    /// </summary>
    public NavigationState? TryCreateContextualState(
        NavigationState currentState,
        TRoute route,
        ContextualStackMutationKind mutation,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(route);

        if (currentState.ActiveWindow is not { Root: BranchHostNode currentBranchHost } window ||
            currentBranchHost.SelectedBranch is not { Content: StackNode currentSelectedStack } selectedBranch ||
            currentSelectedStack.Entries.Count == 0 ||
            currentSelectedStack.Entries[0].Route is not TRoute currentRootRoute)
        {
            return null;
        }

        var recipe = GetRecipe(route);
        if (!IsEligible(currentRootRoute, route, recipe.ContextualEligibility) ||
            !_branchesById.TryGetValue(recipe.BranchId, out var owningBranch) ||
            currentBranchHost.Branches.FirstOrDefault(branch => StringComparer.Ordinal.Equals(branch.Id, recipe.BranchId)) is not { Content: StackNode currentOwningStack } currentBranch)
        {
            return null;
        }

        StackNode? nextOwningStack = IsBranchRoot(owningBranch, route)
            ? CreateCanonicalBranchStack(owningBranch, route, metadata, currentOwningStack.Id)
            : mutation switch
            {
                ContextualStackMutationKind.Push => currentOwningStack with
                {
                    Entries = BuildPushedEntries(currentOwningStack.Entries, route, metadata)
                },
                ContextualStackMutationKind.ReplaceTop => currentOwningStack with
                {
                    Entries = BuildReplacedEntries(currentOwningStack.Entries, route, metadata) ?? Array.Empty<RouteEntry>()
                },
                _ => null
            };

        if (nextOwningStack is null ||
            mutation is ContextualStackMutationKind.ReplaceTop && currentOwningStack.Entries.Count == 0)
        {
            return null;
        }

        var nextBranchHost = currentBranchHost.ReplaceBranch(currentBranch with { Content = nextOwningStack }) with
        {
            SelectedBranchId = owningBranch.Id
        };

        return currentState.ReplaceWindow(window with { Root = nextBranchHost });
    }

    private BranchHostNode CreateCanonicalBranchHostNode(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata,
        string branchHostId)
    {
        var recipe = GetRecipe(route);
        var branches = new NavigationBranch[_branches.Count];

        for (var i = 0; i < _branches.Count; i++)
        {
            var branch = _branches[i];
            var stack = StringComparer.Ordinal.Equals(branch.Id, recipe.BranchId)
                ? CreateCanonicalBranchStack(branch, route, metadata, BuildBranchStackId(branchHostId, branch.Id))
                : CreateSanitizedBranchStack(branch, route, branchHostId);
            branches[i] = new NavigationBranch(branch.Id, branch.Title, stack);
        }

        return new BranchHostNode(
            branchHostId,
            branches,
            recipe.BranchId,
            _branches[0].Id);
    }

    private StackNode CreateCanonicalBranchStack(
        BranchHostBranchDefinition<TRoute> branch,
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata,
        string stackId)
    {
        return new StackNode(stackId, BuildCanonicalEntries(route, metadata));
    }

    private StackNode CreateSanitizedBranchStack(
        BranchHostBranchDefinition<TRoute> branch,
        TRoute scopedRoute,
        string branchHostId)
    {
        var rootRoute = branch.RootRouteFactory(scopedRoute) ??
                        throw new InvalidOperationException(
                            $"Branch-host branch '{branch.Id}' returned a null root route.");
        return new StackNode(
            BuildBranchStackId(branchHostId, branch.Id),
            [CreateEntry(rootRoute)]);
    }

    private IReadOnlyList<RouteEntry> BuildCanonicalEntries(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        return BuildEntries(GetRecipe(route).CanonicalFactory(route, metadata));
    }

    private IReadOnlyList<RouteEntry> BuildContextualTailEntries(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        return BuildEntries(GetRecipe(route).ContextualTailFactory(route, metadata));
    }

    private IReadOnlyList<RouteEntry> BuildEntries(IReadOnlyList<StackRouteStep<TRoute>> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var entries = new List<RouteEntry>(steps.Count);
        foreach (var step in steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (!_recipes.ContainsKey(step.Route.GetType()))
            {
                throw new InvalidOperationException(
                    $"Branch-host route step '{step.Route.GetType().FullName}' must be registered before it can participate in branch-host planning.");
            }

            entries.Add(CreateEntry(step.Route, step.Metadata));
        }

        return entries;
    }

    private IReadOnlyList<RouteEntry> BuildPushedEntries(
        IReadOnlyList<RouteEntry> currentEntries,
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        var nextEntries = currentEntries.ToList();
        AppendTail(nextEntries, BuildContextualTailEntries(route, metadata));
        return nextEntries;
    }

    private IReadOnlyList<RouteEntry>? BuildReplacedEntries(
        IReadOnlyList<RouteEntry> currentEntries,
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        if (currentEntries.Count == 0)
        {
            return null;
        }

        var preservedCount = currentEntries.Count == 1 ? 1 : currentEntries.Count - 1;
        var nextEntries = currentEntries
            .Take(preservedCount)
            .ToList();
        AppendTail(nextEntries, BuildContextualTailEntries(route, metadata));
        return nextEntries;
    }

    private void AppendTail(List<RouteEntry> entries, IReadOnlyList<RouteEntry> tailEntries)
    {
        foreach (var tailEntry in tailEntries)
        {
            var matchIndex = FindMatchingEntryIndex(entries, tailEntry);
            if (matchIndex >= 0)
            {
                entries[matchIndex] = tailEntry;
                entries.RemoveRange(matchIndex + 1, entries.Count - matchIndex - 1);
                continue;
            }

            entries.Add(tailEntry);
        }
    }

    private bool ShouldReplaceWith(RouteEntry existingEntry, RouteEntry replacementEntry)
    {
        if (StringComparer.Ordinal.Equals(existingEntry.Id, replacementEntry.Id))
        {
            return true;
        }

        return AreSameSlot(existingEntry, replacementEntry);
    }

    private int FindMatchingEntryIndex(IReadOnlyList<RouteEntry> entries, RouteEntry replacementEntry)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (ShouldReplaceWith(entries[i], replacementEntry))
            {
                return i;
            }
        }

        return -1;
    }

    private bool AreSameSlot(RouteEntry left, RouteEntry right)
    {
        if (left.Route is not TRoute leftRoute || right.Route is not TRoute rightRoute)
        {
            return false;
        }

        var leftSlotId = GetSlotId(leftRoute);
        var rightSlotId = GetSlotId(rightRoute);
        return !string.IsNullOrWhiteSpace(leftSlotId) &&
               !string.IsNullOrWhiteSpace(rightSlotId) &&
               StringComparer.Ordinal.Equals(leftSlotId, rightSlotId);
    }

    private bool IsEligible(
        TRoute currentRootRoute,
        TRoute targetRoute,
        ContextualStackEligibility contextualEligibility)
    {
        return contextualEligibility switch
        {
            ContextualStackEligibility.AnyScope => true,
            ContextualStackEligibility.MatchingScope => HasMatchingScope(currentRootRoute, targetRoute),
            _ => false
        };
    }

    private bool HasMatchingScope(TRoute currentRootRoute, TRoute targetRoute)
    {
        var currentScope = GetScopeKey(currentRootRoute);
        var targetScope = GetScopeKey(targetRoute);
        return !string.IsNullOrWhiteSpace(currentScope) &&
               !string.IsNullOrWhiteSpace(targetScope) &&
               StringComparer.Ordinal.Equals(currentScope, targetScope);
    }

    private bool IsBranchRoot(BranchHostBranchDefinition<TRoute> branch, TRoute route)
    {
        return EqualityComparer<TRoute>.Default.Equals(branch.RootRouteFactory(route), route);
    }

    private string? GetSlotId(TRoute route)
    {
        var recipe = GetRecipe(route);
        return recipe.SlotIdFactory?.Invoke(route) ?? recipe.EntryIdFactory(route);
    }

    private string? GetScopeKey(TRoute route)
    {
        return GetRecipe(route).ScopeKeyFactory?.Invoke(route);
    }

    private BranchHostRouteRecipe<TRoute> GetRecipe(TRoute route)
    {
        if (!_recipes.TryGetValue(route.GetType(), out var recipe))
        {
            throw new InvalidOperationException(
                $"Route '{route.GetType().FullName}' is not registered in this branch-host navigation model.");
        }

        return recipe;
    }

    private static string BuildBranchStackId(string branchHostId, string branchId)
    {
        return $"{branchHostId}:{branchId}";
    }
}

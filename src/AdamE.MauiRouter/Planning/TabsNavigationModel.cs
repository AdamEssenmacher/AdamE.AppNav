using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Planning;

/// <summary>
/// Provides canonical tabs creation and contextual branch mutation for an application's semantic routes.
/// </summary>
public sealed class TabsNavigationModel<TRoute>
    where TRoute : AppRoute
{
    private readonly IReadOnlyList<TabsBranchDefinition<TRoute>> _branches;
    private readonly IReadOnlyDictionary<string, TabsBranchDefinition<TRoute>> _branchesById;
    private readonly IReadOnlyDictionary<Type, TabsRouteRecipe<TRoute>> _recipes;
    private readonly string _defaultWindowId;
    private readonly string _defaultTabsId;

    internal TabsNavigationModel(
        string defaultWindowId,
        string defaultTabsId,
        IReadOnlyList<TabsBranchDefinition<TRoute>> branches,
        IReadOnlyDictionary<Type, TabsRouteRecipe<TRoute>> recipes)
    {
        _defaultWindowId = defaultWindowId;
        _defaultTabsId = defaultTabsId;
        _branches = branches.ToArray();
        _branchesById = _branches.ToDictionary(static branch => branch.Id, StringComparer.Ordinal);
        _recipes = recipes;
    }

    /// <summary>
    /// Creates a tabs-navigation model from the supplied configuration.
    /// </summary>
    public static TabsNavigationModel<TRoute> Create(Action<TabsNavigationModelBuilder<TRoute>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new TabsNavigationModelBuilder<TRoute>();
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
    /// Creates the canonical tabs navigation state for the supplied route and metadata.
    /// </summary>
    public NavigationState CreateCanonicalState(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata = null,
        string? windowId = null,
        string? tabsId = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        var resolvedWindowId = string.IsNullOrWhiteSpace(windowId) ? _defaultWindowId : windowId;
        var resolvedTabsId = string.IsNullOrWhiteSpace(tabsId) ? _defaultTabsId : tabsId;

        return new NavigationState(
            [
                new WindowNode(resolvedWindowId, CreateCanonicalTabsNode(route, metadata, resolvedTabsId))
            ],
            resolvedWindowId);
    }

    /// <summary>
    /// Attempts to create a contextual tabs navigation state for the supplied route and current tabs tree.
    /// </summary>
    public NavigationState? TryCreateContextualState(
        NavigationState currentState,
        TRoute route,
        ContextualStackMutationKind mutation,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(route);

        if (currentState.ActiveWindow is not { Root: TabsNode currentTabs } window ||
            currentTabs.SelectedBranch is not { Content: StackNode currentSelectedStack } selectedBranch ||
            currentSelectedStack.Entries.Count == 0 ||
            currentSelectedStack.Entries[0].Route is not TRoute currentRootRoute)
        {
            return null;
        }

        var recipe = GetRecipe(route);
        if (!IsEligible(currentRootRoute, route, recipe.ContextualEligibility) ||
            !_branchesById.TryGetValue(recipe.BranchId, out var owningBranch) ||
            currentTabs.Branches.FirstOrDefault(branch => StringComparer.Ordinal.Equals(branch.Id, recipe.BranchId)) is not { Content: StackNode currentOwningStack } currentBranch)
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

        var nextTabs = currentTabs.ReplaceBranch(currentBranch with { Content = nextOwningStack }) with
        {
            SelectedTabId = owningBranch.Id
        };

        return currentState.ReplaceWindow(window with { Root = nextTabs });
    }

    private TabsNode CreateCanonicalTabsNode(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata,
        string tabsId)
    {
        var recipe = GetRecipe(route);
        var branches = new NavigationBranch[_branches.Count];

        for (var i = 0; i < _branches.Count; i++)
        {
            var branch = _branches[i];
            var stack = StringComparer.Ordinal.Equals(branch.Id, recipe.BranchId)
                ? CreateCanonicalBranchStack(branch, route, metadata, BuildBranchStackId(tabsId, branch.Id))
                : CreateSanitizedBranchStack(branch, route, tabsId);
            branches[i] = new NavigationBranch(branch.Id, branch.Title, stack);
        }

        return new TabsNode(
            tabsId,
            branches,
            recipe.BranchId,
            _branches[0].Id);
    }

    private StackNode CreateCanonicalBranchStack(
        TabsBranchDefinition<TRoute> branch,
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata,
        string stackId)
    {
        return new StackNode(stackId, BuildCanonicalEntries(route, metadata));
    }

    private StackNode CreateSanitizedBranchStack(
        TabsBranchDefinition<TRoute> branch,
        TRoute scopedRoute,
        string tabsId)
    {
        var rootRoute = branch.RootRouteFactory(scopedRoute) ??
                        throw new InvalidOperationException(
                            $"Tabs branch '{branch.Id}' returned a null root route.");
        return new StackNode(
            BuildBranchStackId(tabsId, branch.Id),
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
                    $"Tabs route step '{step.Route.GetType().FullName}' must be registered before it can participate in tabs planning.");
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
        MergeReplacementTail(nextEntries, BuildContextualTailEntries(route, metadata));
        return nextEntries;
    }

    private void AppendTail(List<RouteEntry> entries, IReadOnlyList<RouteEntry> tailEntries)
    {
        if (tailEntries.Count == 0)
        {
            return;
        }

        for (var i = 0; i < tailEntries.Count; i++)
        {
            if (i == 0)
            {
                EnsureTail(entries, tailEntries[i]);
                continue;
            }

            entries.Add(tailEntries[i]);
        }
    }

    private void MergeReplacementTail(List<RouteEntry> preservedEntries, IReadOnlyList<RouteEntry> replacementTail)
    {
        if (replacementTail.Count == 0)
        {
            return;
        }

        if (preservedEntries.LastOrDefault() is { } lastEntry &&
            ShouldReplaceWith(lastEntry, replacementTail[0]))
        {
            preservedEntries[^1] = replacementTail[0];
            for (var i = 1; i < replacementTail.Count; i++)
            {
                preservedEntries.Add(replacementTail[i]);
            }

            return;
        }

        preservedEntries.AddRange(replacementTail);
    }

    private void EnsureTail(List<RouteEntry> entries, RouteEntry requiredEntry)
    {
        if (entries.LastOrDefault() is not { } lastEntry)
        {
            entries.Add(requiredEntry);
            return;
        }

        if (ShouldReplaceWith(lastEntry, requiredEntry))
        {
            entries[^1] = requiredEntry;
            return;
        }

        entries.Add(requiredEntry);
    }

    private bool ShouldReplaceWith(RouteEntry existingEntry, RouteEntry replacementEntry)
    {
        if (StringComparer.Ordinal.Equals(existingEntry.Id, replacementEntry.Id))
        {
            return true;
        }

        return AreSameSlot(existingEntry, replacementEntry);
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

    private bool IsBranchRoot(TabsBranchDefinition<TRoute> branch, TRoute route)
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

    private TabsRouteRecipe<TRoute> GetRecipe(TRoute route)
    {
        if (!_recipes.TryGetValue(route.GetType(), out var recipe))
        {
            throw new InvalidOperationException(
                $"Route '{route.GetType().FullName}' is not registered in this tabs navigation model.");
        }

        return recipe;
    }

    private static string BuildBranchStackId(string tabsId, string branchId)
    {
        return $"{tabsId}:{branchId}";
    }
}

using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Planning;

/// <summary>
/// Provides canonical stack creation and contextual stack mutation for an application's semantic routes.
/// </summary>
public sealed class StackNavigationModel<TRoute>
    where TRoute : AppRoute
{
    private readonly IReadOnlyDictionary<Type, StackRouteRecipe<TRoute>> _recipes;
    private readonly string _defaultWindowId;
    private readonly string _defaultStackId;

    internal StackNavigationModel(
        string defaultWindowId,
        string defaultStackId,
        IReadOnlyDictionary<Type, StackRouteRecipe<TRoute>> recipes)
    {
        _defaultWindowId = defaultWindowId;
        _defaultStackId = defaultStackId;
        _recipes = recipes;
    }

    /// <summary>
    /// Creates a stack-navigation model from the supplied configuration.
    /// </summary>
    public static StackNavigationModel<TRoute> Create(Action<StackNavigationModelBuilder<TRoute>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new StackNavigationModelBuilder<TRoute>();
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
    /// Creates the canonical navigation state for the supplied route and metadata.
    /// </summary>
    public NavigationState CreateCanonicalState(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata = null,
        string? windowId = null,
        string? stackId = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        var canonicalEntries = BuildCanonicalEntries(route, metadata);
        var resolvedWindowId = string.IsNullOrWhiteSpace(windowId) ? _defaultWindowId : windowId;
        var resolvedStackId = string.IsNullOrWhiteSpace(stackId) ? _defaultStackId : stackId;

        return new NavigationState(
            new[]
            {
                new WindowNode(resolvedWindowId, new StackNode(resolvedStackId, canonicalEntries))
            },
            resolvedWindowId);
    }

    /// <summary>
    /// Attempts to create a contextual navigation state for the supplied route and current stack.
    /// </summary>
    public NavigationState? TryCreateContextualState(
        NavigationState currentState,
        TRoute route,
        ContextualStackMutationKind mutation,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(route);

        if (currentState.ActiveWindow is not { Root: StackNode currentStack } window ||
            currentStack.Entries.Count == 0 ||
            currentStack.Entries[0].Route is not TRoute currentRootRoute)
        {
            return null;
        }

        var recipe = GetRecipe(route);
        if (!IsEligible(currentRootRoute, route, recipe.ContextualEligibility))
        {
            return null;
        }

        if (recipe.ContextualPushBehavior == ContextualStackPushBehavior.ReplaceWithCanonicalStack)
        {
            return currentState.ReplaceWindow(window with
            {
                Root = new StackNode(currentStack.Id, BuildCanonicalEntries(route, metadata))
            });
        }

        IReadOnlyList<RouteEntry>? nextEntries = mutation switch
        {
            ContextualStackMutationKind.Push => BuildPushedEntries(currentStack.Entries, route, metadata),
            ContextualStackMutationKind.ReplaceTop => BuildReplacedEntries(currentStack.Entries, route, metadata),
            _ => null
        };

        if (nextEntries is null)
        {
            return null;
        }

        return currentState.ReplaceWindow(window with
        {
            Root = currentStack with { Entries = nextEntries }
        });
    }

    private IReadOnlyList<RouteEntry> BuildCanonicalEntries(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        var recipe = GetRecipe(route);
        return BuildEntries(recipe.CanonicalFactory(route, metadata));
    }

    private IReadOnlyList<RouteEntry> BuildContextualTailEntries(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        var recipe = GetRecipe(route);
        return BuildEntries(recipe.ContextualTailFactory(route, metadata));
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
                    $"Stack route step '{step.Route.GetType().FullName}' must be registered before it can participate in stack planning.");
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
        var tailEntries = BuildContextualTailEntries(route, metadata);
        AppendTail(nextEntries, tailEntries);
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

        var nextEntries = currentEntries
            .Take(Math.Max(0, currentEntries.Count - 1))
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

    private string? GetSlotId(TRoute route)
    {
        var recipe = GetRecipe(route);
        return recipe.SlotIdFactory?.Invoke(route) ?? recipe.EntryIdFactory(route);
    }

    private string? GetScopeKey(TRoute route)
    {
        return GetRecipe(route).ScopeKeyFactory?.Invoke(route);
    }

    private StackRouteRecipe<TRoute> GetRecipe(TRoute route)
    {
        if (!_recipes.TryGetValue(route.GetType(), out var recipe))
        {
            throw new InvalidOperationException(
                $"Route '{route.GetType().FullName}' is not registered in this stack navigation model.");
        }

        return recipe;
    }
}

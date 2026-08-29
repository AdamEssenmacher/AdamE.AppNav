using AdamE.AppNav.Navigation;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Planning;

/// <summary>
/// Provides canonical stack creation and contextual stack mutation for an application's semantic routes.
/// </summary>
public sealed class StackNavigationModel<TRoute> : INavigationModel<TRoute>
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

        StackRouteRecipe<TRoute> recipe = GetRecipe(route);
        string entryId = recipe.EntryIdFactory(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);

        return new RouteEntry(entryId, route, metadata);
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

        IReadOnlyList<RouteEntry> canonicalEntries = BuildCanonicalEntries(route, metadata);
        string resolvedWindowId = string.IsNullOrWhiteSpace(windowId) ? _defaultWindowId : windowId;
        string resolvedStackId = string.IsNullOrWhiteSpace(stackId) ? _defaultStackId : stackId;

        return new NavigationState(
            [
                new WindowNode(resolvedWindowId, new StackNode(resolvedStackId, canonicalEntries))
            ],
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
            return null;

        StackRouteRecipe<TRoute> recipe = GetRecipe(route);
        if (!IsEligible(currentRootRoute, route, recipe.ContextualEligibility)) return null;

        if (recipe.ContextualPushBehavior == ContextualStackPushBehavior.ReplaceWithCanonicalStack)
            return currentState.ReplaceWindow(window with
            {
                Root = new StackNode(currentStack.Id, BuildCanonicalEntries(route, metadata))
            });

        IReadOnlyList<RouteEntry>? nextEntries = mutation switch
        {
            ContextualStackMutationKind.Push => BuildPushedEntries(currentStack.Entries, route, metadata),
            ContextualStackMutationKind.ReplaceTop => BuildReplacedEntries(currentStack.Entries, route, metadata),
            _ => null
        };

        if (nextEntries is null) return null;

        return currentState.ReplaceWindow(window with
        {
            Root = currentStack with { Entries = nextEntries }
        });
    }

    private List<RouteEntry> BuildCanonicalEntries(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        StackRouteRecipe<TRoute> recipe = GetRecipe(route);
        return BuildEntries(recipe.CanonicalFactory(route, metadata));
    }

    private List<RouteEntry> BuildContextualTailEntries(
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        StackRouteRecipe<TRoute> recipe = GetRecipe(route);

        return BuildEntries(recipe.ContextualTailFactory(route, metadata));
    }

    private List<RouteEntry> BuildEntries(IReadOnlyList<StackRouteStep<TRoute>> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var entries = new List<RouteEntry>(steps.Count);
        foreach (StackRouteStep<TRoute> step in steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (!_recipes.ContainsKey(step.Route.GetType()))
                throw new AppNavigationConfigurationException(
                    $"Stack route step '{step.Route.GetType().FullName}' must be registered before it can participate in stack planning.");

            entries.Add(CreateEntry(step.Route, step.Metadata));
        }

        return entries;
    }

    private List<RouteEntry> BuildPushedEntries(
        IReadOnlyList<RouteEntry> currentEntries,
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        List<RouteEntry> nextEntries = currentEntries.ToList();
        IReadOnlyList<RouteEntry> tailEntries = BuildContextualTailEntries(route, metadata);
        AppendTail(nextEntries, tailEntries);

        return nextEntries;
    }

    private List<RouteEntry>? BuildReplacedEntries(
        IReadOnlyList<RouteEntry> currentEntries,
        TRoute route,
        IReadOnlyDictionary<string, object?>? metadata)
    {
        if (currentEntries.Count == 0) return null;

        List<RouteEntry> nextEntries = currentEntries
            .Take(Math.Max(0, currentEntries.Count - 1))
            .ToList();

        AppendTail(nextEntries, BuildContextualTailEntries(route, metadata));

        return nextEntries;
    }

    private void AppendTail(List<RouteEntry> entries, IReadOnlyList<RouteEntry> tailEntries)
    {
        foreach (RouteEntry tailEntry in tailEntries)
        {
            int matchIndex = FindMatchingEntryIndex(entries, tailEntry);
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
        return StringComparer.Ordinal.Equals(existingEntry.Id, replacementEntry.Id) ||
               AreSameSlot(existingEntry, replacementEntry);
    }

    private int FindMatchingEntryIndex(List<RouteEntry> entries, RouteEntry replacementEntry)
    {
        for (var i = 0; i < entries.Count; i++)
            if (ShouldReplaceWith(entries[i], replacementEntry))
                return i;

        return -1;
    }

    private bool AreSameSlot(RouteEntry left, RouteEntry right)
    {
        if (left.Route is not TRoute leftRoute || right.Route is not TRoute rightRoute) return false;

        string leftSlotId = GetSlotId(leftRoute);
        string rightSlotId = GetSlotId(rightRoute);
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
        string? currentScope = GetScopeKey(currentRootRoute);
        string? targetScope = GetScopeKey(targetRoute);

        return !string.IsNullOrWhiteSpace(currentScope) &&
               !string.IsNullOrWhiteSpace(targetScope) &&
               StringComparer.Ordinal.Equals(currentScope, targetScope);
    }

    private string GetSlotId(TRoute route)
    {
        StackRouteRecipe<TRoute> recipe = GetRecipe(route);

        return recipe.SlotIdFactory?.Invoke(route) ?? recipe.EntryIdFactory(route);
    }

    private string? GetScopeKey(TRoute route)
    {
        return GetRecipe(route).ScopeKeyFactory?.Invoke(route);
    }

    private StackRouteRecipe<TRoute> GetRecipe(TRoute route)
    {
        if (!_recipes.TryGetValue(route.GetType(), out StackRouteRecipe<TRoute>? recipe))
            throw new AppNavigationConfigurationException(
                $"Route '{route.GetType().FullName}' is not registered in this stack navigation model.");

        return recipe;
    }
}

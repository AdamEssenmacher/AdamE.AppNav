using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Testing;

public static class NavigationStateAssert
{
    public static WindowNode ActiveWindow(NavigationState state, string? expectedId = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var window = state.ActiveWindow;
        if (window is null)
        {
            throw new NavigationAssertionException("Expected navigation state to have an active window, but no active window was found.");
        }

        if (expectedId is not null && !StringComparer.Ordinal.Equals(window.Id, expectedId))
        {
            throw new NavigationAssertionException(
                $"Expected active window id '{expectedId}', but found '{window.Id}'.");
        }

        return window;
    }

    public static TNode Root<TNode>(NavigationState state)
        where TNode : NavigationNode
    {
        var window = ActiveWindow(state);
        if (window.Root is not TNode root)
        {
            throw new NavigationAssertionException(
                $"Expected active window root to be '{typeof(TNode).FullName}', but found '{DescribeNode(window.Root)}'.");
        }

        return root;
    }

    public static BranchHostNode SelectedBranchHost(NavigationState state, string expectedBranchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBranchId);

        var branchHost = Root<BranchHostNode>(state);
        if (!StringComparer.Ordinal.Equals(branchHost.SelectedBranchId, expectedBranchId))
        {
            throw new NavigationAssertionException(
                $"Expected selected branch '{expectedBranchId}', but found '{branchHost.SelectedBranchId}'.");
        }

        return branchHost;
    }

    public static TNode SelectedBranch<TNode>(BranchHostNode branchHost, string branchId)
        where TNode : NavigationNode
    {
        ArgumentNullException.ThrowIfNull(branchHost);
        var branch = branchHost.Branches.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, branchId));
        if (branch is null)
        {
            throw new NavigationAssertionException(
                $"Expected branch host '{branchHost.Id}' to contain branch '{branchId}', but branches were: {DescribeBranches(branchHost.Branches)}.");
        }

        if (!StringComparer.Ordinal.Equals(branchHost.SelectedBranchId, branchId))
        {
            throw new NavigationAssertionException(
                $"Expected branch host '{branchHost.Id}' branch '{branchId}' to be selected, but selected branch was '{branchHost.SelectedBranchId}'.");
        }

        if (branch.Content is not TNode content)
        {
            throw new NavigationAssertionException(
                $"Expected branch '{branchId}' content to be '{typeof(TNode).FullName}', but found '{DescribeNode(branch.Content)}'.");
        }

        return content;
    }

    public static TRoute StackTop<TRoute>(StackNode stack)
        where TRoute : AppRoute
    {
        ArgumentNullException.ThrowIfNull(stack);

        if (stack.Top is null)
        {
            throw new NavigationAssertionException(
                $"Expected stack '{stack.Id}' to have top route '{typeof(TRoute).FullName}', but the stack was empty.");
        }

        if (stack.Top.Route is not TRoute route)
        {
            throw new NavigationAssertionException(
                $"Expected stack '{stack.Id}' top route '{typeof(TRoute).FullName}', but found '{stack.Top.Route.GetType().FullName}'.");
        }

        return route;
    }

    public static void StackRouteTypes(StackNode stack, params Type[] expectedRouteTypes)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(expectedRouteTypes);

        var actualRouteTypes = stack.Entries.Select(entry => entry.Route.GetType()).ToArray();
        if (actualRouteTypes.Length != expectedRouteTypes.Length)
        {
            throw new NavigationAssertionException(
                $"Expected stack '{stack.Id}' to contain {expectedRouteTypes.Length} route(s), but found {actualRouteTypes.Length}: {DescribeTypes(actualRouteTypes)}.");
        }

        for (var i = 0; i < expectedRouteTypes.Length; i++)
        {
            if (actualRouteTypes[i] != expectedRouteTypes[i])
            {
                throw new NavigationAssertionException(
                    $"Expected stack '{stack.Id}' route at index {i} to be '{expectedRouteTypes[i].FullName}', but found '{actualRouteTypes[i].FullName}'.");
            }
        }
    }

    public static TRoute ModalRoute<TRoute>(WindowNode window, int index = 0)
        where TRoute : AppRoute
    {
        ArgumentNullException.ThrowIfNull(window);

        if (index < 0 || index >= window.Modals.Count)
        {
            throw new NavigationAssertionException(
                $"Expected window '{window.Id}' to have modal at index {index}, but it has {window.Modals.Count} modal(s).");
        }

        var route = window.Modals[index].RouteEntry.Route;
        if (route is not TRoute typedRoute)
        {
            throw new NavigationAssertionException(
                $"Expected modal route at index {index} to be '{typeof(TRoute).FullName}', but found '{route.GetType().FullName}'.");
        }

        return typedRoute;
    }

    private static string DescribeNode(NavigationNode? node)
    {
        return node is null ? "<null>" : node.GetType().FullName ?? node.GetType().Name;
    }

    private static string DescribeBranches(IReadOnlyList<NavigationBranch> branches)
    {
        return branches.Count == 0
            ? "<none>"
            : string.Join(", ", branches.Select(branch => branch.Id));
    }

    private static string DescribeTypes(IReadOnlyList<Type> types)
    {
        return types.Count == 0
            ? "<none>"
            : string.Join(", ", types.Select(type => type.FullName));
    }
}

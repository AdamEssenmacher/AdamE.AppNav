using AdamE.AppNav.State;

namespace AdamE.AppNav.Navigation;

internal static class PresentedRouteTraversal
{
    internal static AppRoute? FindTopRoute(NavigationNode? node)
    {
        // Walk through container nodes until reaching the route entry that best represents what
        // the user sees. A modal's own route remains the fallback when its nested content is empty.
        return node switch
        {
            StackNode stack => stack.Top?.Route,
            BranchHostNode { SelectedBranch: not null } branchHost => FindTopRoute(branchHost.SelectedBranch.Content),
            // A split reports the column a single-column presentation shows on top.
            SplitNode { CompactColumn: SplitColumn.Secondary, Secondary: { } secondary } split =>
                FindTopRoute(secondary) ?? FindTopRoute(split.Primary),
            SplitNode split => FindTopRoute(split.Primary),
            ModalNode modal => modal.Content is null
                ? modal.RouteEntry.Route
                : FindTopRoute(modal.Content) ?? modal.RouteEntry.Route,
            _ => null
        };
    }
}

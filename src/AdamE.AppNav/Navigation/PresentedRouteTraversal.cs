using AdamE.AppNav.State;

namespace AdamE.AppNav.Navigation;

internal static class PresentedRouteTraversal
{
    internal static AppRoute? FindTopRoute(NavigationNode? node, SplitLayout splitLayout = SplitLayout.Collapsed)
    {
        // Walk through container nodes until reaching the route entry that best represents what
        // the user sees. A modal's own route remains the fallback when its nested content is empty.
        return node switch
        {
            StackNode stack => stack.Top?.Route,
            BranchHostNode { SelectedBranch: not null } branchHost =>
                FindTopRoute(branchHost.SelectedBranch.Content, splitLayout),
            // Collapsed, a split reports what it shows on top. Side by side, the secondary column is the focus
            // whenever it has content.
            SplitNode { Secondary: { } secondary } split
                when splitLayout == SplitLayout.SideBySide || split.CompactColumn == SplitColumn.Secondary =>
                FindTopRoute(secondary, splitLayout) ?? FindTopRoute(split.Primary, splitLayout),
            SplitNode split => FindTopRoute(split.Primary, splitLayout),
            ModalNode modal => modal.Content is null
                ? modal.RouteEntry.Route
                : FindTopRoute(modal.Content, splitLayout) ?? modal.RouteEntry.Route,
            _ => null
        };
    }
}

using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Navigation;

internal static class PresentedRouteResolver
{
    public static AppRoute? FindPresentedRoute(WindowNode? window)
    {
        if (window is null)
            return null;

        // Modals are presented above the root navigation surface. The most recent modal is the
        // visible route unless it hosts nested navigation content with its own visible route.
        if (window.Modals.Count > 0)
        {
            ModalNode modal = window.Modals[^1];
            return modal.Content is null
                ? modal.RouteEntry.Route
                : FindTopRoute(modal.Content) ?? modal.RouteEntry.Route;
        }

        return window.Root is null ? null : FindTopRoute(window.Root);
    }

    private static AppRoute? FindTopRoute(NavigationNode? node)
    {
        // Walk through container nodes until reaching the route entry that best represents what
        // the user sees. A modal's own route remains the fallback when its nested content is empty.
        return node switch
        {
            StackNode stack => stack.Top?.Route,
            BranchHostNode { SelectedBranch: not null } branchHost => FindTopRoute(branchHost.SelectedBranch.Content),
            ModalNode modal => modal.Content is null
                ? modal.RouteEntry.Route
                : FindTopRoute(modal.Content) ?? modal.RouteEntry.Route,
            _ => null
        };
    }
}

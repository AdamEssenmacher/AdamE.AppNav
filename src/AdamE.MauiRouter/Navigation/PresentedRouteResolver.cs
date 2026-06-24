using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Navigation;

internal static class PresentedRouteResolver
{
    public static AppRoute? FindPresentedRoute(WindowNode? window)
    {
        if (window is null)
        {
            return null;
        }

        if (window.Modals.Count > 0)
        {
            var modal = window.Modals[^1];
            return modal.Content is null
                ? modal.RouteEntry.Route
                : FindTopRoute(modal.Content) ?? modal.RouteEntry.Route;
        }

        return window.Root is null ? null : FindTopRoute(window.Root);
    }

    private static AppRoute? FindTopRoute(NavigationNode? node)
    {
        return node switch
        {
            StackNode stack => stack.Top?.Route,
            TabsNode tabs when tabs.SelectedBranch is not null => FindTopRoute(tabs.SelectedBranch.Content),
            FlyoutNode flyout when flyout.SelectedBranch is not null => FindTopRoute(flyout.SelectedBranch.Content),
            ModalNode modal => modal.Content is null
                ? modal.RouteEntry.Route
                : FindTopRoute(modal.Content) ?? modal.RouteEntry.Route,
            _ => null
        };
    }
}

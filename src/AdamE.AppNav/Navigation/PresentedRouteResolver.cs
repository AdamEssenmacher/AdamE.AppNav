using AdamE.AppNav.State;

namespace AdamE.AppNav.Navigation;

internal static class PresentedRouteResolver
{
    public static AppRoute? FindPresentedRoute(WindowNode? window)
    {
        if (window is null)
            return null;

        // Modals are presented above the root navigation surface. The most recent modal is the
        // visible route unless it hosts nested navigation content with its own visible route.
        if (window.Modals.Count <= 0)
            return window.Root is null ? null : PresentedRouteTraversal.FindTopRoute(window.Root);

        ModalNode modal = window.Modals[^1];
        return modal.Content is null
            ? modal.RouteEntry.Route
            : PresentedRouteTraversal.FindTopRoute(modal.Content) ?? modal.RouteEntry.Route;
    }
}

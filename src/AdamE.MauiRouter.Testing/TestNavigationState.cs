using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Testing;

public static class TestNavigationState
{
    public static NavigationState Empty => NavigationState.Empty;

    public static RouteEntry Entry(
        string id,
        AppRoute route,
        NavigationTransition? transition = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        return new RouteEntry(id, route, transition, metadata);
    }

    public static StackNode Stack(string id, params RouteEntry[] entries)
    {
        return new StackNode(id, entries.ToArray());
    }

    public static NavigationBranch Branch(string id, string title, NavigationNode content)
    {
        return new NavigationBranch(id, title, content);
    }

    public static TabsNode Tabs(
        string id,
        string selectedTabId,
        string? defaultTabId,
        params NavigationBranch[] branches)
    {
        return new TabsNode(id, branches.ToArray(), selectedTabId, defaultTabId);
    }

    public static TabsNode Tabs(
        string id,
        string selectedTabId,
        params NavigationBranch[] branches)
    {
        return new TabsNode(id, branches.ToArray(), selectedTabId);
    }

    public static FlyoutNode Flyout(
        string id,
        string selectedItemId,
        string? defaultItemId,
        params NavigationBranch[] branches)
    {
        return new FlyoutNode(id, branches.ToArray(), selectedItemId, defaultItemId);
    }

    public static FlyoutNode Flyout(
        string id,
        string selectedItemId,
        params NavigationBranch[] branches)
    {
        return new FlyoutNode(id, branches.ToArray(), selectedItemId);
    }

    public static ModalNode Modal(
        string id,
        RouteEntry routeEntry,
        NavigationNode? content = null)
    {
        return new ModalNode(id, routeEntry, content);
    }

    public static WindowNode Window(
        string id,
        NavigationNode? root = null,
        IReadOnlyList<ModalNode>? modals = null)
    {
        return new WindowNode(id, root, modals?.ToArray());
    }

    public static NavigationState State(string activeWindowId, params WindowNode[] windows)
    {
        return new NavigationState(windows.ToArray(), activeWindowId);
    }
}

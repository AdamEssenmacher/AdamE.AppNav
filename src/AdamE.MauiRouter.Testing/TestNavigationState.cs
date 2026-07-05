using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Testing;

public static class TestNavigationState
{
    public static NavigationState Empty => NavigationState.Empty;

    public static RouteEntry Entry(
        string id,
        AppRoute route,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        return new RouteEntry(id, route, metadata);
    }

    public static StackNode Stack(string id, params RouteEntry[] entries)
    {
        return new StackNode(id, entries.ToArray());
    }

    public static NavigationBranch Branch(string id, string title, NavigationNode content)
    {
        return new NavigationBranch(id, title, content);
    }

    public static BranchHostNode BranchHost(
        string id,
        string selectedBranchId,
        string? defaultBranchId,
        params NavigationBranch[] branches)
    {
        return new BranchHostNode(id, branches.ToArray(), selectedBranchId, defaultBranchId);
    }

    public static BranchHostNode BranchHost(
        string id,
        string selectedBranchId,
        params NavigationBranch[] branches)
    {
        return new BranchHostNode(id, branches.ToArray(), selectedBranchId);
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

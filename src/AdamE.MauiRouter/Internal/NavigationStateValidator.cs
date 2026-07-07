using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Internal;

internal static class NavigationStateValidator
{
    public static void ValidatePlan(NavigationPlan? plan, string source)
    {
        if (plan is null)
            throw new InvalidOperationException($"{source} produced a null navigation plan.");

        ValidateState(plan.TargetState, $"{source} target state");
    }

    public static void ValidateState(NavigationState? state, string source)
    {
        if (state is null)
            throw new InvalidOperationException($"{source} cannot be null.");

        var windowIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (WindowNode window in RequireListItems(state.Windows, $"{source}.Windows"))
        {
            RequireId(window.Id, $"{source}.Windows[].Id");
            if (!windowIds.Add(window.Id))
                throw Invalid($"{source} contains duplicate window id '{window.Id}'.");

            ValidateWindow(window, $"{source}.Window('{window.Id}')");
        }

        if (state.ActiveWindowId is not null)
            RequireId(state.ActiveWindowId, $"{source}.ActiveWindowId");
    }

    private static void ValidateWindow(WindowNode window, string path)
    {
        if (window.Root is not null)
            ValidateNode(window.Root, $"{path}.Root");

        var modalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModalNode modal in RequireListItems(window.Modals, $"{path}.Modals"))
        {
            RequireId(modal.Id, $"{path}.Modals[].Id");
            if (!modalIds.Add(modal.Id))
                throw Invalid($"{path} contains duplicate modal id '{modal.Id}'.");

            ValidateModal(modal, $"{path}.Modal('{modal.Id}')");
        }
    }

    private static void ValidateNode(NavigationNode? node, string path)
    {
        if (node is null)
            throw Invalid($"{path} cannot be null.");

        RequireId(node.Id, $"{path}.Id");

        switch (node)
        {
            case StackNode stack:
                ValidateStack(stack, path);
                break;
            case BranchHostNode branchHost:
                ValidateBranchHost(branchHost, path);
                break;
            case ModalNode modal:
                ValidateModal(modal, path);
                break;
        }
    }

    private static void ValidateStack(StackNode stack, string path)
    {
        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (RouteEntry entry in RequireListItems(stack.Entries, $"{path}.Entries"))
        {
            ValidateRouteEntry(entry, $"{path}.Entry('{entry.Id}')");
            if (!entryIds.Add(entry.Id))
                throw Invalid($"{path} contains duplicate route-entry id '{entry.Id}'.");
        }
    }

    private static void ValidateBranchHost(BranchHostNode branchHost, string path)
    {
        IReadOnlyList<NavigationBranch> branches = RequireListItems(branchHost.Branches, $"{path}.Branches");
        if (branches.Count == 0)
            throw Invalid($"{path} must contain at least one branch.");

        var branchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (NavigationBranch branch in branches)
        {
            RequireId(branch.Id, $"{path}.Branches[].Id");
            if (!branchIds.Add(branch.Id))
                throw Invalid($"{path} contains duplicate branch id '{branch.Id}'.");

            if (string.IsNullOrWhiteSpace(branch.Title))
                throw Invalid($"{path}.Branch('{branch.Id}').Title cannot be null, empty, or whitespace.");

            ValidateNode(branch.Content, $"{path}.Branch('{branch.Id}').Content");
        }

        RequireId(branchHost.SelectedBranchId, $"{path}.SelectedBranchId");
        if (!branchIds.Contains(branchHost.SelectedBranchId))
            throw Invalid(
                $"{path} selected branch id '{branchHost.SelectedBranchId}' does not reference an existing branch.");

        if (branchHost.DefaultBranchId is null)
            return;

        RequireId(branchHost.DefaultBranchId, $"{path}.DefaultBranchId");
        if (!branchIds.Contains(branchHost.DefaultBranchId))
            throw Invalid(
                $"{path} default branch id '{branchHost.DefaultBranchId}' does not reference an existing branch.");
    }

    private static void ValidateModal(ModalNode modal, string path)
    {
        ValidateRouteEntry(modal.RouteEntry, $"{path}.RouteEntry");
        if (modal.Content is not null)
            ValidateNode(modal.Content, $"{path}.Content");
    }

    private static void ValidateRouteEntry(RouteEntry? entry, string path)
    {
        if (entry is null)
            throw Invalid($"{path} cannot be null.");

        RequireId(entry.Id, $"{path}.Id");
        if (entry.Route is null)
            throw Invalid($"{path}.Route cannot be null.");
    }

    private static IReadOnlyList<T> RequireListItems<T>(IReadOnlyList<T>? items, string path)
        where T : class
    {
        if (items is null)
            throw Invalid($"{path} cannot be null.");

        for (var i = 0; i < items.Count; i++)
            if (items[i] is null)
                throw Invalid($"{path}[{i}] cannot be null.");

        return items;
    }

    private static void RequireId(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid($"{path} cannot be null, empty, or whitespace.");
    }

    private static InvalidOperationException Invalid(string message)
    {
        return new InvalidOperationException($"Invalid navigation state: {message}");
    }
}

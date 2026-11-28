using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Internal;

internal static class NavigationStateValidator
{
    public static void ValidatePlan(NavigationPlan? plan, NavigationValidationSource source)
    {
        if (plan is null)
            throw new AppNavigationConfigurationException($"{source} produced a null navigation plan.");

        if (plan.TargetState is null)
            throw new AppNavigationConfigurationException($"{source} target state cannot be null.");

        if (FindStateError(plan.TargetState) is { } error)
            throw Invalid($"{source} target state{error}");
    }

    public static void ValidateState(NavigationState? state, string source)
    {
        if (state is null)
            throw new AppNavigationConfigurationException($"{source} cannot be null.");

        if (FindStateError(state) is { } error)
            throw Invalid($"{source}{error}");
    }

    // Errors are relative to the current node. Each caller prepends its path segment only when a child fails.
    private static string? FindStateError(NavigationState state)
    {
        IReadOnlyList<WindowNode> windows = state.Windows;
        if (FindListError(windows) is { } listError)
            return $".Windows{listError}";

        var windowIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (WindowNode window in windows)
        {
            if (string.IsNullOrWhiteSpace(window.Id))
                return ".Windows[].Id" + MissingId;
            if (!windowIds.Add(window.Id))
                return $" contains duplicate window id '{window.Id}'.";

            if (FindWindowError(window) is { } error)
                return $".Window('{window.Id}'){error}";
        }

        if (state.ActiveWindowId is not null)
        {
            if (string.IsNullOrWhiteSpace(state.ActiveWindowId))
                return ".ActiveWindowId" + MissingId;
            if (!windowIds.Contains(state.ActiveWindowId))
                return $".ActiveWindowId '{state.ActiveWindowId}' does not reference an existing window.";
        }

        return null;
    }

    private static string? FindWindowError(WindowNode window)
    {
        if (window.Root is not null && FindNodeError(window.Root, SplitPlacement.Allowed) is { } rootError)
            return $".Root{rootError}";

        IReadOnlyList<ModalNode> modals = window.Modals;
        if (FindListError(modals) is { } listError)
            return $".Modals{listError}";

        var modalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModalNode modal in modals)
        {
            if (string.IsNullOrWhiteSpace(modal.Id))
                return ".Modals[].Id" + MissingId;
            if (!modalIds.Add(modal.Id))
                return $" contains duplicate modal id '{modal.Id}'.";

            if (FindModalError(modal) is { } error)
                return $".Modal('{modal.Id}'){error}";
        }

        return null;
    }

    private static string? FindNodeError(NavigationNode? node, SplitPlacement splitPlacement)
    {
        if (node is null)
            return " cannot be null.";

        if (string.IsNullOrWhiteSpace(node.Id))
            return ".Id" + MissingId;

        return node switch
        {
            StackNode stack => FindStackError(stack),
            BranchHostNode branchHost => FindBranchHostError(branchHost, splitPlacement),
            SplitNode split => FindSplitError(split, splitPlacement),
            ModalNode modal => FindModalError(modal),
            _ => $" has unsupported node type '{node.GetType().FullName}'."
        };
    }

    private static string? FindStackError(StackNode stack)
    {
        IReadOnlyList<RouteEntry> entries = stack.Entries;
        if (FindListError(entries) is { } listError)
            return $".Entries{listError}";

        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (RouteEntry entry in entries)
        {
            if (FindRouteEntryError(entry) is { } error)
                return $".Entry('{entry.Id}'){error}";
            if (!entryIds.Add(entry.Id))
                return $" contains duplicate route-entry id '{entry.Id}'.";
        }

        return null;
    }

    private static string? FindBranchHostError(BranchHostNode branchHost, SplitPlacement splitPlacement)
    {
        IReadOnlyList<NavigationBranch> branches = branchHost.Branches;
        if (FindListError(branches) is { } listError)
            return $".Branches{listError}";
        if (branches.Count == 0)
            return " must contain at least one branch.";

        var branchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (NavigationBranch branch in branches)
        {
            if (string.IsNullOrWhiteSpace(branch.Id))
                return ".Branches[].Id" + MissingId;
            if (!branchIds.Add(branch.Id))
                return $" contains duplicate branch id '{branch.Id}'.";

            if (string.IsNullOrWhiteSpace(branch.Title))
                return $".Branch('{branch.Id}').Title{MissingId}";

            if (FindNodeError(branch.Content, splitPlacement) is { } error)
                return $".Branch('{branch.Id}').Content{error}";
        }

        if (string.IsNullOrWhiteSpace(branchHost.SelectedBranchId))
            return ".SelectedBranchId" + MissingId;
        if (!branchIds.Contains(branchHost.SelectedBranchId))
            return $" selected branch id '{branchHost.SelectedBranchId}' does not reference an existing branch.";

        if (branchHost.DefaultBranchId is not null)
        {
            if (string.IsNullOrWhiteSpace(branchHost.DefaultBranchId))
                return ".DefaultBranchId" + MissingId;
            if (!branchIds.Contains(branchHost.DefaultBranchId))
                return $" default branch id '{branchHost.DefaultBranchId}' does not reference an existing branch.";
        }

        return null;
    }

    // A split cannot be nested, at any depth, inside a modal or another split: what collapsing means there is not
    // defined, so such placements are rejected rather than left to each presenter.
    private static string? FindSplitError(SplitNode split, SplitPlacement splitPlacement)
    {
        switch (splitPlacement)
        {
            case SplitPlacement.InsideModal:
                return " is a split inside a modal; a split cannot be nested, at any depth, inside a modal or another split.";
            case SplitPlacement.InsideSplit:
                return " is a split inside another split; a split cannot be nested, at any depth, inside a modal or another split.";
        }

        if (!Enum.IsDefined(split.CompactColumn))
            return $".CompactColumn has unknown value '{split.CompactColumn}'.";

        if (FindNodeError(split.Primary, SplitPlacement.InsideSplit) is { } primaryError)
            return $".Primary{primaryError}";

        if (split.Secondary is not null)
        {
            if (FindNodeError(split.Secondary, SplitPlacement.InsideSplit) is { } secondaryError)
                return $".Secondary{secondaryError}";
        }
        else if (split.CompactColumn == SplitColumn.Secondary)
        {
            return " shows its secondary column but has no secondary content. Remove a selection with " +
                   "SplitNode.ClearSecondary(); 'with { Secondary = null }' keeps CompactColumn as it was.";
        }

        return null;
    }

    private static string? FindModalError(ModalNode modal)
    {
        if (FindRouteEntryError(modal.RouteEntry) is { } entryError)
            return $".RouteEntry{entryError}";
        if (modal.Content is not null && FindNodeError(modal.Content, SplitPlacement.InsideModal) is { } contentError)
            return $".Content{contentError}";

        return null;
    }

    private static string? FindRouteEntryError(RouteEntry? entry)
    {
        if (entry is null)
            return " cannot be null.";
        if (string.IsNullOrWhiteSpace(entry.Id))
            return ".Id" + MissingId;
        if (entry.Route is null)
            return ".Route cannot be null.";

        return null;
    }

    private static string? FindListError<T>(IReadOnlyList<T>? items) where T : class
    {
        if (items is null)
            return " cannot be null.";

        for (var i = 0; i < items.Count; i++)
            if (items[i] is null)
                return $"[{i}] cannot be null.";

        return null;
    }

    private const string MissingId = " cannot be null, empty, or whitespace.";

    private static AppNavigationConfigurationException Invalid(string message)
    {
        return new AppNavigationConfigurationException($"Invalid navigation state: {message}");
    }

    private enum SplitPlacement
    {
        Allowed,
        InsideModal,
        InsideSplit
    }
}

using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Oahu;

/// <param name="Branch">The selected branch, or a split's primary column.</param>
/// <param name="Entries">The selected branch's stack, or a split's primary column.</param>
/// <param name="ModalId">The presented modal, if any.</param>
/// <param name="ModalEntries">The modal's stack.</param>
/// <param name="SecondaryEntries">A split's secondary column; empty when it has none or the window has no split.</param>
/// <param name="CompactColumn">The column a collapsed split shows on top; <see langword="null"/> without a split.</param>
internal sealed record SurfaceObservation(string Branch, IReadOnlyList<string> Entries,
    string? ModalId, IReadOnlyList<string> ModalEntries, IReadOnlyList<string> SecondaryEntries,
    SplitColumn? CompactColumn)
{
    public SurfaceObservation(string Branch, IReadOnlyList<string> Entries, string? ModalId,
        IReadOnlyList<string> ModalEntries) : this(Branch, Entries, ModalId, ModalEntries, [], null)
    {
    }

    public bool Matches(OahuNavigationTarget target) =>
        Branch == target.Branch && ModalId == target.ModalId &&
        (target.Branches.TryGetValue(Branch, out var stack)
            ? OahuNavigationTarget.Matches(stack, Entries) : target.Branches.Count == 0 && Entries.Count == 0) &&
        OahuNavigationTarget.Matches(target.ModalEntries, ModalEntries) &&
        (target.IsSplit
            ? CompactColumn == target.CompactColumn && OahuNavigationTarget.Matches(target.SecondaryEntries, SecondaryEntries)
            : CompactColumn is null && SecondaryEntries.Count == 0);

    // Accept only a single native gesture relative to the exact state rendered by this surface.
    public NavigationReconciliation? Reconcile(NavigationState state, OahuNavigationTarget target)
    {
        if (state.ActiveWindow is not { } window ||
            !target.Branches.TryGetValue(Branch, out var stack) || Entries.Count == 0 ||
            !OahuNavigationTarget.IsPrefix(stack, Entries))
            return null;
        if (target.IsSplit)
            return ReconcileSplit(state, window, target, stack);
        if (CompactColumn is not null || SecondaryEntries.Count != 0) return null;
        if (Branch != target.Branch)
        {
            if (target.ModalId != null || ModalId != null || ModalEntries.Count != 0 ||
                Entries.Count != stack.Count || window.Root is not BranchHostNode host)
                return null;
            return Changed(state, window with { Root = host with { SelectedBranchId = Branch } },
                NavigationReconciliationSource.BranchChanged, stack[^1]);
        }
        if (target.ModalId != null)
            return Entries.Count == stack.Count ? ReconcileModal(state, window, target, stack[^1]) : null;
        if (ModalId != null || ModalEntries.Count != 0 || Entries.Count >= stack.Count) return null;
        var prefix = stack.Take(Entries.Count).ToArray();
        NavigationNode root = window.Root switch
        {
            StackNode simple => simple with { Entries = prefix },
            BranchHostNode host when host.SelectedBranch is { Content: StackNode selected } branch =>
                host.ReplaceBranch(branch with { Content = selected with { Entries = prefix } }),
            _ => throw new InvalidOperationException("Accepted surface has an invalid root.")
        };
        return Changed(state, window with { Root = root }, NavigationReconciliationSource.HostBack, prefix[^1]);
    }

    // A dismissal, or a pop inside the modal stack, over an unchanged root.
    private NavigationReconciliation? ReconcileModal(NavigationState state, WindowNode window,
        OahuNavigationTarget target, RouteEntry uncovered)
    {
        if (ModalId == null && ModalEntries.Count == 0)
            return Changed(state, window with { Modals = [] }, NavigationReconciliationSource.ModalDismissed, uncovered);
        if (ModalId != target.ModalId || ModalEntries.Count == 0 ||
            ModalEntries.Count >= target.ModalEntries.Count ||
            !OahuNavigationTarget.IsPrefix(target.ModalEntries, ModalEntries) ||
            window.Modals[0].Content is not StackNode content) return null;
        var shortened = content with { Entries = content.Entries.Take(ModalEntries.Count).ToArray() };
        return Changed(state, window with { Modals = [window.Modals[0] with { Content = shortened }] },
            NavigationReconciliationSource.HostBack, shortened.Top ?? uncovered);
    }

    // Native changes to a split: Back inside either column, and closing the secondary column from its root in a
    // collapsed window. The observed columns are committed as they are, so a pop in the primary column keeps the
    // secondary column when the window shows both.
    private NavigationReconciliation? ReconcileSplit(NavigationState state, WindowNode window,
        OahuNavigationTarget target, IReadOnlyList<RouteEntry> primary)
    {
        if (Branch != target.Branch || CompactColumn is not { } compact ||
            window.Root is not SplitNode { Primary: StackNode primaryStack } split)
            return null;
        var secondaryClosed = target.SecondaryEntries.Count != 0 && SecondaryEntries.Count == 0;
        if (SecondaryEntries.Count > target.SecondaryEntries.Count ||
            !OahuNavigationTarget.IsPrefix(target.SecondaryEntries, SecondaryEntries) ||
            (secondaryClosed ? compact != SplitColumn.Primary : compact != target.CompactColumn))
            return null;
        var columnsUnchanged = Entries.Count == primary.Count && SecondaryEntries.Count == target.SecondaryEntries.Count;
        if (target.ModalId != null)
        {
            if (!columnsUnchanged) return null;
            var uncovered = target.VisibleEntries.Count != 0 ? target.VisibleEntries[^1] : primary[^1];
            return ReconcileModal(state, window, target, uncovered);
        }
        if (ModalId != null || ModalEntries.Count != 0 || columnsUnchanged) return null;

        var primaryPrefix = primary.Take(Entries.Count).ToArray();
        var next = split with { Primary = primaryStack with { Entries = primaryPrefix } };
        RouteEntry top;
        if (secondaryClosed)
        {
            next = next.ClearSecondary();
            top = primaryPrefix[^1];
        }
        else if (split.Secondary is StackNode secondaryStack && SecondaryEntries.Count != 0)
        {
            var secondaryPrefix = target.SecondaryEntries.Take(SecondaryEntries.Count).ToArray();
            next = next with { Secondary = secondaryStack with { Entries = secondaryPrefix } };
            top = compact == SplitColumn.Secondary ? secondaryPrefix[^1] : primaryPrefix[^1];
        }
        else
        {
            top = primaryPrefix[^1];
        }
        return Changed(state, window with { Root = next }, NavigationReconciliationSource.HostBack, top);
    }

    private static NavigationReconciliation Changed(NavigationState state, WindowNode next,
        NavigationReconciliationSource source, RouteEntry top) =>
        new(state, state.ReplaceWindow(next), source, top.Route, "Observed on the attached native surface.");
}

using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Oahu;

internal sealed record SurfaceObservation(string Branch, IReadOnlyList<string> Entries,
    string? ModalId, IReadOnlyList<string> ModalEntries)
{
    public bool Matches(OahuNavigationTarget target) =>
        Branch == target.Branch && ModalId == target.ModalId &&
        (target.Branches.TryGetValue(Branch, out var stack)
            ? OahuNavigationTarget.Matches(stack, Entries) : target.Branches.Count == 0 && Entries.Count == 0) &&
        OahuNavigationTarget.Matches(target.ModalEntries, ModalEntries);

    // Accept only a single native gesture relative to the exact state rendered by this surface.
    public NavigationReconciliation? Reconcile(NavigationState state, OahuNavigationTarget target)
    {
        if (state.ActiveWindow is not { } window ||
            !target.Branches.TryGetValue(Branch, out var stack) || Entries.Count == 0 ||
            !OahuNavigationTarget.IsPrefix(stack, Entries))
            return null;
        if (Branch != target.Branch)
        {
            if (target.ModalId != null || ModalId != null || ModalEntries.Count != 0 ||
                Entries.Count != stack.Count || window.Root is not BranchHostNode host)
                return null;
            return Changed(window with { Root = host with { SelectedBranchId = Branch } },
                NavigationReconciliationSource.BranchChanged, stack[^1]);
        }
        if (target.ModalId != null)
        {
            if (Entries.Count != stack.Count) return null;
            if (ModalId == null && ModalEntries.Count == 0)
                return Changed(window with { Modals = [] }, NavigationReconciliationSource.ModalDismissed, stack[^1]);
            if (ModalId != target.ModalId || ModalEntries.Count == 0 ||
                ModalEntries.Count >= target.ModalEntries.Count ||
                !OahuNavigationTarget.IsPrefix(target.ModalEntries, ModalEntries) ||
                window.Modals[0].Content is not StackNode content) return null;
            var shortened = content with { Entries = content.Entries.Take(ModalEntries.Count).ToArray() };
            return Changed(window with { Modals = [window.Modals[0] with { Content = shortened }] },
                NavigationReconciliationSource.HostBack, shortened.Top!);
        }
        if (ModalId != null || ModalEntries.Count != 0 || Entries.Count >= stack.Count) return null;
        var prefix = stack.Take(Entries.Count).ToArray();
        NavigationNode root = window.Root switch
        {
            StackNode simple => simple with { Entries = prefix },
            BranchHostNode host when host.SelectedBranch is { Content: StackNode selected } branch =>
                host.ReplaceBranch(branch with { Content = selected with { Entries = prefix } }),
            _ => throw new InvalidOperationException("Accepted surface has an invalid root.")
        };
        return Changed(window with { Root = root }, NavigationReconciliationSource.HostBack, prefix[^1]);

        NavigationReconciliation Changed(WindowNode next, NavigationReconciliationSource source, RouteEntry top) =>
            new(state, state.ReplaceWindow(next), source, top.Route, "Observed on the attached native surface.");
    }
}

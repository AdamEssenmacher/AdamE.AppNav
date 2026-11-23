using System.Collections.ObjectModel;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Oahu;

/// <summary>A projection of one window, a stack, branch host or split root, and an optional modal stack.</summary>
/// <remarks>Other shapes are refused with <see cref="NotSupportedException"/> before anything is staged. A
/// <see cref="SplitNode"/> is presented only by a surface that reports <see cref="IOahuNavigationSurface.PresentsSplits"/>,
/// only as the window root, and only with a stack in each column.</remarks>
public sealed record OahuNavigationTarget
{
    internal OahuNavigationTarget(NavigationState state, long revision, bool presentsSplits = false)
    {
        Revision = revision;
        var branches = new Dictionary<string, IReadOnlyList<RouteEntry>>(StringComparer.Ordinal);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        Branches = new ReadOnlyDictionary<string, IReadOnlyList<RouteEntry>>(branches);
        BranchTitles = new ReadOnlyDictionary<string, string>(titles);
        if (state.Windows.Count == 0) return;
        if (state.Windows.Count != 1 || state.ActiveWindow is not { } window)
            throw new NotSupportedException("An OAHU surface presents one active window.");
        WindowId = window.Id;
        RefuseUnsupportedSplits(window, presentsSplits);
        switch (window.Root)
        {
            case StackNode stack:
                Branch = stack.Id;
                branches.Add(Branch, StackEntries(stack));
                titles.Add(Branch, stack.Id);
                break;
            case BranchHostNode host when host.SelectedBranch is not null:
                IsBranchHost = true;
                Branch = host.SelectedBranchId;
                foreach (var branch in host.Branches)
                {
                    if (branch.Content is not StackNode child)
                        throw new NotSupportedException("Every branch must contain a stack.");
                    branches.Add(branch.Id, StackEntries(child));
                    titles.Add(branch.Id, branch.Title);
                }
                break;
            case SplitNode split:
                if (split.Primary is not StackNode primary)
                    throw new NotSupportedException(
                        $"The primary column of split '{split.Id}' must be a stack; an OAHU surface does not present a {split.Primary.GetType().Name} in a split column.");
                Branch = primary.Id;
                branches.Add(Branch, StackEntries(primary));
                titles.Add(Branch, primary.Id);
                SplitId = split.Id;
                CompactColumn = split.CompactColumn;
                switch (split.Secondary)
                {
                    case null:
                        break;
                    case StackNode secondary:
                        SecondaryId = secondary.Id;
                        SecondaryEntries = StackEntries(secondary);
                        break;
                    default:
                        throw new NotSupportedException(
                            $"The secondary column of split '{split.Id}' must be a stack; an OAHU surface does not present a {split.Secondary.GetType().Name} in a split column.");
                }
                break;
            default:
                throw new NotSupportedException("A window must have a stack, a branch host or a split as its root.");
        }
        RootId = window.Root.Id;
        if (window.Modals.Count > 1)
            throw new NotSupportedException("Only one modal can be presented at a time.");
        if (window.Modals.FirstOrDefault() is { } modal)
        {
            ModalId = modal.Id;
            ModalOwner = modal.RouteEntry;
            ModalEntries = modal.Content switch
            {
                null => [modal.RouteEntry],
                StackNode content => StackEntries(content),
                _ => throw new NotSupportedException("Modal content must be a stack.")
            };
        }

        // Mounts and entry leases are keyed by occurrence across the entire window.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var all = branches.Values.SelectMany(stack => stack).Concat(SecondaryEntries).Concat(ModalEntries);
        if (window.Modals.FirstOrDefault() is { Content: not null } owner)
            all = all.Append(owner.RouteEntry);
        foreach (var entry in all)
            if (!ids.Add(entry.Id))
                throw new NotSupportedException($"Duplicate route entry occurrence '{entry.Id}'.");
    }

    // Refuse every split this surface cannot present before staging anything for it.
    private static void RefuseUnsupportedSplits(WindowNode window, bool presentsSplits)
    {
        if (!presentsSplits)
        {
            SplitNode? split = FindSplit(window.Root);
            for (var i = 0; split is null && i < window.Modals.Count; ++i)
                split = FindSplit(window.Modals[i]);
            if (split is not null)
                throw new NotSupportedException(
                    $"This OAHU surface does not present split nodes; found split '{split.Id}'. Attach a surface that presents splits, such as the SwiftUI host.");
            return;
        }
        switch (window.Root)
        {
            case BranchHostNode host:
                for (var i = 0; i < host.Branches.Count; ++i)
                    if (FindSplit(host.Branches[i].Content) is { } nested)
                        throw new NotSupportedException(
                            $"Split '{nested.Id}' is inside branch '{host.Branches[i].Id}'. An OAHU surface presents a split only as the window root.");
                break;
            case SplitNode split:
                foreach (var column in new[] { split.Primary, split.Secondary })
                    if (column is BranchHostNode columnHost)
                        throw new NotSupportedException(
                            $"Split '{split.Id}' has branch host '{columnHost.Id}' in a column. An OAHU surface presents only stacks in split columns.");
                break;
        }
        for (var i = 0; i < window.Modals.Count; ++i)
            if (FindSplit(window.Modals[i]) is { } modalSplit)
                throw new NotSupportedException(
                    $"Split '{modalSplit.Id}' is inside modal '{window.Modals[i].Id}'. An OAHU surface presents a split only as the window root.");
    }

    private static SplitNode? FindSplit(NavigationNode? node)
    {
        switch (node)
        {
            case SplitNode split:
                return split;
            case BranchHostNode host:
                for (var i = 0; i < host.Branches.Count; ++i)
                    if (FindSplit(host.Branches[i].Content) is { } nested)
                        return nested;
                return null;
            case ModalNode modal:
                return FindSplit(modal.Content);
            default:
                return null;
        }
    }

    private static IReadOnlyList<RouteEntry> StackEntries(StackNode stack) => stack.Entries.Count != 0
        ? Array.AsReadOnly(stack.Entries.ToArray()) : throw new NotSupportedException($"Stack '{stack.Id}' needs a root entry.");

    internal string WindowId { get; } = "";
    internal string RootId { get; } = "";
    private IReadOnlyList<RouteEntry> PrimaryEntries => Branches.TryGetValue(Branch, out var entries) ? entries : [];

    /// <summary>The stack a single-column presentation shows on top. A split shows its primary column, with the
    /// secondary column stacked above it when <see cref="CompactColumn"/> is <see cref="SplitColumn.Secondary"/>.</summary>
    internal IReadOnlyList<RouteEntry> VisibleEntries =>
        ModalId is not null ? ModalEntries
        : IsSplit && CompactColumn == SplitColumn.Secondary ? [.. PrimaryEntries, .. SecondaryEntries]
        : PrimaryEntries;

    /// <summary>Whether a source element on this entry is on screen in some layout: the visible top, or the top of a
    /// split's primary column, which shows beside the secondary column on a wide window.</summary>
    internal bool ShowsOnTop(string entryId) =>
        VisibleEntries.LastOrDefault()?.Id == entryId ||
        (IsSplit && ModalId is null && PrimaryEntries.LastOrDefault()?.Id == entryId);

    internal bool IsSplit => SplitId is not null;
    public OahuPresentationTransition Transition { get; internal init; } = OahuPresentationTransition.Initial;

    public long Revision { get; }
    public bool IsBranchHost { get; }
    public string Branch { get; } = "";
    public IReadOnlyDictionary<string, IReadOnlyList<RouteEntry>> Branches { get; }
    public IReadOnlyDictionary<string, string> BranchTitles { get; }
    public string? ModalId { get; }
    /// <summary>The split at the window root, or <see langword="null"/> when the root is not a split. The primary column
    /// is <see cref="Branch"/>.</summary>
    public string? SplitId { get; }
    /// <summary>The secondary column's stack, or <see langword="null"/> when the split has no secondary column.</summary>
    public string? SecondaryId { get; }
    public IReadOnlyList<RouteEntry> SecondaryEntries { get; } = [];
    /// <summary>The column a collapsed split shows on top. Meaningful only when <see cref="SplitId"/> is set.</summary>
    public SplitColumn CompactColumn { get; }
    public RouteEntry? ModalOwner { get; }
    public IReadOnlyList<RouteEntry> ModalEntries { get; } = [];

    internal static bool Matches(IReadOnlyList<RouteEntry> entries, IReadOnlyList<string> observed) =>
        entries.Count == observed.Count && IsPrefix(entries, observed);

    internal static bool IsPrefix(IReadOnlyList<RouteEntry> entries, IReadOnlyList<string> observed)
    {
        if (observed.Count > entries.Count) return false;
        for (var i = 0; i < observed.Count; ++i)
            if (entries[i].Id != observed[i]) return false;
        return true;
    }
}

/// <summary>One native host attachment. Both methods serialize work on its UI thread and check the token there before mutating native state.</summary>
public interface IOahuNavigationSurface
{
    /// <summary>Whether this surface presents a <see cref="SplitNode"/> at the window root. The presenter refuses a
    /// split with <see cref="NotSupportedException"/> before staging anything when the attached surface does not.</summary>
    /// <remarks>A surface that does not present splits cannot roll back to a committed state that holds one: if a
    /// navigation away from such a state fails on it, the rollback is refused too and the host is marked failed, as for
    /// any failed rollback. Attach a surface that presents splits to a router whose state can hold one.</remarks>
    bool PresentsSplits => false;

    /// <summary>Stage resources and publish the target. The native renderer reports completion separately.</summary>
    Task ShowAsync(OahuNavigationTarget target, CancellationToken token);

    /// <summary>Synchronize occurrence resources with an accepted target after projection, reconciliation, or rollback.</summary>
    /// <param name="target">The accepted target.</param>
    /// <param name="applyIfCurrent">Call within the UI turn with the synchronous state update. It returns false when a newer observation or attachment has superseded the target. Do not put mount teardown in this callback.</param>
    /// <param name="token">Cancels work for a timed-out or detached presentation.</param>
    Task AcceptAsync(OahuNavigationTarget target, Func<Action, bool> applyIfCurrent, CancellationToken token);

    /// <summary>Release outgoing resources after the router settles, or after restoring the committed surface.</summary>
    /// <remarks>
    /// This callback can arrive after a newer target has been staged or settled. Do not remove resources that
    /// are displayed or can still be needed by a newer presentation. The token only signals host detachment.
    /// </remarks>
    Task SettleAsync(OahuNavigationTarget target, CancellationToken token);
}

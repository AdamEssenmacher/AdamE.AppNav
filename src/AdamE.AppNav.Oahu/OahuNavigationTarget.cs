using System.Collections.ObjectModel;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Oahu;

/// <summary>A projection of one window, a stack or branch host, and an optional modal stack.</summary>
public sealed record OahuNavigationTarget
{
    internal OahuNavigationTarget(NavigationState state, long revision)
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
            default:
                throw new NotSupportedException("A window must have a stack or a branch host as its root.");
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
        var all = branches.Values.SelectMany(stack => stack).Concat(ModalEntries);
        if (window.Modals.FirstOrDefault() is { Content: not null } owner)
            all = all.Append(owner.RouteEntry);
        foreach (var entry in all)
            if (!ids.Add(entry.Id))
                throw new NotSupportedException($"Duplicate route entry occurrence '{entry.Id}'.");
    }

    private static IReadOnlyList<RouteEntry> StackEntries(StackNode stack) => stack.Entries.Count != 0
        ? Array.AsReadOnly(stack.Entries.ToArray()) : throw new NotSupportedException($"Stack '{stack.Id}' needs a root entry.");

    internal string WindowId { get; } = "";
    internal string RootId { get; } = "";
    internal IReadOnlyList<RouteEntry> VisibleEntries =>
        ModalId is not null ? ModalEntries : Branches.TryGetValue(Branch, out var entries) ? entries : [];
    public OahuPresentationTransition Transition { get; internal init; } = OahuPresentationTransition.Initial;

    public long Revision { get; }
    public bool IsBranchHost { get; }
    public string Branch { get; } = "";
    public IReadOnlyDictionary<string, IReadOnlyList<RouteEntry>> Branches { get; }
    public IReadOnlyDictionary<string, string> BranchTitles { get; }
    public string? ModalId { get; }
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
    /// <summary>Stage resources and publish the target. The native renderer reports completion separately.</summary>
    Task ShowAsync(OahuNavigationTarget target, CancellationToken token);

    /// <summary>Synchronize occurrence resources with an accepted target after projection, reconciliation, or rollback.</summary>
    /// <param name="target">The accepted target.</param>
    /// <param name="applyIfCurrent">Call within the UI turn with the synchronous state update. It returns false when a newer observation or attachment has superseded the target. Do not put mount teardown in this callback.</param>
    /// <param name="token">Cancels work for a timed-out or detached presentation.</param>
    Task AcceptAsync(OahuNavigationTarget target, Func<Action, bool> applyIfCurrent, CancellationToken token);

    /// <summary>Release outgoing resources after the router settles, or after restoring the committed surface.</summary>
    Task SettleAsync(OahuNavigationTarget target, CancellationToken token) => Task.CompletedTask;
}

using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Internal;

namespace AdamE.MauiRouter.State;

/// <summary>
/// Represents a route instance inside a navigation node.
/// </summary>
public sealed record RouteEntry
{
    private IReadOnlyDictionary<string, object?>? _metadata;

    public RouteEntry(
        string Id,
        AppRoute Route,
        NavigationTransition? Transition = null,
        IReadOnlyDictionary<string, object?>? Metadata = null)
    {
        this.Id = Id;
        this.Route = Route;
        this.Transition = Transition;
        this.Metadata = Metadata;
    }

    /// <summary>
    /// Gets the stable identifier of this route entry within its containing node.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// Gets the semantic application route represented by this entry.
    /// </summary>
    public AppRoute Route { get; init; }

    /// <summary>
    /// Gets the transition preference associated with presenting this route entry.
    /// </summary>
    public NavigationTransition? Transition { get; init; }

    /// <summary>
    /// Gets route-entry metadata captured in navigation state.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata
    {
        get => _metadata;
        init => _metadata = CollectionSnapshot.NullableMetadataDictionary(value);
    }

    public void Deconstruct(
        out string Id,
        out AppRoute Route,
        out NavigationTransition? Transition,
        out IReadOnlyDictionary<string, object?>? Metadata)
    {
        Id = this.Id;
        Route = this.Route;
        Transition = this.Transition;
        Metadata = this.Metadata;
    }
}

/// <summary>
/// Represents a linear stack of route entries.
/// </summary>
public sealed record StackNode : NavigationNode
{
    private IReadOnlyList<RouteEntry> _entries = CollectionSnapshot.List<RouteEntry>(null);

    public StackNode(string Id, IReadOnlyList<RouteEntry> Entries)
        : base(Id)
    {
        this.Entries = Entries;
    }

    /// <summary>
    /// Gets the ordered route entries in this stack, from root to top.
    /// </summary>
    public IReadOnlyList<RouteEntry> Entries
    {
        get => _entries;
        init => _entries = CollectionSnapshot.List(value);
    }

    /// <summary>
    /// Gets the top route entry in the stack, or <see langword="null"/> when the stack is empty.
    /// </summary>
    public RouteEntry? Top => Entries.Count == 0 ? null : Entries[^1];

    public void Deconstruct(out string Id, out IReadOnlyList<RouteEntry> Entries)
    {
        Id = this.Id;
        Entries = this.Entries;
    }
}

/// <summary>
/// Represents a platform-neutral host that owns multiple independent navigation branches and tracks one selected branch.
/// </summary>
public sealed record BranchHostNode : NavigationNode
{
    private IReadOnlyList<NavigationBranch> _branches = CollectionSnapshot.List<NavigationBranch>(null);

    public BranchHostNode(
        string Id,
        IReadOnlyList<NavigationBranch> Branches,
        string SelectedBranchId,
        string? DefaultBranchId = null)
        : base(Id)
    {
        this.Branches = Branches;
        this.SelectedBranchId = SelectedBranchId;
        this.DefaultBranchId = DefaultBranchId;
    }

    /// <summary>
    /// Gets the independent navigation branches owned by this host.
    /// </summary>
    public IReadOnlyList<NavigationBranch> Branches
    {
        get => _branches;
        init => _branches = CollectionSnapshot.List(value);
    }

    /// <summary>
    /// Gets the identifier of the branch currently selected by the host.
    /// </summary>
    public string SelectedBranchId { get; init; }

    /// <summary>
    /// Gets the branch identifier the host should return to for default-branch fallback behavior.
    /// </summary>
    public string? DefaultBranchId { get; init; }

    /// <summary>
    /// Gets the branch currently selected by the host, if it still exists.
    /// </summary>
    public NavigationBranch? SelectedBranch =>
        Branches.FirstOrDefault(branch => StringComparer.Ordinal.Equals(branch.Id, SelectedBranchId));

    public void Deconstruct(
        out string Id,
        out IReadOnlyList<NavigationBranch> Branches,
        out string SelectedBranchId,
        out string? DefaultBranchId)
    {
        Id = this.Id;
        Branches = this.Branches;
        SelectedBranchId = this.SelectedBranchId;
        DefaultBranchId = this.DefaultBranchId;
    }

    /// <summary>
    /// Returns a copy of the host with the matching branch replaced.
    /// </summary>
    /// <param name="branch">The branch whose id should replace the existing branch with the same id.</param>
    /// <returns>A copy of this branch host with the matching branch replaced.</returns>
    public BranchHostNode ReplaceBranch(NavigationBranch branch)
    {
        return this with
        {
            Branches = Branches
                .Select(candidate => StringComparer.Ordinal.Equals(candidate.Id, branch.Id) ? branch : candidate)
                .ToArray()
        };
    }
}

/// <summary>
/// Represents a modal route entry with optional nested navigation content.
/// </summary>
/// <param name="Id">The stable identifier of the modal node.</param>
/// <param name="RouteEntry">The route entry that represents the modal shell or route.</param>
/// <param name="Content">Optional nested navigation content owned by the modal.</param>
public sealed record ModalNode(
    string Id,
    RouteEntry RouteEntry,
    NavigationNode? Content = null) : NavigationNode(Id);

/// <summary>
/// Represents the logical navigation tree owned by one application window.
/// </summary>
public sealed record WindowNode : NavigationNode
{
    private IReadOnlyList<ModalNode> _modals = CollectionSnapshot.List<ModalNode>(null);

    public WindowNode(
        string Id,
        NavigationNode? Root = null,
        IReadOnlyList<ModalNode>? Modals = null)
        : base(Id)
    {
        this.Root = Root;
        this.Modals = Modals ?? CollectionSnapshot.List<ModalNode>(null);
    }

    /// <summary>
    /// Gets the window's root navigation node, or <see langword="null"/> when the window has no root content.
    /// </summary>
    public NavigationNode? Root { get; init; }

    /// <summary>
    /// Gets the modal nodes currently presented by the window, from bottom to top.
    /// </summary>
    public IReadOnlyList<ModalNode> Modals
    {
        get => _modals;
        init => _modals = CollectionSnapshot.List(value);
    }

    public void Deconstruct(
        out string Id,
        out NavigationNode? Root,
        out IReadOnlyList<ModalNode>? Modals)
    {
        Id = this.Id;
        Root = this.Root;
        Modals = this.Modals;
    }
}

using AdamE.AppNav.Internal;

namespace AdamE.AppNav.State;

/// <summary>
/// Represents a route instance inside a navigation node.
/// </summary>
public sealed record RouteEntry
{
    public RouteEntry(
        string Id,
        AppRoute Route,
        IReadOnlyDictionary<string, object?>? Metadata = null)
    {
        this.Id = Id;
        this.Route = Route;
        this.Metadata = Metadata;
    }

    /// <summary>
    /// Gets the stable presenter reuse identifier of this route entry within its containing stack.
    /// </summary>
    public string Id
    {
        get;
        init => field = NavigationIdentity.RequiredId(value, nameof(Id));
    } = null!;

    /// <summary>
    /// Gets the semantic application route represented by this entry.
    /// </summary>
    public AppRoute Route
    {
        get;
        init => field = NavigationIdentity.Required(value, nameof(Route));
    } = null!;

    /// <summary>
    /// Gets route-entry metadata captured in the navigation state.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata
    {
        get;
        private init => field = CollectionSnapshot.NullableMetadataDictionary(value);
    }
}

/// <summary>
/// Represents a linear stack of route entries.
/// </summary>
public sealed record StackNode : NavigationNode
{
    private protected override void SealNodeType()
    {
    }

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
        get;
        init
        {
            IReadOnlyList<RouteEntry> entries = NavigationIdentity.RequiredList(value, nameof(Entries));
            NavigationIdentity.EnsureUniqueIds(
                entries,
                static entry => entry.Id,
                nameof(Entries),
                "route-entry id",
                "Stack entries");
            field = entries;
        }
    } = CollectionSnapshot.List<RouteEntry>(null);

    /// <summary>
    /// Gets the top route entry in the stack, or <see langword="null"/> when the stack is empty.
    /// </summary>
    public RouteEntry? Top => Entries.Count == 0 ? null : Entries[^1];
}

/// <summary>
/// Represents a platform-neutral host that owns multiple independent navigation branches and tracks one selected branch.
/// </summary>
public sealed record BranchHostNode : NavigationNode
{
    private protected override void SealNodeType()
    {
    }

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
        ValidateBranchReferences();
    }

    /// <summary>
    /// Gets the independent navigation branches owned by this host.
    /// </summary>
    public IReadOnlyList<NavigationBranch> Branches
    {
        get;
        init
        {
            IReadOnlyList<NavigationBranch> branches = NavigationIdentity.RequiredList(value, nameof(Branches));
            NavigationIdentity.EnsureNotEmpty(branches, nameof(Branches), "Branch hosts");
            NavigationIdentity.EnsureUniqueIds(
                branches,
                static branch => branch.Id,
                nameof(Branches),
                "branch id",
                "Branch host branches");
            field = branches;
        }
    } = CollectionSnapshot.List<NavigationBranch>(null);

    /// <summary>
    /// Gets the identifier of the branch currently selected by the host.
    /// </summary>
    public string SelectedBranchId
    {
        get;
        init => field = NavigationIdentity.RequiredId(value, nameof(SelectedBranchId));
    } = null!;

    /// <summary>
    /// Gets the branch identifier the host should return to for default-branch fallback behavior.
    /// </summary>
    public string? DefaultBranchId
    {
        get;
        init => field = NavigationIdentity.OptionalId(value, nameof(DefaultBranchId));
    }

    /// <summary>
    /// Gets the branch currently selected by the host if it still exists.
    /// </summary>
    public NavigationBranch? SelectedBranch =>
        Branches.FirstOrDefault(branch => StringComparer.Ordinal.Equals(branch.Id, SelectedBranchId));

    /// <summary>
    /// Returns a copy of the host with the matching branch replaced.
    /// </summary>
    /// <param name="branch">The branch whose id should replace the existing branch with the same id.</param>
    /// <returns>A copy of this branch host with the matching branch replaced.</returns>
    public BranchHostNode ReplaceBranch(NavigationBranch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);

        return this with
        {
            Branches = Branches
                .Select(candidate => StringComparer.Ordinal.Equals(candidate.Id, branch.Id) ? branch : candidate)
                .ToArray()
        };
    }

    private void ValidateBranchReferences()
    {
        if (!Branches.Any(branch => StringComparer.Ordinal.Equals(branch.Id, SelectedBranchId)))
            throw new ArgumentException(
                $"Selected branch id '{SelectedBranchId}' must reference an existing branch.",
                nameof(SelectedBranchId));

        if (DefaultBranchId is not null &&
            !Branches.Any(branch => StringComparer.Ordinal.Equals(branch.Id, DefaultBranchId)))
            throw new ArgumentException(
                $"Default branch id '{DefaultBranchId}' must reference an existing branch.",
                nameof(DefaultBranchId));
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
    NavigationNode? Content = null) : NavigationNode(Id)
{
    private protected override void SealNodeType()
    {
    }

    /// <summary>
    /// Gets the route entry that represents the modal shell or route.
    /// </summary>
    public RouteEntry RouteEntry
    {
        get;
        init => field = NavigationIdentity.Required(value, nameof(RouteEntry));
    } = NavigationIdentity.Required(RouteEntry, nameof(RouteEntry));
}

/// <summary>
/// Represents the logical navigation tree owned by one application window.
/// </summary>
public sealed record WindowNode : NavigationNode
{
    private protected override void SealNodeType()
    {
    }

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
        get;
        init
        {
            IReadOnlyList<ModalNode> modals = NavigationIdentity.OptionalList(value, nameof(Modals));
            NavigationIdentity.EnsureUniqueIds(
                modals,
                static modal => modal.Id,
                nameof(Modals),
                "modal id",
                "Window modals");
            field = modals;
        }
    } = CollectionSnapshot.List<ModalNode>(null);
}

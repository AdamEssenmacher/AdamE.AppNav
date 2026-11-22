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
/// Represents a platform-neutral two-column layout: a primary column beside an optional secondary column, each holding
/// its own navigation tree.
/// </summary>
/// <remarks>
/// <para>
/// A split describes logical state only. A presenter with room for both columns shows them side by side. A presenter
/// that is too narrow collapses them into one stack: the primary column's tree, with the secondary column's tree above
/// it when <see cref="CompactColumn"/> is <see cref="SplitColumn.Secondary"/>. What a column shows is its tree's
/// presented content; for a column holding a branch host, that is its selected branch's content. A change of window
/// width changes only how the split is presented, never its logical state, so collapsing and expanding again shows
/// the same trees.
/// </para>
/// <para>
/// For example, a mail app keeps its mailbox list in the primary column and the selected message's stack in the
/// secondary column. A null <see cref="Secondary"/> means nothing is selected; a presenter may show a placeholder
/// in its place. A split can also show a default message beside the list on wide windows while a collapsed
/// presentation keeps the list on top: give it a secondary tree and <see cref="SplitColumn.Primary"/>.
/// </para>
/// <para>
/// A split differs from a <see cref="BranchHostNode"/> in two ways. A branch host shows one selected branch at a
/// time, and that choice is logical state; a split's columns are both part of what is presented, and whether they
/// appear together depends only on the room the presenter has. A branch host's branches are independent peers; a
/// split's columns are ordered, and the secondary tree belongs to what the primary tree shows.
/// </para>
/// <para>
/// Back does not depend on how the split is presented. While <see cref="CompactColumn"/> is
/// <see cref="SplitColumn.Secondary"/>, Back goes back inside the secondary tree, and from its root closes the
/// secondary column: <see cref="Secondary"/> becomes null, so its entries leave the state. Otherwise Back goes back
/// inside the primary tree. Back inside the primary tree also closes the secondary column, because the secondary tree
/// belonged to the primary context that was left; that includes a default secondary tree shown beside a primary
/// column that is on top. The route a navigation result reports is the top of the column
/// <see cref="CompactColumn"/> names.
/// </para>
/// <para>
/// A split cannot be nested, at any depth, inside a modal or another split; router validation rejects such a state.
/// </para>
/// <para>
/// Planners change the selection with <see cref="Select"/> and <see cref="ClearSecondary"/>. A
/// <c>with { Secondary = ... }</c> expression does not apply the constructor's <see cref="CompactColumn"/> default
/// again: it keeps the current column, so selecting an item that way on a split showing its primary column leaves the
/// list on top, and clearing the selection that way on a split showing its secondary column builds a split that
/// router validation rejects. Every route entry in both trees is part of the committed state, so entry IDs must be
/// unique across both columns, as they are across branches.
/// </para>
/// </remarks>
public sealed record SplitNode : NavigationNode
{
    private protected override void SealNodeType()
    {
    }

    /// <summary>
    /// Creates a split.
    /// </summary>
    /// <param name="Id">The stable structural identifier of the split.</param>
    /// <param name="Primary">The navigation tree of the primary column.</param>
    /// <param name="Secondary">
    /// The navigation tree of the secondary column, or <see langword="null"/> when nothing is selected.
    /// </param>
    /// <param name="CompactColumn">
    /// The column a collapsed presentation shows on top, or <see langword="null"/> for the secondary column when
    /// <paramref name="Secondary"/> is non-null and the primary column otherwise. <see cref="SplitColumn.Secondary"/>
    /// requires a non-null <paramref name="Secondary"/>.
    /// </param>
    public SplitNode(
        string Id,
        NavigationNode Primary,
        NavigationNode? Secondary = null,
        SplitColumn? CompactColumn = null)
        : base(Id)
    {
        this.Primary = Primary;
        this.Secondary = Secondary;
        this.CompactColumn = CompactColumn ?? DefaultCompactColumn(Secondary);
        ValidateCompactColumn();
    }

    /// <summary>
    /// Gets the navigation tree of the primary column.
    /// </summary>
    public NavigationNode Primary
    {
        get;
        init => field = NavigationIdentity.Required(value, nameof(Primary));
    }

    /// <summary>
    /// Gets the navigation tree of the secondary column, or <see langword="null"/> when nothing is selected.
    /// </summary>
    /// <remarks>
    /// To remove the selection, call <see cref="ClearSecondary"/>. A <c>with { Secondary = null }</c> expression keeps
    /// the current <see cref="CompactColumn"/>, so on a split showing its secondary column it builds a split that
    /// router validation rejects.
    /// </remarks>
    public NavigationNode? Secondary { get; init; }

    /// <summary>
    /// Gets the column a collapsed, single-column presentation shows on top.
    /// </summary>
    /// <remarks>
    /// <see cref="SplitColumn.Primary"/> shows only the primary column's tree. <see cref="SplitColumn.Secondary"/> shows
    /// the primary column's tree with the secondary column's tree above it.
    /// </remarks>
    public SplitColumn CompactColumn
    {
        get;
        init => field = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(CompactColumn), value, "Unknown split column.");
    }

    /// <summary>
    /// Returns a copy of the split that shows <paramref name="secondary"/> in its secondary column.
    /// </summary>
    /// <param name="secondary">The navigation tree of the selected item.</param>
    /// <param name="compactColumn">
    /// The column a collapsed presentation shows on top, or <see langword="null"/> for
    /// <see cref="SplitColumn.Secondary"/>, as the constructor resolves it.
    /// </param>
    /// <returns>A copy of this split with the selection and compact column set.</returns>
    /// <remarks>
    /// Prefer this to <c>with { Secondary = ... }</c>, which keeps the current <see cref="CompactColumn"/> rather
    /// than resolving it again.
    /// </remarks>
    public SplitNode Select(NavigationNode secondary, SplitColumn? compactColumn = null)
    {
        ArgumentNullException.ThrowIfNull(secondary);
        return this with { Secondary = secondary, CompactColumn = compactColumn ?? DefaultCompactColumn(secondary) };
    }

    /// <summary>
    /// Returns a copy of the split with nothing selected: no secondary column, and the primary column shown.
    /// </summary>
    /// <returns>A copy of this split with <see cref="Secondary"/> null and <see cref="CompactColumn"/>
    /// <see cref="SplitColumn.Primary"/>.</returns>
    public SplitNode ClearSecondary() =>
        this with { Secondary = null, CompactColumn = SplitColumn.Primary };

    private static SplitColumn DefaultCompactColumn(NavigationNode? secondary) =>
        secondary is null ? SplitColumn.Primary : SplitColumn.Secondary;

    private void ValidateCompactColumn()
    {
        if (CompactColumn == SplitColumn.Secondary && Secondary is null)
            throw new ArgumentException(
                "A split can show its secondary column only when it has secondary content.",
                nameof(CompactColumn));
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

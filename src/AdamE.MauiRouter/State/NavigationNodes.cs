using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Internal;

namespace AdamE.MauiRouter.State;

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

    public string Id { get; init; }

    public AppRoute Route { get; init; }

    public NavigationTransition? Transition { get; init; }

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

public sealed record StackNode : NavigationNode
{
    private IReadOnlyList<RouteEntry> _entries = CollectionSnapshot.List<RouteEntry>(null);

    public StackNode(string Id, IReadOnlyList<RouteEntry> Entries)
        : base(Id)
    {
        this.Entries = Entries;
    }

    public IReadOnlyList<RouteEntry> Entries
    {
        get => _entries;
        init => _entries = CollectionSnapshot.List(value);
    }

    public RouteEntry? Top => Entries.Count == 0 ? null : Entries[^1];

    public void Deconstruct(out string Id, out IReadOnlyList<RouteEntry> Entries)
    {
        Id = this.Id;
        Entries = this.Entries;
    }
}

public sealed record TabsNode : NavigationNode
{
    private IReadOnlyList<NavigationBranch> _branches = CollectionSnapshot.List<NavigationBranch>(null);

    public TabsNode(
        string Id,
        IReadOnlyList<NavigationBranch> Branches,
        string SelectedTabId,
        string? DefaultTabId = null)
        : base(Id)
    {
        this.Branches = Branches;
        this.SelectedTabId = SelectedTabId;
        this.DefaultTabId = DefaultTabId;
    }

    public IReadOnlyList<NavigationBranch> Branches
    {
        get => _branches;
        init => _branches = CollectionSnapshot.List(value);
    }

    public string SelectedTabId { get; init; }

    public string? DefaultTabId { get; init; }

    public NavigationBranch? SelectedBranch =>
        Branches.FirstOrDefault(branch => StringComparer.Ordinal.Equals(branch.Id, SelectedTabId));

    public void Deconstruct(
        out string Id,
        out IReadOnlyList<NavigationBranch> Branches,
        out string SelectedTabId,
        out string? DefaultTabId)
    {
        Id = this.Id;
        Branches = this.Branches;
        SelectedTabId = this.SelectedTabId;
        DefaultTabId = this.DefaultTabId;
    }

    public TabsNode ReplaceBranch(NavigationBranch branch)
    {
        return this with
        {
            Branches = Branches
                .Select(candidate => StringComparer.Ordinal.Equals(candidate.Id, branch.Id) ? branch : candidate)
                .ToArray()
        };
    }
}

public sealed record FlyoutNode : NavigationNode
{
    private IReadOnlyList<NavigationBranch> _branches = CollectionSnapshot.List<NavigationBranch>(null);

    public FlyoutNode(
        string Id,
        IReadOnlyList<NavigationBranch> Branches,
        string SelectedItemId,
        string? DefaultItemId = null)
        : base(Id)
    {
        this.Branches = Branches;
        this.SelectedItemId = SelectedItemId;
        this.DefaultItemId = DefaultItemId;
    }

    public IReadOnlyList<NavigationBranch> Branches
    {
        get => _branches;
        init => _branches = CollectionSnapshot.List(value);
    }

    public string SelectedItemId { get; init; }

    public string? DefaultItemId { get; init; }

    public NavigationBranch? SelectedBranch =>
        Branches.FirstOrDefault(branch => StringComparer.Ordinal.Equals(branch.Id, SelectedItemId));

    public void Deconstruct(
        out string Id,
        out IReadOnlyList<NavigationBranch> Branches,
        out string SelectedItemId,
        out string? DefaultItemId)
    {
        Id = this.Id;
        Branches = this.Branches;
        SelectedItemId = this.SelectedItemId;
        DefaultItemId = this.DefaultItemId;
    }

    public FlyoutNode ReplaceBranch(NavigationBranch branch)
    {
        return this with
        {
            Branches = Branches
                .Select(candidate => StringComparer.Ordinal.Equals(candidate.Id, branch.Id) ? branch : candidate)
                .ToArray()
        };
    }
}

public sealed record ModalNode(
    string Id,
    RouteEntry RouteEntry,
    NavigationNode? Content = null) : NavigationNode(Id);

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

    public NavigationNode? Root { get; init; }

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

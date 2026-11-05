using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

public sealed class NavigationTopologyTests
{
    private const string Window = "main";
    private const string Stack = "stack";
    private const string Owner = "owner";

    private static readonly NavigationTopologySupport TwoDeep = new(MaxStackEntries: 2);

    private static readonly NavigationTopology Topology = new NavigationTopology(Window, Stack)
        .WithModal("sheet", NavigationModalPlacement.OverRoot)
        .WithModal("anywhere", NavigationModalPlacement.OverAnyEntry)
        .WithOwnedModal("owned", static entry => entry.Metadata?.TryGetValue(Owner, out object? id) == true
            ? id as string
            : null);

    [Fact]
    public void DeclarationRejectsMisuseAndCopiesOnDeclare()
    {
        Assert.Throws<ArgumentException>(() => Topology.WithModal("other", NavigationModalPlacement.OverOwnerEntry));
        Assert.Throws<ArgumentOutOfRangeException>(() => Topology.WithModal("other", (NavigationModalPlacement)7));
        Assert.Throws<ArgumentException>(() => Topology.WithModal("sheet", NavigationModalPlacement.OverAnyEntry));
        Assert.Throws<ArgumentException>(() => new NavigationTopology(" ", Stack));
        // A null owner on purpose: the null check is what is tested.
        Assert.Throws<ArgumentNullException>(() => Topology.WithOwnedModal("other", null!));
        NavigationTopology wider = Topology.WithModal("other", NavigationModalPlacement.OverAnyEntry);
        Assert.False(Topology.Modals.ContainsKey("other"));
        Assert.Equal(4, wider.Modals.Count);
    }

    [Fact]
    public void SupportRejectsImpossibleLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NavigationTopologySupport(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NavigationTopologySupport(2, MaxModals: -1));
    }

    [Fact]
    public void APlanWithNoWindowMatchesNothing() =>
        Assert.Null(Topology.Match(NavigationState.Empty, TwoDeep));

    [Fact]
    public void DeclaredShapesMatchWithTheirOwners()
    {
        NavigationTopologyMatch root = Assert.IsType<NavigationTopologyMatch>(
            Topology.Match(Plan(["root"], new ModalNode("sheet", Entry("sheet-entry"))), TwoDeep));
        Assert.Equal(Stack, root.Stack.Id);
        Assert.Equal("sheet", Assert.Single(root.Modals).Node.Id);
        Assert.Null(root.Modals[0].OwnerEntryId);

        NavigationTopologyMatch anywhere = Assert.IsType<NavigationTopologyMatch>(
            Topology.Match(Plan(["root", "pushed"], new ModalNode("anywhere", Entry("any"))), TwoDeep));
        Assert.Equal(2, anywhere.Stack.Entries.Count);

        NavigationTopologyMatch owned = Assert.IsType<NavigationTopologyMatch>(
            Topology.Match(Plan(["root", "pushed"], new ModalNode("owned", Entry("answer", "pushed"))), TwoDeep));
        Assert.Equal("pushed", Assert.Single(owned.Modals).OwnerEntryId);
    }

    public static TheoryData<string> RefusedShapes =>
    [
        "two-windows",
        "other-window",
        "root-not-a-stack",
        "other-stack",
        "empty-stack",
        "too-deep",
        "too-many-modals",
        "undeclared-modal",
        "nested-content",
        "over-root-over-pushed",
        "owned-not-top",
        "owned-without-owner"
    ];

    [Theory]
    [MemberData(nameof(RefusedShapes))]
    public void OtherShapesAreRefusedAsUnsupported(string shape)
    {
        NavigationState state = shape switch
        {
            "two-windows" => new NavigationState(
                [
                    new WindowNode(Window, new StackNode(Stack, [Entry("root")])),
                    new WindowNode("second", new StackNode("second-stack", [Entry("other")]))
                ],
                Window),
            "other-window" => new NavigationState([new WindowNode("other", new StackNode(Stack, [Entry("root")]))], "other"),
            "root-not-a-stack" => new NavigationState(
                [
                    new WindowNode(
                        Window,
                        new BranchHostNode(
                            "branches",
                            [new NavigationBranch("one", "One", new StackNode(Stack, [Entry("root")]))],
                            "one"))
                ],
                Window),
            "other-stack" => new NavigationState([new WindowNode(Window, new StackNode("other", [Entry("root")]))], Window),
            "empty-stack" => Plan([]),
            "too-deep" => Plan(["root", "a", "b"]),
            "too-many-modals" => Plan(
                ["root"],
                new ModalNode("sheet", Entry("one")),
                new ModalNode("anywhere", Entry("two"))),
            "undeclared-modal" => Plan(["root"], new ModalNode("undeclared", Entry("one"))),
            "nested-content" => Plan(
                ["root"],
                new ModalNode("sheet", Entry("one"), new StackNode("nested", [Entry("inner")]))),
            "over-root-over-pushed" => Plan(["root", "pushed"], new ModalNode("sheet", Entry("one"))),
            "owned-not-top" => Plan(["root", "pushed"], new ModalNode("owned", Entry("answer", "root"))),
            "owned-without-owner" => Plan(["root", "pushed"], new ModalNode("owned", Entry("answer"))),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };

        Assert.Throws<NotSupportedException>(() => Topology.Match(state, TwoDeep));
    }

    [Fact]
    public void LimitsAreThePresentersAndGrowWithoutChangingTheDeclaration()
    {
        NavigationState deep = Plan(["root", "a", "b"]);
        NavigationState two = Plan(
            ["root"],
            new ModalNode("sheet", Entry("one")),
            new ModalNode("anywhere", Entry("two")));
        NavigationState nested = Plan(["root"], new ModalNode("sheet", Entry("one"), new StackNode("nested", [Entry("inner")])));

        Assert.Equal(3, Topology.Match(deep, new NavigationTopologySupport(3))?.Stack.Entries.Count);
        Assert.Equal(2, Topology.Match(two, new NavigationTopologySupport(2, MaxModals: 2))?.Modals.Count);
        Assert.NotNull(Topology.Match(nested, new NavigationTopologySupport(2, ModalContent: true)));
        NotSupportedException refused = Assert.Throws<NotSupportedException>(() => Topology.Match(deep, TwoDeep));
        Assert.Contains("1 to 2 entries", refused.Message, StringComparison.Ordinal);
    }

    private static RouteEntry Entry(string id, string? owner = null) =>
        new(id, new TopologyRoute(), owner is null ? null : new Dictionary<string, object?> { [Owner] = owner });

    private static NavigationState Plan(string[] entries, params ModalNode[] modals) =>
        new([new WindowNode(Window, new StackNode(Stack, [.. entries.Select(static id => Entry(id))]), modals)], Window);

    private sealed record TopologyRoute : AppRoute;
}

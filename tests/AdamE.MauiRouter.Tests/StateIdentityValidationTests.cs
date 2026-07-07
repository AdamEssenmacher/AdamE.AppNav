using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Tests;

public sealed class StateIdentityValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RouteEntryRequiresStableId(string? id)
    {
        Assert.Throws<ArgumentException>(() => new RouteEntry(id!, new TestRoute("home")));
    }

    [Fact]
    public void RouteEntryRequiresRoute()
    {
        Assert.Throws<ArgumentNullException>(() => new RouteEntry("home", null!));
    }

    [Fact]
    public void StackNodeRejectsNullEntriesAndDuplicateEntryIds()
    {
        Assert.Throws<ArgumentNullException>(() => new StackNode("stack", null!));
        Assert.Throws<ArgumentException>(() => new StackNode("stack", new RouteEntry[] { null! }));
        Assert.Throws<ArgumentException>(() => new StackNode("stack", new[]
        {
            Entry("product"),
            Entry("product")
        }));
    }

    [Fact]
    public void EmptyStackNodeIsValid()
    {
        var stack = new StackNode("stack", Array.Empty<RouteEntry>());

        Assert.Empty(stack.Entries);
    }

    [Fact]
    public void RouteEntryIdsMayRepeatAcrossIndependentStacks()
    {
        var state = new NavigationState(new[]
        {
            new WindowNode(
                "main",
                new BranchHostNode(
                    "branchHost",
                    new[]
                    {
                        new NavigationBranch("home", "Home", new StackNode("home-stack", new[] { Entry("root") })),
                        new NavigationBranch("catalog", "Catalog", new StackNode("catalog-stack", new[] { Entry("root") }))
                    },
                    "home",
                    "home"))
        }, "main");

        var branchHost = Assert.IsType<BranchHostNode>(state.ActiveWindow!.Root);
        Assert.All(branchHost.Branches, branch =>
        {
            var stack = Assert.IsType<StackNode>(branch.Content);
            Assert.Equal("root", stack.Top!.Id);
        });
    }

    [Fact]
    public void BranchHostRejectsEmptyDuplicateOrMissingBranchReferences()
    {
        Assert.Throws<ArgumentException>(() =>
            new BranchHostNode("branchHost", Array.Empty<NavigationBranch>(), "home"));
        Assert.Throws<ArgumentException>(() =>
            new BranchHostNode(
                "branchHost",
                new[]
                {
                    Branch("home"),
                    Branch("home")
                },
                "home"));
        Assert.Throws<ArgumentException>(() =>
            new BranchHostNode("branchHost", new[] { Branch("home") }, "missing"));
        Assert.Throws<ArgumentException>(() =>
            new BranchHostNode("branchHost", new[] { Branch("home") }, "home", "missing"));
    }

    [Fact]
    public void NavigationStateRejectsNullWindowsAndDuplicateWindowIds()
    {
        Assert.Throws<ArgumentNullException>(() => new NavigationState(null!));
        Assert.Throws<ArgumentException>(() => new NavigationState(new WindowNode[] { null! }));
        Assert.Throws<ArgumentException>(() => new NavigationState(new[]
        {
            new WindowNode("main"),
            new WindowNode("main")
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FindWindowDoesNotTreatBlankIdAsActiveWindow(string? id)
    {
        var state = new NavigationState(new[]
        {
            new WindowNode("main"),
            new WindowNode("secondary")
        });

        Assert.Equal("main", state.ActiveWindow!.Id);
        Assert.Null(state.FindWindow(id));
        Assert.Equal("secondary", state.FindWindow("secondary")!.Id);
    }

    [Fact]
    public void WindowNodeRejectsDuplicateModalIds()
    {
        Assert.Throws<ArgumentException>(() => new WindowNode(
            "main",
            Stack("stack"),
            new[]
            {
                new ModalNode("cart", Entry("cart")),
                new ModalNode("cart", Entry("cart-detail"))
            }));
    }

    [Fact]
    public void BranchRequiresStableIdentityAndContent()
    {
        Assert.Throws<ArgumentException>(() => new NavigationBranch("", "Home", Stack("stack")));
        Assert.Throws<ArgumentException>(() => new NavigationBranch("home", "", Stack("stack")));
        Assert.Throws<ArgumentNullException>(() => new NavigationBranch("home", "Home", null!));
    }

    [Fact]
    public void ModalRequiresRouteEntry()
    {
        Assert.Throws<ArgumentNullException>(() => new ModalNode("modal", null!));
    }

    private static NavigationBranch Branch(string id)
    {
        return new NavigationBranch(id, id, Stack($"{id}-stack"));
    }

    private static StackNode Stack(string id)
    {
        return new StackNode(id, new[] { Entry("home") });
    }

    private static RouteEntry Entry(string id)
    {
        return new RouteEntry(id, new TestRoute(id));
    }

    private sealed record TestRoute(string Value) : AppRoute;
}

using AdamE.AppNav.Back;
using AdamE.AppNav.Internal;
using AdamE.AppNav.Leases;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;

namespace AdamE.AppNav.Tests;

// A mail app: the mailbox list in the primary column beside the selected message's own stack.
public sealed class SplitNodeTests
{
    [Fact]
    public void ConstructionDefaultsToThePrimaryColumnWithNothingSelected()
    {
        var split = new SplitNode("mail", Mailboxes());

        Assert.Equal("mail", split.Id);
        Assert.Equal("mailboxes", split.Primary.Id);
        Assert.Null(split.Secondary);
        Assert.Equal(SplitColumn.Primary, split.CompactColumn);
    }

    [Fact]
    public void ConstructionDefaultsToTheSecondaryColumnWhenSomethingIsSelected()
    {
        Assert.Equal(SplitColumn.Secondary, new SplitNode("mail", Mailboxes(), Message("m1")).CompactColumn);
        Assert.Equal(
            SplitColumn.Secondary,
            new SplitNode("mail", Mailboxes(), Message("m1"), CompactColumn: null).CompactColumn);
    }

    [Fact]
    public void AnExplicitPrimaryColumnKeepsTheListOnTopBesideADefaultSelection()
    {
        // Wide windows show a default message beside the list; a collapsed window keeps the list on top.
        var split = new SplitNode("mail", Mailboxes(), Message("welcome"), SplitColumn.Primary);

        Assert.Equal(SplitColumn.Primary, split.CompactColumn);
        Assert.Equal(["welcome"], EntryIds(split.Secondary));
        Assert.Equal(new MailRoute("inbox"), PresentedRouteTraversal.FindTopRoute(split));
        NavigationStateValidator.ValidateState(Root(split), "Test");
    }

    [Fact]
    public void ConstructionRejectsMissingAndInconsistentValues()
    {
        // Nulls on purpose: the null checks are what is tested.
        Assert.Throws<ArgumentNullException>(() => new SplitNode("mail", null!));
        Assert.Throws<ArgumentException>(() => new SplitNode(" ", Mailboxes()));
        Assert.Throws<ArgumentException>(() => new SplitNode("mail", Mailboxes(), null, SplitColumn.Secondary));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SplitNode("mail", Mailboxes(), Message("m1"), (SplitColumn)7));

        SplitNode split = Split(SplitColumn.Secondary, Message("m1"));
        Assert.Throws<ArgumentNullException>(() => split with { Primary = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => split with { CompactColumn = (SplitColumn)(-1) });
    }

    [Fact]
    public void SplitsCompareByValueAndCopyWithChanges()
    {
        // Stack entries compare by reference, as for every node, so equal splits share their column trees.
        StackNode mailboxes = Mailboxes();
        StackNode message = Message("m1", "attachment");
        var split = new SplitNode("mail", mailboxes, message, SplitColumn.Secondary);
        var same = new SplitNode("mail", mailboxes, message, SplitColumn.Secondary);

        Assert.Equal(split, same);
        Assert.Equal(split.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(split, split with { CompactColumn = SplitColumn.Primary });
        Assert.NotEqual(split, split with { Secondary = Message("m2") });

        SplitNode collapsedToList = split with { CompactColumn = SplitColumn.Primary };
        Assert.Equal(split.Secondary, collapsedToList.Secondary);
        Assert.Equal(SplitColumn.Primary, collapsedToList.CompactColumn);
    }

    [Fact]
    public void ValidatorAcceptsSplitsAsWindowRootsAndBranchContent()
    {
        NavigationStateValidator.ValidateState(Root(Split(SplitColumn.Secondary, Message("m1"))), "Test");
        NavigationStateValidator.ValidateState(Root(Split(SplitColumn.Primary)), "Test");
        NavigationStateValidator.ValidateState(
            Root(new BranchHostNode(
                "tabs",
                [new NavigationBranch("mail", "Mail", Split(SplitColumn.Secondary, Message("m1")))],
                "mail")),
            "Test");
    }

    [Fact]
    public void ValidatorRecursesIntoBothColumns()
    {
        var invalidHost = new BranchHostNode(
            "folders",
            [new NavigationBranch("inbox", "Inbox", Mailboxes())],
            "inbox") with { SelectedBranchId = "missing" };

        AppNavigationConfigurationException primary = Assert.Throws<AppNavigationConfigurationException>(() =>
            NavigationStateValidator.ValidateState(Root(new SplitNode("mail", invalidHost)), "Test"));
        Assert.Contains("Root.Primary", primary.Message, StringComparison.Ordinal);

        AppNavigationConfigurationException secondary = Assert.Throws<AppNavigationConfigurationException>(() =>
            NavigationStateValidator.ValidateState(Root(new SplitNode("mail", Mailboxes(), invalidHost)), "Test"));
        Assert.Contains("Root.Secondary", secondary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorRejectsASecondaryColumnShownWithoutContent()
    {
        // A copy can drop the secondary content after construction; validation catches it before presentation.
        SplitNode split = Split(SplitColumn.Secondary, Message("m1")) with { Secondary = null };

        AppNavigationConfigurationException exception = Assert.Throws<AppNavigationConfigurationException>(() =>
            NavigationStateValidator.ValidateState(Root(split), "Test"));
        Assert.Contains("no secondary content", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorRejectsSplitsInsideModalsAndOtherSplits()
    {
        var modalState = new NavigationState(
            [
                new WindowNode(
                    "main",
                    Mailboxes(),
                    [new ModalNode("compose", Entry("compose"), Split(SplitColumn.Primary))])
            ],
            "main");
        AppNavigationConfigurationException modal = Assert.Throws<AppNavigationConfigurationException>(() =>
            NavigationStateValidator.ValidateState(modalState, "Test"));
        Assert.Contains("inside a modal", modal.Message, StringComparison.Ordinal);

        var nestedModalBranch = new ModalNode(
            "sheet",
            Entry("sheet"),
            new BranchHostNode("tabs", [new NavigationBranch("mail", "Mail", Split(SplitColumn.Primary))], "mail"));
        Assert.Throws<AppNavigationConfigurationException>(() =>
            NavigationStateValidator.ValidateState(Root(nestedModalBranch), "Test"));

        var nestedSplit = new SplitNode("outer", Mailboxes(), Split(SplitColumn.Primary));
        AppNavigationConfigurationException nested = Assert.Throws<AppNavigationConfigurationException>(() =>
            NavigationStateValidator.ValidateState(Root(nestedSplit), "Test"));
        Assert.Contains("inside another split", nested.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorRejectsASplitNestedThroughABranchHostInsideAnotherSplit()
    {
        var tabs = new BranchHostNode(
            "tabs",
            [new NavigationBranch("mail", "Mail", Split(SplitColumn.Primary))],
            "mail");
        var outer = new SplitNode("outer", new StackNode("accounts", [Entry("accounts")]), tabs);

        AppNavigationConfigurationException exception = Assert.Throws<AppNavigationConfigurationException>(() =>
            NavigationStateValidator.ValidateState(Root(outer), "Test"));
        Assert.Contains("Root.Secondary.Branch('mail').Content", exception.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be nested, at any depth", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectResolvesTheCompactColumnLikeTheConstructor()
    {
        SplitNode empty = Split(SplitColumn.Primary);
        StackNode message = Message("m1");

        SplitNode selected = empty.Select(message);
        Assert.Same(message, selected.Secondary);
        Assert.Equal(SplitColumn.Secondary, selected.CompactColumn);

        SplitNode listOnTop = empty.Select(message, SplitColumn.Primary);
        Assert.Same(message, listOnTop.Secondary);
        Assert.Equal(SplitColumn.Primary, listOnTop.CompactColumn);

        // A null on purpose: the null check is what is tested.
        Assert.Throws<ArgumentNullException>(() => empty.Select(null!));
    }

    [Fact]
    public void ClearSecondaryShowsThePrimaryColumn()
    {
        SplitNode cleared = Split(SplitColumn.Secondary, Message("m1")).ClearSecondary();

        Assert.Null(cleared.Secondary);
        Assert.Equal(SplitColumn.Primary, cleared.CompactColumn);
        Assert.Equal("mailboxes", cleared.Primary.Id);
    }

    [Fact]
    public void AWithExpressionKeepsTheCompactColumnItHad()
    {
        // The documented trap: with does not resolve the default again, so the list stays on top.
        SplitNode selectedWithWith = Split(SplitColumn.Primary) with { Secondary = Message("m1") };

        Assert.Equal(SplitColumn.Primary, selectedWithWith.CompactColumn);
        Assert.Equal(SplitColumn.Secondary, Split(SplitColumn.Primary).Select(Message("m1")).CompactColumn);
    }

    [Fact]
    public void BackPopsTheSecondaryColumnWhileItIsShown()
    {
        NavigationState state = Root(Split(SplitColumn.Secondary, Message("m1", "attachment")));

        NavigationPlan plan = Back(state);

        SplitNode split = RootSplit(plan);
        Assert.Equal(SplitColumn.Secondary, split.CompactColumn);
        Assert.Equal(["m1"], EntryIds(split.Secondary));
        Assert.Equal("Back navigation will pop the selected stack.", plan.Reason);
    }

    [Fact]
    public void BackFromTheSecondaryRootClosesTheSecondaryColumn()
    {
        NavigationState state = Root(Split(SplitColumn.Secondary, Message("m1")));

        NavigationPlan plan = Back(state);

        SplitNode split = RootSplit(plan);
        Assert.Null(split.Secondary);
        Assert.Equal(SplitColumn.Primary, split.CompactColumn);
        Assert.Equal("Back navigation will close the secondary column.", plan.Reason);
        // The closed column's entry leaves the state, so its lease ends.
        Assert.Equal(
            new HashSet<string>(["inbox"], StringComparer.Ordinal),
            RouteEntryLeaseRegistry.EntryIds(plan.TargetState));
    }

    [Fact]
    public void BackNeverOnlyChangesTheCompactColumn()
    {
        // Every Back a split plans removes an entry or a column, so it shows a change at any window width.
        foreach (NavigationState state in new[]
        {
            Root(Split(SplitColumn.Secondary, Message("m1", "attachment"))),
            Root(Split(SplitColumn.Secondary, Message("m1"))),
            Root(new SplitNode(
                "mail",
                new StackNode("mailboxes", [Entry("accounts"), Entry("inbox")]),
                Message("m1"),
                SplitColumn.Primary))
        })
        {
            NavigationPlan plan = Back(state);

            Assert.True(
                RouteEntryLeaseRegistry.EntryIds(plan.TargetState).Count <
                RouteEntryLeaseRegistry.EntryIds(state).Count);
        }
    }

    [Fact]
    public async Task ARouterBackFromTheSecondaryRootReportsThePrimaryColumnsTop()
    {
        await using IRouterNavigator router = Router(Root(Split(SplitColumn.Secondary, Message("m1"))));

        BackNavigationResult result = await router.BackAsync();

        Assert.Equal(BackNavigationStatus.Completed, result.Status);
        Assert.Null(Assert.IsType<SplitNode>(router.CurrentState.ActiveWindow?.Root).Secondary);
        Assert.Equal(new MailRoute("inbox"), result.NavigationResult?.Route);
        Assert.Equal(new MailRoute("inbox"), router.History.Current?.Route);
    }

    [Fact]
    public async Task AReconciliationReportsTheTopOfTheCompactColumn()
    {
        NavigationState state = Root(Split(SplitColumn.Secondary, Message("m1", "attachment")));
        await using IRouterNavigator router = Router(state);
        NavigationState popped = Root(Split(SplitColumn.Secondary, Message("m1")));

        NavigationResult result = await router.ReconcileAsync(
            new NavigationReconciliation(state, popped, NavigationReconciliationSource.HostBack));

        Assert.Equal(new MailRoute("m1"), result.Route);
    }

    [Fact]
    public void BackPopsThePrimaryColumnAndClearsTheSecondaryColumn()
    {
        // The list is on top with a message still selected; leaving that list leaves the message's context too.
        NavigationState state = Root(new SplitNode(
            "mail",
            new StackNode("mailboxes", [Entry("accounts"), Entry("inbox")]),
            Message("m1"),
            SplitColumn.Primary));

        NavigationPlan plan = Back(state);

        SplitNode split = RootSplit(plan);
        Assert.Equal(["accounts"], EntryIds(split.Primary));
        Assert.Null(split.Secondary);
        Assert.Equal(SplitColumn.Primary, split.CompactColumn);
    }

    [Fact]
    public void ADefaultBranchFallbackInThePrimaryColumnClearsTheSecondaryColumn()
    {
        var folders = new BranchHostNode(
            "folders",
            [
                new NavigationBranch("inbox", "Inbox", new StackNode("inbox", [Entry("inbox")])),
                new NavigationBranch("archive", "Archive", new StackNode("archive", [Entry("archive")]))
            ],
            "archive",
            "inbox");
        NavigationState state = Root(new SplitNode("mail", folders, Message("m1"), SplitColumn.Primary));

        SplitNode split = RootSplit(Back(state));

        Assert.Equal("inbox", Assert.IsType<BranchHostNode>(split.Primary).SelectedBranchId);
        Assert.Null(split.Secondary);
        Assert.Equal(SplitColumn.Primary, split.CompactColumn);
    }

    [Fact]
    public void ABranchHostInTheSecondaryColumnFallsBackToItsDefaultBranchFirst()
    {
        var message = new BranchHostNode(
            "message-tabs",
            [
                new NavigationBranch("body", "Body", new StackNode("body", [Entry("body")])),
                new NavigationBranch("headers", "Headers", new StackNode("headers", [Entry("headers")]))
            ],
            "headers",
            "body");
        NavigationState state = Root(new SplitNode("mail", Mailboxes(), message));

        NavigationPlan plan = Back(state);

        SplitNode split = RootSplit(plan);
        Assert.Equal("body", Assert.IsType<BranchHostNode>(split.Secondary).SelectedBranchId);
        Assert.Equal(SplitColumn.Secondary, split.CompactColumn);
        Assert.Equal("Back navigation will return to the default branch.", plan.Reason);

        // Without the fallback, Back from the secondary root closes the secondary column.
        NavigationPlan? noFallback = new DefaultBackNavigator(
                new BackNavigationOptions { ReturnToDefaultBranchBeforeLeaving = false })
            .CreateBackPlan(state);
        Assert.NotNull(noFallback);
        Assert.Null(RootSplit(noFallback).Secondary);
    }

    [Fact]
    public void ASplitAtItsPrimaryRootInsideANonDefaultTabFallsBackToTheDefaultBranch()
    {
        var tabs = new BranchHostNode(
            "tabs",
            [
                new NavigationBranch("home", "Home", new StackNode("home", [Entry("home")])),
                new NavigationBranch("mail", "Mail", Split(SplitColumn.Primary, Message("m1")))
            ],
            "mail",
            "home");

        NavigationPlan plan = Back(Root(tabs));

        BranchHostNode backTabs = Assert.IsType<BranchHostNode>(RootOf(plan));
        Assert.Equal("home", backTabs.SelectedBranchId);
        Assert.Equal(tabs.Branches, backTabs.Branches);
    }

    [Fact]
    public void BackHasNothingToDoAtThePrimaryRoot()
    {
        Assert.Null(new DefaultBackNavigator().CreateBackPlan(Root(Split(SplitColumn.Primary, Message("m1")))));
        Assert.Null(new DefaultBackNavigator().CreateBackPlan(Root(Split(SplitColumn.Primary))));
    }

    [Fact]
    public void BackReachesASplitInsideTheSelectedBranch()
    {
        var host = new BranchHostNode(
            "tabs",
            [
                new NavigationBranch("mail", "Mail", Split(SplitColumn.Secondary, Message("m1"))),
                new NavigationBranch("settings", "Settings", new StackNode("settings", [Entry("settings")]))
            ],
            "mail");

        NavigationPlan plan = Back(Root(host));

        BranchHostNode backHost = Assert.IsType<BranchHostNode>(RootOf(plan));
        NavigationBranch mail = Assert.Single(backHost.Branches, static branch => branch.Id == "mail");
        Assert.Equal(SplitColumn.Primary, Assert.IsType<SplitNode>(mail.Content).CompactColumn);
    }

    [Fact]
    public void BackDismissesAModalOverASplitFirst()
    {
        SplitNode split = Split(SplitColumn.Secondary, Message("m1", "attachment"));
        var state = new NavigationState(
            [new WindowNode("main", split, [new ModalNode("compose", Entry("compose"))])],
            "main");

        NavigationPlan plan = Back(state);

        WindowNode window = plan.TargetState.ActiveWindow ?? throw new InvalidOperationException("No window.");
        Assert.Empty(window.Modals);
        Assert.Equal(split, window.Root);
    }

    [Fact]
    public void TopRouteIsWhatTheCollapsedSplitShowsOnTop()
    {
        SplitNode split = Split(SplitColumn.Secondary, Message("m1", "attachment"));

        Assert.Equal(new MailRoute("attachment"), PresentedRouteTraversal.FindTopRoute(split));
        Assert.Equal(
            new MailRoute("inbox"),
            PresentedRouteTraversal.FindTopRoute(split with { CompactColumn = SplitColumn.Primary }));
        Assert.Equal(new MailRoute("inbox"), PresentedRouteTraversal.FindTopRoute(Split(SplitColumn.Primary)));
        Assert.Equal(
            new MailRoute("inbox"),
            PresentedRouteTraversal.FindTopRoute(Split(SplitColumn.Secondary, new StackNode("message", []))));
    }

    [Fact]
    public void LeasesSeeEntriesInBothColumnsWhicheverIsShown()
    {
        NavigationState state = Root(Split(SplitColumn.Primary, Message("m1", "attachment")));

        Assert.Equal(
            new HashSet<string>(["inbox", "m1", "attachment"], StringComparer.Ordinal),
            RouteEntryLeaseRegistry.EntryIds(state));
    }

    [Fact]
    public void LeasesRejectAnEntryIdRepeatedAcrossColumns()
    {
        NavigationState state = Root(Split(SplitColumn.Primary, Message("inbox")));

        AppNavigationConfigurationException exception = Assert.Throws<AppNavigationConfigurationException>(() =>
            RouteEntryLeaseRegistry.ValidateUniqueEntryIds(state, "Test"));
        Assert.Contains("'inbox'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeTopologyRefusesASplitRoot()
    {
        var topology = new NavigationTopology("main", "mailboxes");

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() =>
            topology.Match(Root(Split(SplitColumn.Primary)), new NavigationTopologySupport(maxStackEntries: 4)));
        Assert.Contains("split 'mail'", exception.Message, StringComparison.Ordinal);
    }

    private static NavigationPlan Back(NavigationState state)
    {
        NavigationPlan? plan = new DefaultBackNavigator().CreateBackPlan(state);
        Assert.NotNull(plan);
        Assert.Equal(NavigationPlanKind.Back, plan.Kind);
        NavigationStateValidator.ValidatePlan(plan, "Back");
        return plan;
    }

    private static IRouterNavigator Router(NavigationState state) => RouterNavigatorFactory.Create(
        TestRoutes.CreateTable(),
        TestNavigationPlanner.EchoStack(),
        new RecordingNavigationPresenter(),
        new RouterNavigatorFactoryOptions { InitialState = state });

    private static NavigationNode? RootOf(NavigationPlan plan) => plan.TargetState.ActiveWindow?.Root;

    private static SplitNode RootSplit(NavigationPlan plan) => Assert.IsType<SplitNode>(RootOf(plan));

    private static string[] EntryIds(NavigationNode? node) =>
        Assert.IsType<StackNode>(node).Entries.Select(static entry => entry.Id).ToArray();

    private static NavigationState Root(NavigationNode root) =>
        new([new WindowNode("main", root)], "main");

    private static SplitNode Split(SplitColumn compactColumn, NavigationNode? secondary = null) =>
        new("mail", Mailboxes(), secondary, compactColumn);

    private static StackNode Mailboxes() => new("mailboxes", [Entry("inbox")]);

    private static StackNode Message(params string[] entryIds) =>
        new("message", entryIds.Select(Entry).ToArray());

    private static RouteEntry Entry(string id) => new(id, new MailRoute(id));

    private sealed record MailRoute(string Value) : AppRoute;
}

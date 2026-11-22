using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
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
    public void TopRouteFollowsTheLayout()
    {
        SplitNode listOnTop = Split(SplitColumn.Primary, Message("m1", "attachment"));

        Assert.Equal(new MailRoute("inbox"), PresentedRouteTraversal.FindTopRoute(listOnTop, SplitLayout.Collapsed));
        Assert.Equal(
            new MailRoute("attachment"),
            PresentedRouteTraversal.FindTopRoute(listOnTop, SplitLayout.SideBySide));
        Assert.Equal(
            new MailRoute("inbox"),
            PresentedRouteTraversal.FindTopRoute(Split(SplitColumn.Primary), SplitLayout.SideBySide));
    }

    [Fact]
    public async Task BackDefaultsToTheLayoutReportedForItsWindow()
    {
        NavigationState state = Root(Split(SplitColumn.Secondary, Message("m1")));
        await using IRouterNavigator router = Router(state);

        router.ReportSplitLayout("main", SplitLayout.SideBySide);
        BackNavigationResult sideBySide = await router.BackAsync();
        Assert.Equal(BackNavigationStatus.Unhandled, sideBySide.Status);
        Assert.Same(state, router.CurrentState);

        router.ReportSplitLayout("main", SplitLayout.Collapsed);
        BackNavigationResult collapsed = await router.BackAsync();
        Assert.Equal(BackNavigationStatus.Completed, collapsed.Status);
        Assert.Equal(SplitColumn.Primary, RootSplitOf(router.CurrentState, "main").CompactColumn);
    }

    [Fact]
    public async Task EachWindowKeepsItsOwnLayout()
    {
        var state = new NavigationState(
            [
                new WindowNode("main", Split(SplitColumn.Secondary, Message("m1"))),
                new WindowNode(
                    "other",
                    new SplitNode(
                        "other-mail",
                        new StackNode("other-mailboxes", [Entry("other-inbox")]),
                        new StackNode("other-message", [Entry("o1")])))
            ],
            "main");
        await using IRouterNavigator router = Router(state);
        router.ReportSplitLayout("main", SplitLayout.SideBySide);

        Assert.Equal(BackNavigationStatus.Unhandled, (await router.BackAsync("main")).Status);
        Assert.Equal(BackNavigationStatus.Completed, (await router.BackAsync("other")).Status);
        Assert.Equal(SplitColumn.Secondary, RootSplitOf(router.CurrentState, "main").CompactColumn);
        Assert.Equal(SplitColumn.Primary, RootSplitOf(router.CurrentState, "other").CompactColumn);
    }

    [Fact]
    public async Task ARequestCanOverrideTheReportedLayout()
    {
        NavigationState state = Root(Split(SplitColumn.Secondary, Message("m1")));
        await using IRouterNavigator router = Router(state);
        router.ReportSplitLayout("main", SplitLayout.SideBySide);

        BackNavigationResult result = await router.BackAsync(
            new BackNavigationRequest { SplitLayout = SplitLayout.Collapsed });

        Assert.Equal(BackNavigationStatus.Completed, result.Status);
        Assert.Equal(SplitColumn.Primary, RootSplitOf(router.CurrentState, "main").CompactColumn);
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackNavigationRequest { SplitLayout = (SplitLayout)9 });
    }

    [Fact]
    public async Task ResultsAndHistoryReportTheRouteTheLayoutShows()
    {
        // The list is on top when collapsed, beside a default message shown when wide.
        var planner = new TestNavigationPlanner((_, _) => ValueTask.FromResult(new NavigationPlan(
            Root(Split(SplitColumn.Primary, Message("welcome"))))));
        await using IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            planner,
            new RecordingNavigationPresenter());

        NavigationResult collapsed = await router.NavigateAsync(new TestRoutes.StoreRoute("a"));
        Assert.Equal(new MailRoute("inbox"), collapsed.Route);

        router.ReportSplitLayout("main", SplitLayout.SideBySide);
        NavigationResult sideBySide = await router.NavigateAsync(new TestRoutes.StoreRoute("b"));
        Assert.Equal(new MailRoute("welcome"), sideBySide.Route);
        Assert.Equal(new MailRoute("welcome"), router.History.Current?.Route);
    }

    [Fact]
    public async Task ReportingALayoutChangesNoStateOrHistoryAndIsADiagnostic()
    {
        var diagnostics = new NavigationDiagnostics(
            options: new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full });
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        NavigationState state = Root(Split(SplitColumn.Secondary, Message("m1")));
        await using IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions { InitialState = state, Diagnostics = diagnostics });
        var history = router.History;

        router.ReportSplitLayout("main", SplitLayout.SideBySide);
        router.ReportSplitLayout("main", SplitLayout.SideBySide);
        router.ReportSplitLayout("main", SplitLayout.Collapsed);

        Assert.Same(state, router.CurrentState);
        Assert.Same(history, router.History);
        Assert.Equal(
            ["SideBySide", "Collapsed"],
            events.Where(static e => e.Kind == NavigationDiagnosticEventKind.SplitLayoutReported)
                .Select(static e => e.Data[NavigationDiagnosticDataKeys.SplitLayout]));
        Assert.Throws<ArgumentException>(() => router.ReportSplitLayout(" ", SplitLayout.SideBySide));
        Assert.Throws<ArgumentOutOfRangeException>(() => router.ReportSplitLayout("main", (SplitLayout)9));

        await router.BackAsync();
        NavigationDiagnosticEvent evaluated =
            Assert.Single(events, static e => e.Kind == NavigationDiagnosticEventKind.BackEvaluated);
        Assert.Equal("Collapsed", evaluated.Data[NavigationDiagnosticDataKeys.SplitLayout]);
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
    public async Task ALayoutIsForgottenOnceACommitRemovesItsWindow()
    {
        var planner = new TestNavigationPlanner((context, _) =>
        {
            var route = (TestRoutes.StoreRoute)context.Route;
            var main = new WindowNode("main", new StackNode("main-stack", [Entry("home")]));
            var detail = new WindowNode("detail", Split(SplitColumn.Secondary, Message("m1")));
            return ValueTask.FromResult(new NavigationPlan(route.StoreId == "both"
                ? new NavigationState([main, detail], "main")
                : new NavigationState([main], "main")));
        });
        await using IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            planner,
            new RecordingNavigationPresenter());
        await router.NavigateAsync(new TestRoutes.StoreRoute("both"));
        router.ReportSplitLayout("detail", SplitLayout.SideBySide);
        Assert.Equal(BackNavigationStatus.Unhandled, (await router.BackAsync("detail")).Status);

        await router.NavigateAsync(new TestRoutes.StoreRoute("main"));
        await router.NavigateAsync(new TestRoutes.StoreRoute("both"));

        Assert.Equal(BackNavigationStatus.Completed, (await router.BackAsync("detail")).Status);
        Assert.Equal(SplitColumn.Primary, RootSplitOf(router.CurrentState, "detail").CompactColumn);
    }

    [Fact]
    public async Task ALayoutReportedBeforeItsWindowExistsAppliesWhenTheNextCommitAddsIt()
    {
        var planner = new TestNavigationPlanner((_, _) =>
            ValueTask.FromResult(new NavigationPlan(Root(Split(SplitColumn.Secondary, Message("m1"))))));
        await using IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            planner,
            new RecordingNavigationPresenter());

        router.ReportSplitLayout("main", SplitLayout.SideBySide);
        await router.NavigateAsync(new TestRoutes.StoreRoute("a"));

        Assert.Equal(BackNavigationStatus.Unhandled, (await router.BackAsync()).Status);
    }

    [Fact]
    public async Task AReportAfterDisposalIsIgnored()
    {
        var diagnostics = new NavigationDiagnostics(
            options: new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full });
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions { Diagnostics = diagnostics });
        await router.DisposeAsync();

        router.ReportSplitLayout("main", SplitLayout.SideBySide);

        Assert.DoesNotContain(events, static e => e.Kind == NavigationDiagnosticEventKind.SplitLayoutReported);
    }

    [Fact]
    public async Task AQueuedBackUsesALayoutReportedWhileItWaited()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presenter = new RecordingNavigationPresenter
        {
            OnApplyAsync = async (_, _, _) =>
            {
                started.TrySetResult();
                await release.Task;
            }
        };
        var planner = new TestNavigationPlanner((_, _) =>
            ValueTask.FromResult(new NavigationPlan(Root(Split(SplitColumn.Secondary, Message("m1"))))));
        await using IRouterNavigator router = RouterNavigatorFactory.Create(TestRoutes.CreateTable(), planner, presenter);

        Task<NavigationResult> navigation = router.NavigateAsync(new TestRoutes.StoreRoute("a")).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<BackNavigationResult> back = router.BackAsync().AsTask();
        router.ReportSplitLayout("main", SplitLayout.SideBySide);
        release.SetResult();
        await navigation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(BackNavigationStatus.Unhandled, (await back.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal(SplitColumn.Secondary, RootSplitOf(router.CurrentState, "main").CompactColumn);
    }

    [Fact]
    public async Task AReconciliationReportsTheRouteTheLayoutShows()
    {
        NavigationState state = Root(Split(SplitColumn.Primary, Message("m1", "attachment")));
        await using IRouterNavigator router = Router(state);
        router.ReportSplitLayout("main", SplitLayout.SideBySide);
        NavigationState popped = Root(Split(SplitColumn.Primary, Message("m1")));

        NavigationResult result = await router.ReconcileAsync(
            new NavigationReconciliation(state, popped, NavigationReconciliationSource.HostBack));

        Assert.Equal(new MailRoute("m1"), result.Route);
    }

    [Fact]
    public async Task BackRecordsTheLayoutOnlyInAWindowWithASplit()
    {
        var diagnostics = new NavigationDiagnostics(
            options: new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full });
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        await using IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions
            {
                InitialState = Root(new StackNode("mailboxes", [Entry("accounts"), Entry("inbox")])),
                Diagnostics = diagnostics
            });

        await router.BackAsync();

        NavigationDiagnosticEvent evaluated =
            Assert.Single(events, static e => e.Kind == NavigationDiagnosticEventKind.BackEvaluated);
        Assert.False(evaluated.Data.ContainsKey(NavigationDiagnosticDataKeys.SplitLayout));
    }

    [Fact]
    public async Task AFlipOnlyCollapsedBackReportsThePrimaryColumnsTop()
    {
        await using IRouterNavigator router = Router(Root(Split(SplitColumn.Secondary, Message("m1"))));

        BackNavigationResult result = await router.BackAsync();

        Assert.Equal(BackNavigationStatus.Completed, result.Status);
        Assert.Equal(new MailRoute("inbox"), result.NavigationResult?.Route);
        Assert.Equal(new MailRoute("inbox"), router.History.Current?.Route);
    }

    [Fact]
    public async Task ABackThatLeavesItsWindowReportsTheRouteInTheOtherWindowsLayout()
    {
        var other = new WindowNode("other", Split(SplitColumn.Primary, Message("o1")));
        var state = new NavigationState(
            [new WindowNode("main", new StackNode("main-stack", [Entry("home")])), other],
            "main");
        // A custom navigator that closes the window and activates the other one.
        var closing = new ClosingBackNavigator(new NavigationState([other], "other"));
        await using IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions { InitialState = state, BackNavigator = closing });
        router.ReportSplitLayout("main", SplitLayout.Collapsed);
        router.ReportSplitLayout("other", SplitLayout.SideBySide);

        BackNavigationResult result = await router.BackAsync("main");

        Assert.Equal(new MailRoute("o1"), result.NavigationResult?.Route);
    }

    [Fact]
    public async Task AReportAdmittedBeforeShutdownWritesItsDiagnosticBeforeDisposalCompletes()
    {
        var diagnostics = new NavigationDiagnostics(
            options: new NavigationDiagnosticsOptions { DataMode = NavigationDiagnosticDataMode.Full });
        var inObserver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObserver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = 0;
        diagnostics.EventWritten += (_, diagnosticEvent) =>
        {
            if (diagnosticEvent.Kind != NavigationDiagnosticEventKind.SplitLayoutReported)
                return;
            inObserver.TrySetResult();
            releaseObserver.Task.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Increment(ref reported);
        };
        IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions { Diagnostics = diagnostics });

        // The report is admitted, then held inside its diagnostic while disposal starts.
        Task report = Task.Run(() => router.ReportSplitLayout("main", SplitLayout.SideBySide));
        await inObserver.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task disposal = router.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(disposal.IsCompleted);

        releaseObserver.SetResult();
        await report.WaitAsync(TimeSpan.FromSeconds(10));
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Volatile.Read(ref reported));
    }

    [Fact]
    public async Task ABackCommitThatRemovesAWindowForgetsItsLayout()
    {
        var closing = new ClosingBackNavigator(new NavigationState([MainWindow()], "main"));
        await using IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TwoWindowPlanner(),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions { BackNavigator = closing });
        await router.NavigateAsync(new TestRoutes.StoreRoute("both"));
        router.ReportSplitLayout("detail", SplitLayout.SideBySide);
        Assert.Equal(
            new MailRoute("m1"),
            (await router.NavigateAsync(new TestRoutes.StoreRoute("both"))).Route);

        await router.BackAsync("detail");
        NavigationResult readded = await router.NavigateAsync(new TestRoutes.StoreRoute("both"));

        Assert.Equal(new MailRoute("inbox"), readded.Route);
    }

    [Fact]
    public async Task AReconciliationThatRemovesAWindowForgetsItsLayout()
    {
        await using IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TwoWindowPlanner(),
            new RecordingNavigationPresenter());
        await router.NavigateAsync(new TestRoutes.StoreRoute("both"));
        router.ReportSplitLayout("detail", SplitLayout.SideBySide);

        await router.ReconcileAsync(new NavigationReconciliation(
            router.CurrentState,
            new NavigationState([MainWindow()], "main"),
            NavigationReconciliationSource.HostBack));
        NavigationResult readded = await router.NavigateAsync(new TestRoutes.StoreRoute("both"));

        Assert.Equal(new MailRoute("inbox"), readded.Route);
    }

    [Fact]
    public async Task TheLayoutSurvivesTheDefaultSafeDiagnosticsMode()
    {
        // Default options: Safe data mode, which keeps only structural values.
        var diagnostics = new NavigationDiagnostics();
        var events = new List<NavigationDiagnosticEvent>();
        diagnostics.EventWritten += (_, diagnosticEvent) => events.Add(diagnosticEvent);
        await using IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions
            {
                InitialState = Root(Split(SplitColumn.Secondary, Message("m1", "attachment"))),
                Diagnostics = diagnostics
            });

        router.ReportSplitLayout("main", SplitLayout.SideBySide);
        await router.BackAsync();

        NavigationDiagnosticEvent reported =
            Assert.Single(events, static e => e.Kind == NavigationDiagnosticEventKind.SplitLayoutReported);
        NavigationDiagnosticEvent evaluated =
            Assert.Single(events, static e => e.Kind == NavigationDiagnosticEventKind.BackEvaluated);
        Assert.Equal("SideBySide", reported.Data[NavigationDiagnosticDataKeys.SplitLayout]);
        Assert.Equal("SideBySide", evaluated.Data[NavigationDiagnosticDataKeys.SplitLayout]);
        Assert.DoesNotContain(NavigationDiagnosticDataKeys.WindowId, reported.Data.Keys);
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
    public void BackFromTheSecondaryRootReturnsToThePrimaryColumn()
    {
        NavigationState state = Root(Split(SplitColumn.Secondary, Message("m1")));

        NavigationPlan plan = Back(state);

        SplitNode split = RootSplit(plan);
        Assert.Equal(SplitColumn.Primary, split.CompactColumn);
        Assert.Equal(["m1"], EntryIds(split.Secondary));
        Assert.Equal("Back navigation will return to the primary column.", plan.Reason);
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

        // Without the fallback, the secondary root returns to the primary column.
        NavigationPlan? noFallback = new DefaultBackNavigator(
                new BackNavigationOptions { ReturnToDefaultBranchBeforeLeaving = false })
            .CreateBackPlan(state);
        Assert.NotNull(noFallback);
        Assert.Equal(SplitColumn.Primary, RootSplit(noFallback).CompactColumn);
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
    public void SideBySideBackPopsTheSecondaryColumnWhicheverColumnIsOnTop()
    {
        foreach (SplitColumn compactColumn in new[] { SplitColumn.Primary, SplitColumn.Secondary })
        {
            NavigationPlan plan = SideBySideBack(Root(Split(compactColumn, Message("m1", "attachment"))));

            SplitNode split = RootSplit(plan);
            Assert.Equal(["m1"], EntryIds(split.Secondary));
            Assert.Equal(compactColumn, split.CompactColumn);
        }
    }

    [Fact]
    public void SideBySideBackAtTheSecondaryRootPopsThePrimaryColumnAndClearsTheSecondaryColumn()
    {
        NavigationState state = Root(new SplitNode(
            "mail",
            new StackNode("mailboxes", [Entry("accounts"), Entry("inbox")]),
            Message("m1")));

        SplitNode split = RootSplit(SideBySideBack(state));

        Assert.Equal(["accounts"], EntryIds(split.Primary));
        Assert.Null(split.Secondary);
        Assert.Equal(SplitColumn.Primary, split.CompactColumn);
    }

    [Fact]
    public void SideBySideBackNeverOnlyChangesTheCompactColumn()
    {
        var navigator = new DefaultBackNavigator();
        foreach (NavigationState state in new[]
        {
            Root(Split(SplitColumn.Secondary, Message("m1"))),
            Root(Split(SplitColumn.Primary, Message("m1"))),
            Root(Split(SplitColumn.Primary))
        })
        {
            var context = new BackNavigationContext(state, null, "operation") { SplitLayout = SplitLayout.SideBySide };
            Assert.Null(navigator.CreateBackPlan(context));
        }
    }

    [Fact]
    public async Task TheRouterCarriesTheLayoutFromTheRequestToTheBackNavigator()
    {
        NavigationState state = Root(Split(SplitColumn.Secondary, Message("m1")));
        await using IRouterNavigator router = RouterNavigatorFactory.Create(
            TestRoutes.CreateTable(),
            TestNavigationPlanner.EchoStack(),
            new RecordingNavigationPresenter(),
            new RouterNavigatorFactoryOptions { InitialState = state });

        BackNavigationResult sideBySide = await router.BackAsync(
            new BackNavigationRequest(source: BackNavigationSource.Host) { SplitLayout = SplitLayout.SideBySide });
        Assert.Equal(BackNavigationStatus.Unhandled, sideBySide.Status);
        Assert.Same(state, router.CurrentState);

        BackNavigationResult collapsed = await router.BackAsync(new BackNavigationRequest());
        Assert.Equal(BackNavigationStatus.Completed, collapsed.Status);
        Assert.Equal(
            SplitColumn.Primary,
            Assert.IsType<SplitNode>(router.CurrentState.ActiveWindow?.Root).CompactColumn);
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

    private static WindowNode MainWindow() => new("main", new StackNode("main-stack", [Entry("home")]));

    // "both" shows a main window and a detail window whose split keeps its list on top beside message m1, so the
    // detail route is m1 side by side and inbox collapsed; any other store shows only the main window.
    private static TestNavigationPlanner TwoWindowPlanner() => new((context, _) =>
        ValueTask.FromResult(new NavigationPlan(((TestRoutes.StoreRoute)context.Route).StoreId == "both"
            ? new NavigationState(
                [MainWindow(), new WindowNode("detail", Split(SplitColumn.Primary, Message("m1")))],
                "detail")
            : new NavigationState([MainWindow()], "main"))));

    private static IRouterNavigator Router(NavigationState state) => RouterNavigatorFactory.Create(
        TestRoutes.CreateTable(),
        TestNavigationPlanner.EchoStack(),
        new RecordingNavigationPresenter(),
        new RouterNavigatorFactoryOptions { InitialState = state });

    private static SplitNode RootSplitOf(NavigationState state, string windowId) =>
        Assert.IsType<SplitNode>(state.FindWindow(windowId)?.Root);

    private static NavigationPlan SideBySideBack(NavigationState state)
    {
        var context = new BackNavigationContext(state, null, "operation") { SplitLayout = SplitLayout.SideBySide };
        NavigationPlan? plan = new DefaultBackNavigator().CreateBackPlan(context);
        Assert.NotNull(plan);
        NavigationStateValidator.ValidatePlan(plan, "Back");
        return plan;
    }

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

    private sealed class ClosingBackNavigator(NavigationState target) : IBackNavigator
    {
        public NavigationPlan? CreateBackPlan(BackNavigationContext context) =>
            new(target, NavigationPlanKind.Back, "Close the window.");
    }
}

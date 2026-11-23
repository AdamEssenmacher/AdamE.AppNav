using System.Threading.Channels;
using AdamE.AppNav;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Oahu;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using AppNav.SwiftUI.Prototype;

internal static class PresenterChecks
{
    private sealed class Surface : IOahuNavigationSurface
    {
        private readonly Channel<OahuNavigationTarget> frames = Channel.CreateUnbounded<OahuNavigationTarget>();
        public bool FailAccept { get; set; }
        public bool PresentsSplits { get; init; }
        public Func<Task>? BeforeAccept { get; set; }
        public Func<OahuNavigationTarget, Task>? OnSettle { get; set; }
        public List<OahuNavigationTarget> Accepted { get; } = [];
        public Task ShowAsync(OahuNavigationTarget target, CancellationToken token) =>
            frames.Writer.WriteAsync(target, token).AsTask();
        public Task SettleAsync(OahuNavigationTarget target, CancellationToken token) =>
            OnSettle?.Invoke(target) ?? Task.CompletedTask;

        public async Task AcceptAsync(OahuNavigationTarget target, Func<Action, bool> applyIfCurrent,
            CancellationToken token)
        {
            if (BeforeAccept is { } beforeAccept)
                await beforeAccept().WaitAsync(token);
            applyIfCurrent(() =>
            {
                if (FailAccept)
                {
                    FailAccept = false;
                    throw new InvalidOperationException("injected mount release failure");
                }
                Accepted.Add(target);
            });
        }
        public Task NextFailsIfPublished() => frames.Reader.TryPeek(out _) ? Task.FromException(new Exception("Unexpected projection")) : Task.CompletedTask;
        public async Task<OahuNavigationTarget> Next() => await frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Task Ack(OahuNavigationPresenter presenter, long epoch, OahuNavigationTarget frame) =>
        presenter.ObserveAsync(epoch, frame.Revision, frame.Branch,
            frame.Branches.TryGetValue(frame.Branch, out var stack) ? stack.Select(x => x.Id).ToArray() : [],
            frame.ModalId, frame.ModalEntries.Select(x => x.Id).ToArray());

    private static void Assert(bool condition, string why)
    {
        if (!condition) throw new Exception(why);
    }

    public static async Task RunAsync()
    {
        var presenter = ProbeRouter.CreatePresenter();
        await using var router = new ProbeRouter(presenter);
        var first = new Surface();
        var e1 = presenter.Attach(first);
        var start = router.StartAsync();
        await Ack(presenter, e1, await first.Next());
        await start;
        var link = router.DeepLinkAsync();
        var abandoned = await first.Next();
        presenter.Detach(e1);
        var second = new Surface();
        var e2 = presenter.Attach(second);
        var rehosted = await second.Next();
        Assert(abandoned.Transition.Operation == "branch" && rehosted.Transition.Operation == "reattach", "Replay lost attachment boundary");
        await Ack(presenter, e1, abandoned);
        Assert(!link.IsCompleted && router.HistoryCount == 1, "Old activity committed a pending link");
        presenter.Detach(e1); // Late teardown cannot disconnect the replacement.
        await Ack(presenter, e2, rehosted);
        await link;
        Assert(router.HistoryCount == 2, "Expected one link on replacement surface");
        var refresh = router.Navigator.RefreshPresentationAsync().AsTask();
        var frame = await second.Next();
        Assert(frame.Transition.Operation == "refresh" && frame.Transition.SourceKey.Length == 0, "Refresh replayed original animation");
        await Ack(presenter, e2, frame);
        await refresh;
        Assert(router.HistoryCount == 2, "Refresh changed history");

        await presenter.ObserveAsync(e2, frame.Revision, "home", ["home"]);
        Assert(router.CurrentBranch == "catalog", "Changed branch plus unobserved prefix accepted");
        await presenter.ObserveAsync(e2, frame.Revision, "home", ["home", "home-detail"]);
        Assert(router.CurrentBranch == "home" && router.HistoryCount == 3, "Tab selection failed");
        Assert(router.EntriesIn("catalog").Length == 4, "Inactive path lost");

        var modal = router.ModalAsync();
        var sheet = await second.Next();
        Assert(sheet.ModalId == "editor" && sheet.ModalOwner?.Id == "editor-owner" &&
               sheet.ModalEntries.Select(x => x.Id).SequenceEqual(["edit", "review"]), "Modal projection changed topology");
        await presenter.ObserveAsync(e2, sheet.Revision, "home", ["home", "home-detail"]);
        Assert(!modal.IsCompleted, "Underlying branch acknowledged a missing sheet");
        Assert(sheet.Transition.Operation == "modalPresent", "Sheet did not receive modal transition");
        await Ack(presenter, e2, sheet);
        await modal;
        await presenter.ObserveAsync(e2, sheet.Revision, "home", ["home", "home-detail"], "editor", ["edit"]);
        var window = router.Navigator.CurrentState.ActiveWindow!;
        Assert(window.Modals.Single().Content is StackNode { Entries.Count: 1 } && router.HistoryCount == 5,
            "Modal pop discarded its owner or added the wrong history");
        var pushReview = router.PushReviewAsync();
        var review = await second.Next();
        Assert(review.ModalId == "editor" && review.ModalEntries.Select(x => x.Id).SequenceEqual(["edit", "review-again"]) &&
            review.Transition == new OahuPresentationTransition("push", "edit", "review-again", "review-cover", "edit", "review"),
            $"Review push lost its sheet stack or visual source: {review.Transition}");
        await Ack(presenter, e2, review);
        await pushReview;
        await presenter.ObserveAsync(e2, review.Revision, "home", ["home", "home-detail"]);
        Assert(router.Navigator.CurrentState.ActiveWindow!.Modals.Count == 0 && router.HistoryCount == 7,
            "Modal dismissal failed");
        await second.NextFailsIfPublished();

        // Failed cleanup after an observed change must repair the native path before the router unwinds.
        second.FailAccept = true;
        var rejected = presenter.ObserveAsync(e2, review.Revision, "home", ["home"]);
        var rollback = await second.Next();
        Assert(!rejected.IsCompleted && rollback.Revision != review.Revision, "Rollback reused an invalid native witness");
        Assert(rollback.Transition.Operation == "rollback" && rollback.Transition.SourceKey.Length == 0, "Recovery replayed visual origin");
        await Ack(presenter, e2, rollback);
        try
        {
            await rejected;
            throw new Exception("Cleanup failure was swallowed");
        }
        catch (InvalidOperationException e) when (e.Message.Contains("injected")) { }
        Assert(router.CurrentEntryIds.Length == 2 && router.HistoryCount == 7, "Failed cleanup changed history");
        await presenter.ObserveAsync(e2, rollback.Revision, "home", ["home"]);
        Assert(router.HistoryCount == 8, "Router failed to recover after rollback");

        await CheckRapidNativePops();
        await CheckRollbackSettlementFailure();
        await CheckTransitionContext();
        await CheckTopologies();
        await CheckSplits();
        Console.WriteLine("PASS: attachment epochs, recreation, tab selection, modal stack, rapid native pops, rollback, shape preflight");
    }

    private static async Task CheckRollbackSettlementFailure()
    {
        var diagnostics = new NavigationDiagnostics();
        var applyFailure = new TaskCompletionSource<NavigationDiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settleFailure = new TaskCompletionSource<NavigationDiagnosticEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        diagnostics.EventWritten += (_, entry) =>
        {
            if (entry.Kind == NavigationDiagnosticEventKind.PresentationFailed)
                applyFailure.TrySetResult(entry);
            if (entry.Kind == NavigationDiagnosticEventKind.PresentationSettlementFailed)
                settleFailure.TrySetResult(entry);
        };
        using var presenter = new OahuNavigationPresenter();
        await using var router = new ProbeRouter(presenter, diagnostics: diagnostics);
        var surface = new Surface
        {
            OnSettle = target => target.Transition.Operation == "rollback"
                ? Task.FromException(new ApplicationException("rollback cleanup failed")) : Task.CompletedTask
        };
        var epoch = presenter.Attach(surface);
        var start = router.StartAsync();
        await Ack(presenter, epoch, await surface.Next());
        await start;

        surface.FailAccept = true;
        var link = router.DeepLinkAsync();
        await Ack(presenter, epoch, await surface.Next());
        await Ack(presenter, epoch, await surface.Next());
        try
        {
            await link;
            throw new Exception("Failed apply unexpectedly succeeded");
        }
        catch (InvalidOperationException error) when (error.Message == "injected mount release failure") { }

        var timeout = TimeSpan.FromSeconds(5);
        var apply = await applyFailure.Task.WaitAsync(timeout);
        var settle = await settleFailure.Task.WaitAsync(timeout);
        Assert(apply.OperationId == settle.OperationId && router.HistoryCount == 1 && router.CurrentBranch == "home",
            "Rollback cleanup failed under a different operation identifier or rewrote history");
    }

    private static async Task CheckRapidNativePops()
    {
        var presenter = new OahuNavigationPresenter();
        await using var router = new ProbeRouter(presenter);
        var surface = new Surface();
        var epoch = presenter.Attach(surface);
        var start = router.StartAsync();
        await Ack(presenter, epoch, await surface.Next());
        await start;
        var link = router.DeepLinkAsync();
        var catalog = await surface.Next();
        await Ack(presenter, epoch, catalog);
        await link;
        surface.Accepted.Clear();

        var firstAcceptance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeAcceptance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        surface.BeforeAccept = () =>
        {
            firstAcceptance.TrySetResult();
            return resumeAcceptance.Task;
        };
        var initial = catalog.Branches[catalog.Branch].Select(entry => entry.Id).ToArray();
        var firstPop = initial[..^1];
        var secondPop = initial[..^2];
        var first = presenter.ObserveAsync(epoch, catalog.Revision, catalog.Branch, firstPop);
        await firstAcceptance.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = presenter.ObserveAsync(epoch, catalog.Revision, catalog.Branch, secondPop);
        resumeAcceptance.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        var accepted = surface.Accepted.Single();
        Assert(accepted.Branches[catalog.Branch].Select(entry => entry.Id).SequenceEqual(secondPop),
            "An earlier native pop republished an entry the user had already removed");
        Assert(router.HistoryCount == 4 && router.CurrentEntryIds.SequenceEqual(secondPop),
            "Rapid native pops did not commit both history entries in order");
        var previous = router.Navigator.History.Entries[^2].State.ActiveWindow!.Root as BranchHostNode;
        var previousStack = (StackNode)previous!.SelectedBranch!.Content;
        Assert(previousStack.Entries.Select(entry => entry.Id).SequenceEqual(firstPop),
            "The first native pop did not retain its intermediate history state");
        await surface.NextFailsIfPublished();
    }

    private static async Task CheckTransitionContext()
    {
        bool failScreenName = false;
        var presenter = new OahuNavigationPresenter(screenName: entry =>
            failScreenName ? throw new ApplicationException("screen name failed") : entry.Route.GetType().Name);
        await using var router = new ProbeRouter(presenter);
        var first = new Surface();
        var oldEpoch = presenter.Attach(first);
        var start = router.StartAsync();
        var home = await first.Next();
        Assert(home.Transition == new OahuPresentationTransition("initial", "", "home-detail", "", "", "Screen"),
            "Initial surface lacked the destination screen");
        await Ack(presenter, oldEpoch, home);
        await start;

        failScreenName = true;
        try
        {
            await router.OpenGameAsync();
            throw new Exception("Expected screen-name selection to fail");
        }
        catch (ApplicationException e) when (e.Message == "screen name failed") { }
        await first.NextFailsIfPublished();
        Assert(router.HistoryCount == 1, "Metadata selection failure changed history");
        failScreenName = false;

        var open = router.OpenGameAsync();
        var game = await first.Next();
        Assert(game.Transition == new OahuPresentationTransition("push", "home-detail", "game", "game-cover", "Screen", "OpenGame"),
            $"Tap lost its source: {game.Transition}");
        var replacement = new Surface();
        var epoch = presenter.Attach(replacement);
        var replay = await replacement.Next();
        Assert(replay.Revision == game.Revision &&
            replay.Transition == new OahuPresentationTransition("reattach", "", "game", "", "", "OpenGame"),
            $"Recreated host spent the old visual hint: {replay.Transition}");
        await Ack(presenter, oldEpoch, game);
        Assert(router.HistoryCount == 1 && !open.IsCompleted, "Old surface acknowledged shared push");
        await Ack(presenter, epoch, replay);
        await open;
        Assert(router.HistoryCount == 2 && router.Navigator.History.Current!.Request.Metadata.Count == 0,
            "Visual source leaked into request history");

        var gameRequest = router.Navigator.History.Current!.Request;
        var back = router.Navigator.BackAsync().AsTask();
        var pop = await replacement.Next();
        Assert(pop.Transition.Operation == "pop" && pop.Transition.SourceEntryId == "game" &&
            pop.Transition.DestinationEntryId == "home-detail" && pop.Transition.SourceKey == "", "Back borrowed the old source");
        await Ack(presenter, epoch, pop);
        await back;

        var fresh = router.Navigator.NavigateAsync(gameRequest).AsTask();
        var unhinted = await replacement.Next();
        Assert(unhinted.Transition.Operation == "push" && unhinted.Transition.SourceKey == "",
            "A later presentation of the same route reused the previous Game cover");
        await Ack(presenter, epoch, unhinted);
        await fresh;

        var beforeSheet = router.HistoryCount;
        var showSheet = router.OpenFadeSheetAsync();
        var sheet = await replacement.Next();
        Assert(sheet.Transition == new OahuPresentationTransition("modalPresent", "game", "fade-sheet", "", "OpenGame", "OpenFadeSheet"),
            "A source-free sheet needs its own presentation context");
        await Ack(presenter, epoch, sheet);
        await showSheet;
        var presented = router.Navigator.History.Current!.State.ActiveWindow!;
        Assert(presented.Modals.Single().Id == "fade-sheet" && router.HistoryCount == beforeSheet + 1,
            "AppNav history did not record the presented sheet");
        await presenter.ObserveAsync(epoch, sheet.Revision, sheet.Branch, ["home", "home-detail", "game"]);
        Assert(router.Navigator.History.Current!.State.ActiveWindow!.Modals.Count == 0 &&
            router.CurrentEntryIds.SequenceEqual(["home", "home-detail", "game"]) && router.HistoryCount == beforeSheet + 2,
            "Dismissing the sheet changed the underlying stack or recorded the wrong history");
    }

    private sealed record Page : AppRoute;

    private static async Task CheckTopologies()
    {
        using var presenter = new OahuNavigationPresenter();
        var host = new Surface();
        var epoch = presenter.Attach(host);
        var route = new Page();
        var context = new NavigationPresentationContext(RouterNavigationRequest.FromRoute(route, NavigationRequestSource.InAppCommand),
            route, NavigationState.Empty, "test");
        var root = new StackNode("navigation", [new("first", route), new("second", route)]);
        var window = new WindowNode("primary", root);
        var state = new NavigationState([window]);
        var apply = presenter.ApplyAsync(new(state), context).AsTask();
        var frame = await host.Next();
        Assert(!frame.IsBranchHost && frame.Branch == "navigation" && frame.Branches["navigation"].Count == 2,
            "Standalone stack projection");
        await Ack(presenter, epoch, frame);
        await apply;
        foreach (var unsupported in new[]
        {
            new NavigationState([window, new WindowNode("other", root)]),
            new NavigationState([window with { Modals = [new ModalNode("sheet", root.Entries[0])] }]),
            new NavigationState([window with { Root = new StackNode("empty", []) }]),
            // This surface does not present splits, as an iOS 16 SwiftUI host does not: a split is refused wherever it is.
            new NavigationState([window with { Root = new SplitNode("split", root) }]),
            new NavigationState([window with { Root = new BranchHostNode(
                "tabs", [new NavigationBranch("tab", "Tab", new SplitNode("split", root))], "tab") }]),
            new NavigationState([window with { Modals = [new ModalNode("sheet", new("sheet-entry", route),
                new SplitNode("split", new StackNode("sheet-stack", [new("sheet-root", route)])))] }])
        })
        {
            try
            {
                await presenter.ApplyAsync(new(unsupported), context);
                throw new Exception("Invalid plan reached native");
            }
            catch (NotSupportedException failure)
            {
                Assert(!ContainsSplit(unsupported) || failure.Message.Contains("split 'split'", StringComparison.OrdinalIgnoreCase),
                    "A split is refused as a split");
            }
            await host.NextFailsIfPublished();
        }

        static bool ContainsSplit(NavigationState state) => state.Windows.Any(window =>
            window.Root is SplitNode or BranchHostNode { Branches: [{ Content: SplitNode }] } ||
            window.Modals.Any(modal => modal.Content is SplitNode));
    }

    private static Task AckSplit(OahuNavigationPresenter presenter, long epoch, OahuNavigationTarget frame) =>
        presenter.ObserveAsync(epoch, frame.Revision, frame.Branch, frame.Branches[frame.Branch].Select(x => x.Id).ToArray(),
            frame.ModalId, frame.ModalEntries.Select(x => x.Id).ToArray(),
            frame.SecondaryEntries.Select(x => x.Id).ToArray(), frame.CompactColumn);

    private static async Task CheckSplits()
    {
        // A surface that does not present splits, such as a SwiftUI host before iOS 17, refuses one before staging anything.
        {
            var refusing = ProbeRouter.CreatePresenter();
            await using var refused = new ProbeRouter(refusing, split: true);
            var host = new Surface();
            refusing.Attach(host);
            try
            {
                await refused.StartAsync();
                throw new Exception("A split reached a surface that does not present splits");
            }
            catch (NotSupportedException failure)
            {
                Assert(failure.Message.Contains("does not present split nodes", StringComparison.Ordinal) &&
                    failure.Message.Contains("split 'games'", StringComparison.Ordinal), "Unclear refusal: " + failure.Message);
            }
            await host.NextFailsIfPublished();
            Assert(refused.HistoryCount == 0, "A refused split entered history");
        }

        var presenter = ProbeRouter.CreatePresenter();
        await using var router = new ProbeRouter(presenter, split: true);
        var splits = new Surface { PresentsSplits = true };
        var epoch = presenter.Attach(splits);
        var start = router.StartAsync();
        var home = await splits.Next();
        Assert(home.SplitId == "games" && home.Branch == "home-stack" && !home.IsBranchHost && home.SecondaryId is null &&
            home.SecondaryEntries.Count == 0 && home.CompactColumn == SplitColumn.Primary, "Split root projection");
        await AckSplit(presenter, epoch, home);
        await start;

        // Selecting from the Home row is a push from Home to the game's root, matched to the row's cover.
        var select = router.SelectGameAsync("game-a", "split-cover-a", default);
        var selected = await splits.Next();
        Assert(selected.SecondaryId == "game-a-stack" && selected.SecondaryEntries.Single().Id == "game-a" &&
            selected.CompactColumn == SplitColumn.Secondary, "Selected game projection");
        Assert(selected.Transition is { Operation: "push", SourceEntryId: "home", DestinationEntryId: "game-a",
            SourceKey: "split-cover-a", SourceScreen: "home", DestinationScreen: "game-a" }, "Select transition: " + selected.Transition);
        // A reading that shows Home on top does not settle a selection the collapsed split must show.
        await presenter.ObserveAsync(epoch, selected.Revision, "home-stack", ["home"], null, null, ["game-a"], SplitColumn.Primary);
        await presenter.ObserveAsync(epoch, selected.Revision, "home-stack", ["home"]);
        Assert(!select.IsCompleted && router.HistoryCount == 1, "A reading without the selected column settled the selection");
        await AckSplit(presenter, epoch, selected);
        await select;

        var push = router.PushGamePageAsync();
        var pushed = await splits.Next();
        Assert(pushed.SecondaryEntries.Select(x => x.Id).SequenceEqual(["game-a", "game-a-page-1"]) &&
            pushed.Transition is { Operation: "push", SourceEntryId: "game-a", DestinationEntryId: "game-a-page-1" },
            "Push inside the secondary column");
        await AckSplit(presenter, epoch, pushed);
        await push;

        // A native Back inside the game's stack.
        await presenter.ObserveAsync(epoch, pushed.Revision, "home-stack", ["home"], null, null, ["game-a"], SplitColumn.Secondary);
        Assert(router.Status.StartsWith("split[home]+game-a-stack[game-a]@secondary history=4", StringComparison.Ordinal) &&
            router.HistorySummary.EndsWith("HostReconciliation@split[home]+game-a-stack[game-a]@secondary", StringComparison.Ordinal),
            "Secondary pop: " + router.Status);
        // A reading whose secondary column vanished while it says the secondary is on top is not a native gesture.
        await presenter.ObserveAsync(epoch, pushed.Revision, "home-stack", ["home"], null, null, [], SplitColumn.Secondary);
        Assert(router.HistoryCount == 4, "An impossible split reading reconciled");
        // A collapsed window's Back from the game's root closes the secondary column.
        await presenter.ObserveAsync(epoch, pushed.Revision, "home-stack", ["home"], null, null, [], SplitColumn.Primary);
        Assert(router.Status.StartsWith("split[home]@primary history=5", StringComparison.Ordinal),
            "Closing the secondary column natively: " + router.Status);

        // A pop in the primary column while both columns show keeps the secondary column, exactly as observed.
        var selectB = router.SelectGameAsync("game-b", "", default);
        var gameB = await splits.Next();
        await AckSplit(presenter, epoch, gameB);
        await selectB;
        var homePush = router.PushHomePageAsync();
        var homeMore = await splits.Next();
        Assert(homeMore.Branches["home-stack"].Select(x => x.Id).SequenceEqual(["home", "home-more"]) &&
            homeMore.SecondaryId == "game-b-stack", "Primary push keeps the selection");
        await AckSplit(presenter, epoch, homeMore);
        await homePush;
        await presenter.ObserveAsync(epoch, homeMore.Revision, "home-stack", ["home"], null, null, ["game-b"], SplitColumn.Secondary);
        Assert(router.Status.StartsWith("split[home]+game-b-stack[game-b]@secondary", StringComparison.Ordinal),
            "Primary pop while tiled: " + router.Status);

        // Selecting another game replaces the secondary column.
        var selectA = router.SelectGameAsync("game-a", "split-cover-a", default);
        var switched = await splits.Next();
        Assert(switched.SecondaryId == "game-a-stack" && switched.Transition.Operation == "replace" &&
            switched.Transition.SourceKey == "split-cover-a", "Switching games: " + switched.Transition);
        await AckSplit(presenter, epoch, switched);
        await selectA;

        // An AppNav Back from the game's root closes the secondary column as a pop.
        var back = router.Navigator.BackAsync().AsTask();
        var closed = await splits.Next();
        Assert(closed.SecondaryId is null && closed.CompactColumn == SplitColumn.Primary &&
            closed.Transition is { Operation: "pop", SourceEntryId: "game-a", DestinationEntryId: "home" }, "Back from the game root");
        await AckSplit(presenter, epoch, closed);
        Assert((await back).Status == BackNavigationStatus.Completed, "Back did not complete");

        // A sheet over a selected game, dismissed natively, records the game it uncovers, not the sheet.
        var reselect = router.SelectGameAsync("game-b", "", default);
        var shown = await splits.Next();
        await AckSplit(presenter, epoch, shown);
        await reselect;
        var sheet = router.ModalAsync();
        var covered = await splits.Next();
        Assert(covered.ModalId == "editor" && covered.SplitId == "games", "Sheet over the split");
        await AckSplit(presenter, epoch, covered);
        await sheet;
        await presenter.ObserveAsync(epoch, covered.Revision, "home-stack", ["home"], null, null, ["game-b"], SplitColumn.Secondary);
        Assert(router.Status.StartsWith("split[home]+game-b-stack[game-b]@secondary history=", StringComparison.Ordinal) &&
            router.HistorySummary.EndsWith("HostReconciliation@split[home]+game-b-stack[game-b]@secondary", StringComparison.Ordinal),
            "Sheet dismissal over the split: " + router.Status);
        Assert(router.CurrentRoute == "game-b", "Sheet dismissal recorded the route " + router.CurrentRoute);

        // The Compose shell's native Back follows the planned Back. Over a default game with Home on top, a pop in
        // the primary column also closes the default, which one reading reports.
        var pushMore = router.PushHomePageAsync();
        var deeper = await splits.Next();
        await AckSplit(presenter, epoch, deeper);
        await pushMore;
        var selectDefault = router.SelectDefaultGameAsync("game-a");
        var byDefault = await splits.Next();
        Assert(byDefault.SecondaryId == "game-a-stack" && byDefault.CompactColumn == SplitColumn.Primary,
            "Default game projection");
        await AckSplit(presenter, epoch, byDefault);
        await selectDefault;
        await presenter.ObserveAsync(epoch, byDefault.Revision, "home-stack", ["home"], null, null, [], SplitColumn.Primary);
        Assert(router.Status.StartsWith("split[home]@primary history=", StringComparison.Ordinal),
            "Primary pop over a default game: " + router.Status);
        Assert(router.CurrentRoute == "home", "Primary pop over a default game recorded " + router.CurrentRoute);

        // A page pushed onto a default game puts the game's column on top, so one pane shows the page.
        var selectAgain = router.SelectDefaultGameAsync("game-a");
        var defaultAgain = await splits.Next();
        await AckSplit(presenter, epoch, defaultAgain);
        await selectAgain;
        var pagePush = router.PushGamePageAsync();
        var defaultPage = await splits.Next();
        Assert(defaultPage.CompactColumn == SplitColumn.Secondary && defaultPage.SecondaryEntries.Count == 2,
            "A page pushed onto a default game stays under Home");
        await AckSplit(presenter, epoch, defaultPage);
        await pagePush;

        // Shapes no surface presents fail before the presenter waits for a host.
        {
            using var hostless = new OahuNavigationPresenter();
            var page = new Page();
            var request = new NavigationPresentationContext(RouterNavigationRequest.FromRoute(page, NavigationRequestSource.InAppCommand),
                page, NavigationState.Empty, "test");
            var column = new StackNode("column", [new("column-root", page)]);
            var nested = new NavigationState([new WindowNode("primary", new BranchHostNode("sections",
                [new NavigationBranch("section", "Section", new SplitNode("nested", column))], "section"))]);
            var refusal = hostless.ApplyAsync(new(nested), request).AsTask();
            Assert(refusal.IsCompleted, "An unsupported split waited for a host");
            try
            {
                await refusal;
                throw new Exception("A nested split reached a hostless presenter");
            }
            catch (NotSupportedException failure)
            {
                Assert(failure.Message.Contains("inside branch 'section'", StringComparison.Ordinal), "Unclear refusal: " + failure.Message);
            }
        }

        // Shapes a split-capable surface still refuses.
        var route = new Page();
        var context = new NavigationPresentationContext(RouterNavigationRequest.FromRoute(route, NavigationRequestSource.InAppCommand),
            route, NavigationState.Empty, "test");
        var stack = new StackNode("column", [new("column-root", route)]);
        var tabs = new BranchHostNode("tabs", [new NavigationBranch("tab", "Tab", stack)], "tab");
        var window = new WindowNode("primary", stack);
        foreach (var (unsupported, expected) in new[]
        {
            (new NavigationState([window with { Root = new BranchHostNode("sections",
                [new NavigationBranch("section", "Section", new SplitNode("nested", stack))], "section") }]), "inside branch 'section'"),
            (new NavigationState([window with { Modals = [new ModalNode("sheet", new("sheet-entry", route),
                new SplitNode("nested", new StackNode("sheet-stack", [new("sheet-root", route)])))] }]), "inside modal 'sheet'"),
            (new NavigationState([window with { Root = new SplitNode("nested", tabs) }]), "branch host 'tabs' in a column"),
            (new NavigationState([window with { Root = new SplitNode("nested", stack,
                new BranchHostNode("detail-tabs", [new NavigationBranch("detail", "Detail", new StackNode("detail", [new("detail-root", route)]))], "detail")) }]),
                "branch host 'detail-tabs' in a column")
        })
        {
            using var capable = new OahuNavigationPresenter();
            var host = new Surface { PresentsSplits = true };
            capable.Attach(host);
            try
            {
                await capable.ApplyAsync(new(unsupported), context);
                throw new Exception("Unsupported split reached native: " + expected);
            }
            catch (NotSupportedException failure)
            {
                Assert(failure.Message.Contains(expected, StringComparison.Ordinal), "Unclear refusal: " + failure.Message);
            }
            await host.NextFailsIfPublished();
        }
        Console.WriteLine("PASS: split projection, column reconciliation, collapsed close, primary pop, game switch, Back, sheet, default game and refusals");
    }
}

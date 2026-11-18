using System.Threading.Channels;
using AdamE.AppNav;
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
        public Task ShowAsync(OahuNavigationTarget target, CancellationToken token) =>
            frames.Writer.WriteAsync(target, token).AsTask();
        public Task AcceptAsync(OahuNavigationTarget target, CancellationToken token)
        {
            if (FailAccept)
            {
                FailAccept = false;
                throw new InvalidOperationException("injected mount release failure");
            }
            return Task.CompletedTask;
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
        var presenter = new OahuNavigationPresenter();
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
        await presenter.ObserveAsync(e2, sheet.Revision, "home", ["home", "home-detail"]);
        Assert(router.Navigator.CurrentState.ActiveWindow!.Modals.Count == 0 && router.HistoryCount == 6,
            "Modal dismissal failed");
        await second.NextFailsIfPublished();

        // Failed cleanup after an observed change must repair the native path before the router unwinds.
        second.FailAccept = true;
        var rejected = presenter.ObserveAsync(e2, sheet.Revision, "home", ["home"]);
        var rollback = await second.Next();
        Assert(!rejected.IsCompleted && rollback.Revision != sheet.Revision, "Rollback reused an invalid native witness");
        Assert(rollback.Transition.Operation == "rollback" && rollback.Transition.SourceKey.Length == 0, "Recovery replayed visual origin");
        await Ack(presenter, e2, rollback);
        try
        {
            await rejected;
            throw new Exception("Cleanup failure was swallowed");
        }
        catch (InvalidOperationException e) when (e.Message.Contains("injected")) { }
        Assert(router.CurrentEntryIds.Length == 2 && router.HistoryCount == 6, "Failed cleanup changed history");
        await presenter.ObserveAsync(e2, rollback.Revision, "home", ["home"]);
        Assert(router.HistoryCount == 7, "Router failed to recover after rollback");

        await CheckTransitionContext();
        await CheckTopologies();
        Console.WriteLine("PASS: attachment epochs, recreation, tab selection, modal stack, rollback, shape preflight");
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
            new NavigationState([window with { Root = new StackNode("empty", []) }])
        })
        {
            try
            {
                await presenter.ApplyAsync(new(unsupported), context);
                throw new Exception("Invalid plan reached native");
            }
            catch (NotSupportedException) { }
            await host.NextFailsIfPublished();
        }
    }
}

using AdamE.AppNav;
using AdamE.AppNav.Back;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Leases;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Oahu;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;

namespace AppNav.SwiftUI.Prototype;

// The simulator app's routes and instrumentation. The presenter itself has no dependency on this project.
public sealed record SurfaceTarget(long Revision, string Branch, IReadOnlyDictionary<string, string[]> Branches,
    OahuPresentationTransition Transition)
{
    public string[] EntryIds => Branch.Length == 0 ? [] : Branches[Branch];
}

public sealed class ProbeRouter : IAsyncDisposable
{
    private readonly OahuNavigationPresenter presenter;
    private readonly long probeEpoch;
    private readonly bool split;
    public IRouterNavigator Navigator { get; }
    public event Action? Committed;

    private sealed class CheckSurface(Func<SurfaceTarget, CancellationToken, Task> publish) : IOahuNavigationSurface
    {
        public Task ShowAsync(OahuNavigationTarget target, CancellationToken token) =>
            publish(new SurfaceTarget(target.Revision, target.Branch,
                target.Branches.ToDictionary(x => x.Key, x => x.Value.Select(e => e.Id).ToArray()), target.Transition), token);
        public Task SettleAsync(OahuNavigationTarget target, CancellationToken token) => Task.CompletedTask;

        public Task AcceptAsync(OahuNavigationTarget target, Func<Action, bool> applyIfCurrent, CancellationToken token) =>
            Task.CompletedTask;
    }

    /// <summary>The screen name of the route the latest history entry recorded.</summary>
    public string CurrentRoute => Navigator.History.Current is { } entry ? ScreenName(entry.Route) : "";

    private static string ScreenName(AppRoute route) => route switch
    {
        Screen page => page.Name,
        SelectGame game => game.Id,
        PushGamePage => "game-page",
        OpenGame => "game",
        DetailLink => "detail",
        OpenModal => "editor",
        OpenFadeSheet => "fade-sheet",
        PushReview => "review",
        _ => ""
    };

    public static OahuNavigationPresenter CreatePresenter() => new(screenName: entry => entry.Route switch
    {
        Screen page => page.Name,
        SelectGame game => game.Id,
        PushGamePage => "game-page",
        OpenGame => "game",
        DetailLink => "detail",
        OpenModal => "editor",
        OpenFadeSheet => "fade-sheet",
        PushReview => "review",
        _ => ""
    });

    public ProbeRouter(Func<SurfaceTarget, CancellationToken, Task> publish) : this(CreatePresenter())
    {
        probeEpoch = presenter.Attach(new CheckSurface(publish));
    }

    /// <param name="split">Start with a split: Home in the primary column and the selected game's stack beside it.</param>
    public ProbeRouter(OahuNavigationPresenter presenter, RouteEntryLeaseRegistry? leases = null,
        IReadOnlyList<IBackNavigationPolicy>? backNavigationPolicies = null, NavigationDiagnostics? diagnostics = null,
        bool split = false)
    {
        this.presenter = presenter;
        this.split = split;
        var table = new RouteTableBuilder()
            .Map<DetailLink>("/catalog/group/item/detail", _ => new DetailLink())
            .Build();
        Navigator = RouterNavigatorFactory.Create(table, new Planner(split), presenter,
            new RouterNavigatorFactoryOptions
            {
                EntryLeases = leases, BackNavigationPolicies = backNavigationPolicies ?? [], Diagnostics = diagnostics
            });
        presenter.Bind(Navigator);
        presenter.Reconciled += OnCommit;
    }

    private void OnCommit() => Committed?.Invoke();
    public int HistoryCount => Navigator.History.Entries.Count;
    public string HistorySummary => string.Join(";", Navigator.History.Entries.Select(entry =>
        $"{entry.Request.Source}@{Describe(entry.State)}"));
    public string CurrentBranch => (Navigator.CurrentState.ActiveWindow?.Root as BranchHostNode)?.SelectedBranchId ?? "";
    public IReadOnlyDictionary<string, string[]> CurrentBranches => Branches(Navigator.CurrentState);
    public string[] CurrentEntryIds => EntriesIn(CurrentBranch);
    public string Status => $"{Describe(Navigator.CurrentState)} history={HistoryCount} [{HistorySummary}]";
    public string[] EntriesIn(string branch) => CurrentBranches.TryGetValue(branch, out var entries) ? entries : [];

    private static IReadOnlyDictionary<string, string[]> Branches(NavigationState state) =>
        state.ActiveWindow?.Root is BranchHostNode host
            ? host.Branches.ToDictionary(b => b.Id, b => ((StackNode)b.Content).Entries.Select(e => e.Id).ToArray())
            : new Dictionary<string, string[]>();

    private static string Describe(NavigationState state)
    {
        if (state.ActiveWindow?.Root is SplitNode { Primary: StackNode primary } split)
        {
            var columns = $"split[{Ids(primary)}]";
            if (split.Secondary is StackNode secondary) columns += $"+{secondary.Id}[{Ids(secondary)}]";
            var modal = state.ActiveWindow.Modals.FirstOrDefault() is { } sheet ? $"+modal:{sheet.Id}" : "";
            return $"{columns}{modal}@{(split.CompactColumn == SplitColumn.Secondary ? "secondary" : "primary")}";
        }
        if (state.ActiveWindow?.Root is not BranchHostNode host) return "[]";
        return $"{host.SelectedBranchId}[{string.Join(",", Branches(state)[host.SelectedBranchId])}]";
    }

    private static string Ids(StackNode stack) => string.Join(",", stack.Entries.Select(entry => entry.Id));

    /// <summary>Selects a game in the split from its Home row, so the presenter can match the row's cover.</summary>
    public async Task SelectGameAsync(string game, string sourceKey, CancellationToken cancellationToken)
    {
        var request = RouterNavigationRequest.FromRoute(new SelectGame(game), NavigationRequestSource.InAppCommand);
        var result = sourceKey.Length == 0
            ? await Navigator.NavigateAsync(request, cancellationToken)
            : await presenter.NavigateFromAsync(request, "home", sourceKey, cancellationToken);
        if (!result.Presented) throw new InvalidOperationException($"Game {game} did not open: {result}");
        OnCommit();
    }

    public Task PushGamePageAsync(CancellationToken cancellationToken = default) =>
        NavigateAsync(RouterNavigationRequest.FromRoute(new PushGamePage(), NavigationRequestSource.InAppCommand), cancellationToken);

    public Task PushHomePageAsync(CancellationToken cancellationToken = default) =>
        NavigateAsync(RouterNavigationRequest.FromRoute(new Screen("home-more"), NavigationRequestSource.InAppCommand), cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        NavigateAsync(RouterNavigationRequest.FromRoute(new Start(), NavigationRequestSource.InAppCommand), cancellationToken);

    public Task DeepLinkAsync(CancellationToken cancellationToken = default) =>
        NavigateAsync(RouterNavigationRequest.FromUri(
            new Uri("https://probe.example/catalog/group/item/detail"),
            NavigationRequestSource.AppLink, disposition: RouterNavigationDisposition.Canonical), cancellationToken);

    public Task OpenGameAsync(CancellationToken cancellationToken = default) =>
        OpenGameAsync("game-cover", cancellationToken);

    public async Task OpenGameAsync(string sourceKey, CancellationToken cancellationToken)
    {
        var request = RouterNavigationRequest.FromRoute(new OpenGame(), NavigationRequestSource.InAppCommand);
        var result = await presenter.NavigateFromAsync(request, "home-detail", sourceKey, cancellationToken);
        if (!result.Presented) throw new InvalidOperationException($"Game did not open: {result}");
        OnCommit();
    }

    public Task ModalAsync(CancellationToken cancellationToken = default) =>
        NavigateAsync(RouterNavigationRequest.FromRoute(new OpenModal(), NavigationRequestSource.InAppCommand), cancellationToken);

    public Task OpenFadeSheetAsync(CancellationToken cancellationToken = default) =>
        NavigateAsync(RouterNavigationRequest.FromRoute(new OpenFadeSheet(), NavigationRequestSource.InAppCommand), cancellationToken);

    public async Task PushReviewAsync(CancellationToken cancellationToken = default)
    {
        var request = RouterNavigationRequest.FromRoute(new PushReview(), NavigationRequestSource.InAppCommand);
        var result = await presenter.NavigateFromAsync(request, "edit", "review-cover", cancellationToken);
        if (!result.Presented) throw new InvalidOperationException($"Review did not open: {result}");
        OnCommit();
    }

    private async Task NavigateAsync(RouterNavigationRequest request, CancellationToken cancellationToken)
    {
        var result = await Navigator.NavigateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Presented)
            throw new InvalidOperationException($"Probe request did not present: {result}");
        OnCommit();
    }

    public Task ObserveAsync(long revision, string branch, IReadOnlyList<string> actualEntryIds) =>
        presenter.ObserveAsync(probeEpoch, revision, branch, actualEntryIds);

    public async ValueTask DisposeAsync()
    {
        presenter.Reconciled -= OnCommit;
        presenter.Dispose();
        await Navigator.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record OpenGame : AppRoute;
    private sealed record OpenModal : AppRoute;
    private sealed record OpenFadeSheet : AppRoute;
    private sealed record PushReview : AppRoute;
    private sealed record Start : AppRoute;
    private sealed record DetailLink : AppRoute;
    private sealed record Screen(string Name) : AppRoute;
    private sealed record SelectGame(string Id) : AppRoute;
    private sealed record PushGamePage : AppRoute;

    private sealed class Planner(bool split) : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(NavigationPlanningContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (split && PlanSplit(context) is { } splitPlan)
                return ValueTask.FromResult(splitPlan);
            if (context.Route is OpenGame && context.CurrentState.ActiveWindow is { Root: BranchHostNode branches } gameWindow &&
                branches.SelectedBranch is { Id: "home", Content: StackNode stack } homeBranch &&
                stack.Top?.Id == "home-detail" && gameWindow.Modals.Count == 0)
            {
                var next = stack with { Entries = [..stack.Entries, new RouteEntry("game", context.Route)] };
                var root = branches.ReplaceBranch(homeBranch with { Content = next });
                return ValueTask.FromResult(new NavigationPlan(context.CurrentState.ReplaceWindow(gameWindow with { Root = root })));
            }
            if (context.Route is OpenGame)
                throw new InvalidOperationException("Game can only be opened from the Home row.");
            if (context.Route is PushReview)
            {
                if (context.CurrentState.ActiveWindow is not { } active ||
                    active.Modals.SingleOrDefault() is not { Id: "editor", Content: StackNode { Top.Id: "edit" } sheetStack } sheet)
                    throw new InvalidOperationException("Review can only be pushed from the Edit sheet.");
                var updated = sheetStack with { Entries = [..sheetStack.Entries, new RouteEntry("review-again", context.Route)] };
                return ValueTask.FromResult(new NavigationPlan(context.CurrentState.ReplaceWindow(
                    active with { Modals = [sheet with { Content = updated }] })));
            }
            if (context.Route is OpenFadeSheet && context.CurrentState.ActiveWindow is { } sheetWindow)
            {
                var sheet = new ModalNode("fade-sheet", new RouteEntry("fade-sheet", context.Route));
                return ValueTask.FromResult(new NavigationPlan(context.CurrentState.ReplaceWindow(sheetWindow with { Modals = [sheet] })));
            }
            if (context.Route is OpenModal && context.CurrentState.ActiveWindow is { } window)
            {
                var modal = new ModalNode("editor", new RouteEntry("editor-owner", new OpenModal()),
                    new StackNode("editor-stack", [new RouteEntry("edit", new Screen("edit")),
                        new RouteEntry("review", new Screen("review"))]));
                return ValueTask.FromResult(new NavigationPlan(context.CurrentState.ReplaceWindow(window with { Modals = [modal] })));
            }
            var catalog = context.Route is DetailLink;
            var home = Branch("home", "home", "home-detail");
            var catalogBranch = catalog ? Branch("catalog", "catalog", "group", "item", "detail") : Branch("catalog", "catalog");
            var host = new BranchHostNode("sections", [home, catalogBranch], catalog ? "catalog" : "home", "home");
            return ValueTask.FromResult(new NavigationPlan(new NavigationState([new WindowNode("main", host)], "main")));
        }

        // Home is the primary column. Selecting a game shows its own stack in the secondary column.
        private static NavigationPlan? PlanSplit(NavigationPlanningContext context)
        {
            if (context.CurrentState.ActiveWindow is not { Root: SplitNode current } window)
            {
                var home = new StackNode("home-stack", [new RouteEntry("home", new Screen("home"))]);
                return new NavigationPlan(new NavigationState([new WindowNode("main", new SplitNode("games", home))], "main"));
            }
            NavigationPlan Plan(SplitNode next) => new(context.CurrentState.ReplaceWindow(window with { Root = next }));
            switch (context.Route)
            {
                case SelectGame game:
                    return Plan(current.Select(new StackNode(game.Id + "-stack", [new RouteEntry(game.Id, game)])));
                case PushGamePage when current.Secondary is StackNode stack:
                    var page = new RouteEntry($"{stack.Entries[0].Id}-page-{stack.Entries.Count}", context.Route);
                    return Plan(current with { Secondary = stack with { Entries = [.. stack.Entries, page] } });
                case Screen { Name: "home-more" } when current.Primary is StackNode primary:
                    return Plan(current with { Primary = primary with { Entries = [.. primary.Entries, new RouteEntry("home-more", context.Route)] } });
                case OpenModal:
                    var modal = new ModalNode("editor", new RouteEntry("editor-owner", new OpenModal()),
                        new StackNode("editor-stack", [new RouteEntry("edit", new Screen("edit"))]));
                    return new NavigationPlan(context.CurrentState.ReplaceWindow(window with { Modals = [modal] }));
                default:
                    throw new InvalidOperationException($"The split probe does not plan {context.Route}.");
            }
        }

        private static NavigationBranch Branch(string id, params string[] ids) =>
            new(id, id, new StackNode(id + "-stack", ids.Select(i =>
                new RouteEntry(i, i == "detail" ? new DetailLink() : new Screen(i))).ToArray()));
    }

}

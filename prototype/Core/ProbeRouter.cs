using AdamE.AppNav;
using AdamE.AppNav.Back;
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
public sealed record SurfaceTarget(long Revision, string Branch, IReadOnlyDictionary<string, string[]> Branches)
{
    public string[] EntryIds => Branch.Length == 0 ? [] : Branches[Branch];
}

public sealed class ProbeRouter : IAsyncDisposable
{
    private readonly OahuNavigationPresenter presenter;
    private readonly long probeEpoch;
    public IRouterNavigator Navigator { get; }
    public event Action? Committed;

    private sealed class CheckSurface(Func<SurfaceTarget, CancellationToken, Task> publish) : IOahuNavigationSurface
    {
        public Task ShowAsync(OahuNavigationTarget target, CancellationToken token) =>
            publish(new SurfaceTarget(target.Revision, target.Branch,
                target.Branches.ToDictionary(x => x.Key, x => x.Value.Select(e => e.Id).ToArray())), token);
        public Task AcceptAsync(OahuNavigationTarget target, OahuNavigationTarget committedTarget,
            Func<Action, bool> applyIfCurrent, CancellationToken token) =>
            Task.CompletedTask;
    }

    public static OahuNavigationPresenter CreatePresenter() => new(screenName: entry => entry.Route switch
    {
        Screen page => page.Name,
        OpenGame => "game",
        DetailLink => "detail",
        OpenModal => "editor",
        OpenFadeSheet => "fade-sheet",
        PushReview => "review",
        _ => ""
    });

    public ProbeRouter(Func<SurfaceTarget, CancellationToken, Task> publish) : this(new OahuNavigationPresenter())
    {
        probeEpoch = presenter.Attach(new CheckSurface(publish));
    }

    public ProbeRouter(OahuNavigationPresenter presenter, RouteEntryLeaseRegistry? leases = null,
        IReadOnlyList<IBackNavigationPolicy>? backNavigationPolicies = null)
    {
        this.presenter = presenter;
        var table = new RouteTableBuilder()
            .Map<DetailLink>("/catalog/group/item/detail", _ => new DetailLink())
            .Build();
        Navigator = RouterNavigatorFactory.Create(table, new Planner(), presenter,
            new RouterNavigatorFactoryOptions { EntryLeases = leases, BackNavigationPolicies = backNavigationPolicies ?? [] });
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
        if (state.ActiveWindow?.Root is not BranchHostNode host) return "[]";
        return $"{host.SelectedBranchId}[{string.Join(",", Branches(state)[host.SelectedBranchId])}]";
    }

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

    private sealed class Planner : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(NavigationPlanningContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        private static NavigationBranch Branch(string id, params string[] ids) =>
            new(id, id, new StackNode(id + "-stack", ids.Select(i =>
                new RouteEntry(i, i == "detail" ? new DetailLink() : new Screen(i))).ToArray()));
    }

}

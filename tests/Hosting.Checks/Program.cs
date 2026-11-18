using AdamE.AppNav.Leases;
using AdamE.AppNav.Oahu;
using AdamE.AppNav.Oahu.Hosting;
using AppNav.SwiftUI.Prototype;
using Oahu.Testing;

var loop = new OahuTestLoop();
loop.Run(async () =>
{
    var ui = SynchronizationContext.Current!;
    Task Dispatch(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(_ =>
        {
            try { action(); done.SetResult(); }
            catch (Exception failure) { done.SetException(failure); }
        }, null);
        return done.Task;
    }

    var leases = new RouteEntryLeaseRegistry(new LeaseHost(Dispatch));
    using var presenter = new OahuNavigationPresenter();
    await using var router = new ProbeRouter(presenter, leases);
    Dictionary<string, List<Oahu.OahuMount>> created = [];
    List<long> released = [];
    RouteEntryLease? detailLease = null;
    bool failDetail = true;
    bool retireInFactory = false;
    bool retireBeforeAcknowledgement = false;
    NavigationShellViewModel? shell = null;
    shell = new NavigationShellViewModel(leases, (entry, lease, opened) =>
    {
        if (entry.Id == "detail" && failDetail) throw new ApplicationException("screen creation failed");
        if (entry.Id == "detail") detailLease = lease;
        var mount = loop.Mount<INavigationShellViewModel>(shell!);
        if (!created.TryGetValue(entry.Id, out var instances)) created.Add(entry.Id, instances = []);
        instances.Add(mount);
        if (entry.Id == "detail" && retireInFactory) lease.Retire();
        return new(mount, () =>
        {
            Check(mount.IsDisposed, "Mount cleanup ran before disconnection");
            released.Add(mount.Id);
        });
    }, Dispatch, _ => Task.CompletedTask, failure => throw failure);
    long epoch = 0;
    var surface = new Witness(shell, target =>
    {
        if (retireBeforeAcknowledgement && target.SelectedBranch == "catalog") detailLease!.Retire();
        return presenter.ObserveAsync(epoch, target.Revision, target.SelectedBranch,
            target.Branches.Single(b => b.Branch == target.SelectedBranch).Entries.Select(e => e.EntryId).ToArray());
    });
    epoch = presenter.Attach(surface);
    try
    {
        await router.StartAsync(default);
        var initial = created.Values.SelectMany(x => x).ToArray();
        try
        {
            await router.DeepLinkAsync(default);
            throw new Exception("Expected screen creation to fail");
        }
        catch (ApplicationException failure) when (failure.Message == "screen creation failed") { }
        Check(router.HistoryCount == 1 && router.CurrentBranch == "home", "Failed link changed AppNav");
        Check(created["group"].Single().IsDisposed && created["item"].Single().IsDisposed, "Rollback leaked speculative screens");
        Check(initial.All(m => !m.IsDisposed), "Rollback disconnected a retained screen");
        failDetail = false;

        async Task CheckRetirementRollback()
        {
            try
            {
                await router.DeepLinkAsync(default);
                throw new Exception("A provisional lease retired without aborting navigation");
            }
            catch (OperationCanceledException) { }
            Check(router.HistoryCount == 1 && router.CurrentBranch == "home", "A retired provisional lease changed AppNav");
            Check(created["detail"].Last().IsDisposed && created["group"].Last().IsDisposed,
                "Rollback leaked a speculative screen after lease retirement");
            Check(initial.All(m => !m.IsDisposed), "Retirement rollback disconnected a retained screen");
            // This fixture reuses "detail" for its next occurrence. An explicitly retired
            // provisional lease can own that ID until its asynchronous release completes.
            await detailLease!.Released.WaitAsync(TimeSpan.FromSeconds(5));
        }

        retireInFactory = true;
        await CheckRetirementRollback();
        retireInFactory = false;
        retireBeforeAcknowledgement = true;
        await CheckRetirementRollback();
        retireBeforeAcknowledgement = false;

        await router.DeepLinkAsync(default);
        Check(router.HistoryCount == 2 && router.CurrentBranch == "catalog", "Retry did not commit");
        Check(created["group"].Count == 4 && !created["group"].Last().IsDisposed, "Retry reused a disconnected mount");
        var detail = created["detail"].Last();
        detailLease!.Retire();
        await router.Navigator.RefreshPresentationAsync();
        var catalog = shell.Target.Current.Branches.Single(b => b.Branch == "catalog");
        Check(catalog.Entries.Last() == new ScreenEntry("detail", 0), "Retired entry was remounted");
        Check(detail.IsDisposed && router.HistoryCount == 2, "Refresh retained the old mount or added history");
        presenter.Detach(epoch);
        Task shutdown;
        using (loop.NativeCall())
        {
            shutdown = shell.DisposeAsync().AsTask();
            Check(!shutdown.IsCompleted, "Cleanup ran inside an OAHU callback");
        }
        await shutdown;
    }
    finally
    {
        presenter.Detach(epoch);
        shell.Dispose();
    }
    Check(released.Order().SequenceEqual(created.Values.SelectMany(x => x).Select(x => x.Id).Order()),
        "Every created mount must complete cleanup exactly once");

    var unsafeLeases = new RouteEntryLeaseRegistry(new LeaseHost(Dispatch));
    using var unsafePresenter = new OahuNavigationPresenter();
    var unsafeRouter = new ProbeRouter(unsafePresenter, unsafeLeases);
    RouteEntryLease? unsafeLease = null;
    RouteEntryLease? cleanLease = null;
    Oahu.OahuMount? unsafeMount = null;
    var unsafeResourceReleased = false;
    var cleanupFailure = new ApplicationException("screen owner cleanup failed");
    NavigationShellViewModel? unsafeShell = null;
    unsafeShell = new NavigationShellViewModel(unsafeLeases, (entry, lease, _) =>
    {
        var mount = loop.Mount<INavigationShellViewModel>(unsafeShell!);
        if (entry.Id == "home") cleanLease = lease;
        if (entry.Id != "home-detail") return new(mount);
        unsafeLease = lease;
        unsafeMount = mount;
        lease.Own(() => unsafeResourceReleased = true);
        return new(mount, () => throw cleanupFailure);
    }, Dispatch, _ => Task.CompletedTask, failure => throw failure);
    long unsafeEpoch = 0;
    unsafeEpoch = unsafePresenter.Attach(new Witness(unsafeShell, target =>
        unsafePresenter.ObserveAsync(unsafeEpoch, target.Revision, target.SelectedBranch,
            target.Branches.Single(b => b.Branch == target.SelectedBranch).Entries.Select(e => e.EntryId).ToArray())));
    try
    {
        await unsafeRouter.StartAsync();
        unsafePresenter.Detach(unsafeEpoch);
        await unsafeShell.DisposeAsync();
        throw new Exception("Expected screen owner cleanup to fail");
    }
    catch (AggregateException failure) when (failure.InnerExceptions.Contains(cleanupFailure)) { }

    // Shutdown must remain pending while the failed screen's borrower cannot be certified gone.
    // Wait only for the independent clean lease so this deliberately held fixture cannot hang the harness.
    var unsafeShutdown = unsafeRouter.DisposeAsync().AsTask();
    await cleanLease!.Released.WaitAsync(TimeSpan.FromSeconds(5));
    Check(unsafeMount!.IsDisposed && unsafeLease!.IsRetired && !unsafeLease.Released.IsCompleted &&
          !unsafeResourceReleased && !unsafeShutdown.IsCompleted,
        "Failed screen cleanup released a lease while its borrower was still unsafe");
});
loop.Finish();
Console.WriteLine("PASS OAHU mount ownership across staging failure, lease retirement, verified rollback, retry, detach and failed owner cleanup");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class Witness(NavigationShellViewModel shell, Func<ShellTarget, Task> report) : IOahuNavigationSurface
{
    public async Task ShowAsync(OahuNavigationTarget target, CancellationToken token)
    {
        await shell.ShowAsync(target, token);
        await report(shell.Target.Current);
    }

    public Task AcceptAsync(OahuNavigationTarget target, CancellationToken token) => shell.AcceptAsync(target, token);
}

sealed class LeaseHost(Func<Action, Task> dispatch) : IRouteEntryLeaseHost
{
    public async Task InvokeAsync(Func<ValueTask> work)
    {
        ValueTask release = default;
        await dispatch(() => release = work());
        await release;
    }
    public void ReportDefect(RouteEntryLeaseReleaseException failure) => throw failure;
    public void ReportFatal(RouteEntryLeaseReleaseException failure) => throw failure;
}

using System.Threading.Channels;
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

    await AcceptanceChecks.RunAsync(loop, Dispatch);

    var leases = new RouteEntryLeaseRegistry(new LeaseHost(Dispatch));
    using var presenter = new OahuNavigationPresenter();
    await using var router = new ProbeRouter(presenter, leases);
    Dictionary<string, List<Oahu.OahuMount>> created = [];
    List<long> released = [];
    RouteEntryLease? detailLease = null;
    RouteEntryLease? itemLease = null;
    bool failDetail = true;
    bool retireInFactory = false;
    bool retireBeforeAcknowledgement = false;
    NavigationShellViewModel? shell = null;
    shell = new NavigationShellViewModel(leases, (entry, lease, opened) =>
    {
        if (entry.Id == "detail" && failDetail) throw new ApplicationException("screen creation failed");
        if (entry.Id == "detail") detailLease = lease;
        if (entry.Id == "item") itemLease = lease;
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
    var resumeInitialSettlement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var showFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var resumeFailedShow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    long epoch = 0;
    var surface = new Witness(shell, target =>
    {
        if (retireBeforeAcknowledgement && target.SelectedBranch == "catalog") detailLease!.Retire();
        return presenter.ObserveAsync(epoch, target.Revision, target.SelectedBranch,
            target.Branches.Single(b => b.Branch == target.SelectedBranch).Entries.Select(e => e.EntryId).ToArray());
    }, beforeSettle: target => target.Transition.Operation == "initial" ? resumeInitialSettlement.Task : Task.CompletedTask,
        onShowFailure: () =>
        {
            showFailed.TrySetResult();
            return resumeFailedShow.Task;
        });
    epoch = presenter.Attach(surface);
    try
    {
        await router.StartAsync(default);
        var initial = created.Values.SelectMany(x => x).ToArray();
        var failedLink = router.DeepLinkAsync(default);
        try
        {
            await showFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            resumeInitialSettlement.SetResult();
            await surface.WaitForSettlementAsync("initial");
            Check(!created["group"].Single().IsDisposed && !created["item"].Single().IsDisposed,
                "Old settlement removed newer mounts staged before a screen factory failed");
        }
        finally
        {
            resumeInitialSettlement.TrySetResult();
            resumeFailedShow.SetResult();
        }

        try
        {
            await failedLink;
            throw new Exception("Expected screen creation to fail");
        }
        catch (ApplicationException failure) when (failure.Message == "screen creation failed") { }
        await surface.WaitForSettlementAsync("rollback");
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
            await surface.WaitForSettlementAsync("rollback");
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
        Check(!detail.IsDisposed && !detailLease.Released.IsCompleted,
            "Retiring a visible entry released its mount");
        await router.Navigator.RefreshPresentationAsync();
        await surface.WaitForSettlementAsync("refresh");
        var catalog = shell.Target.Current.Branches.Single(b => b.Branch == "catalog");
        Check(catalog.Entries.Last() == new ScreenEntry("detail", 0), "Retired entry was remounted");
        Check(detail.IsDisposed && router.HistoryCount == 2, "Refresh retained the old mount or added history");

        var item = created["item"].Last();
        await presenter.ObserveAsync(epoch, shell.Target.Current.Revision, "catalog", ["catalog", "group"]);
        Check(router.HistoryCount == 3 && router.CurrentEntryIds.SequenceEqual(["catalog", "group"]),
            "Native multi-pop did not commit");
        Check(item.IsDisposed && released.Count(id => id == item.Id) == 1,
            "Native multi-pop must release its removed mount without another presentation");
        Check(!created["group"].Last().IsDisposed, "Native multi-pop released the still visible mount");
        await itemLease!.Released.WaitAsync(TimeSpan.FromSeconds(5));
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
    var cleanupAttempts = 0;
    var reports = new List<Exception>();
    using var cancelBack = new CancellationTokenSource();
    var cancelAfterAccept = true;
    Oahu.OahuMount? unsafeMount = null;
    var unsafeResourceReleased = false;
    var cleanupFailure = new ApplicationException("screen owner cleanup failed");
    NavigationShellViewModel? unsafeShell = null;
    unsafeShell = new NavigationShellViewModel(unsafeLeases, (entry, lease, _) =>
    {
        var mount = loop.Mount<INavigationShellViewModel>(unsafeShell!);

        if (entry.Id != "home-detail") return new(mount);
        unsafeLease = lease;
        unsafeMount = mount;
        lease.Own(() => unsafeResourceReleased = true);
        return new(mount, () =>
        {
            Check(unsafeRouter.HistoryCount == 2 && unsafeRouter.CurrentEntryIds.SequenceEqual(["home"]),
                "Cleanup started before the router committed its state and history");
            cleanupAttempts++;
            if (cleanupAttempts <= 2) throw cleanupFailure;
        });
    }, Dispatch, _ => Task.CompletedTask, reports.Add);
    long unsafeEpoch = 0;
    var unsafeSurface = new Witness(unsafeShell, target =>
        unsafePresenter.ObserveAsync(unsafeEpoch, target.Revision, target.SelectedBranch,
            target.Branches.Single(b => b.Branch == target.SelectedBranch).Entries.Select(e => e.EntryId).ToArray()),
        accepted =>
        {
            if (cancelAfterAccept && accepted.Transition.Operation == "pop")
            {
                cancelAfterAccept = false;
                cancelBack.Cancel();
            }
        });
    unsafeEpoch = unsafePresenter.Attach(unsafeSurface);
    await unsafeRouter.StartAsync();
    await unsafeSurface.WaitForSettlementAsync("initial");
    var original = unsafeMount!;
    try
    {
        await unsafeRouter.Navigator.BackAsync(cancellationToken: cancelBack.Token);
        throw new Exception("Expected cancellation after native acceptance");
    }
    catch (OperationCanceledException) when (cancelBack.IsCancellationRequested) { }
    await unsafeSurface.WaitForSettlementAsync("rollback");
    Check(unsafeRouter.HistoryCount == 1 && !original.IsDisposed && ReferenceEquals(original, unsafeMount) &&
          unsafeShell.Target.Current.Branches.Single(b => b.Branch == "home").Entries.Last().Mount == original.Id,
        "Rollback must restore the original mounted screen");

    await unsafeRouter.Navigator.BackAsync();
    await unsafeSurface.WaitForSettlementAsync("pop");
    Check(unsafeRouter.HistoryCount == 2 && unsafeRouter.CurrentEntryIds.SequenceEqual(["home"]) &&
          original.IsDisposed && reports.Count == 1 && reports[0] is AggregateException reportedFailure &&
          reportedFailure.InnerExceptions.Contains(cleanupFailure) && !unsafeResourceReleased,
        "Failed deferred cleanup rolled back Back, released its lease, or lost the error");
    try
    {
        await unsafeShell.DisposeAsync();
        throw new Exception("Expected teardown to report the repeated cleanup failure");
    }
    catch (AggregateException error) when (error.InnerExceptions.Contains(cleanupFailure)) { }

    var unsafeShutdown = unsafeRouter.DisposeAsync().AsTask();
    Check(!unsafeLease!.Released.IsCompleted && !unsafeResourceReleased && !unsafeShutdown.IsCompleted,
        "Shutdown detached a borrower after owner cleanup failed");
    await unsafeShell.DisposeAsync();
    await unsafeShutdown.WaitAsync(TimeSpan.FromSeconds(5));
    Check(cleanupAttempts == 3 && unsafeResourceReleased, "Repeated disposal did not resume failed owner cleanup");
});
loop.Finish();
Console.WriteLine("PASS OAHU mount ownership across staging failure, lease retirement, verified rollback, retry, detach and failed owner cleanup");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class Witness(NavigationShellViewModel shell, Func<ShellTarget, Task> report,
    Action<OahuNavigationTarget>? afterAccept = null, Func<OahuNavigationTarget, Task>? beforeSettle = null,
    Func<Task>? onShowFailure = null) : IOahuNavigationSurface
{
    private readonly Channel<OahuNavigationTarget> settlements = Channel.CreateUnbounded<OahuNavigationTarget>();

    public async Task WaitForSettlementAsync(string operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await settlements.Reader.ReadAsync(timeout.Token)).Transition.Operation != operation) { }
    }

    public async Task ShowAsync(OahuNavigationTarget target, CancellationToken token)
    {
        try { await shell.ShowAsync(target, token); }
        catch when (onShowFailure is not null)
        {
            await onShowFailure();
            throw;
        }
        await report(shell.Target.Current);
    }

    public async Task AcceptAsync(OahuNavigationTarget target, Func<Action, bool> applyIfCurrent, CancellationToken token)
    {
        await shell.AcceptAsync(target, applyIfCurrent, token);
        afterAccept?.Invoke(target);
    }

    public async Task SettleAsync(OahuNavigationTarget target, CancellationToken token)
    {
        if (beforeSettle is not null)
            await beforeSettle(target);
        await shell.SettleAsync(target, token);
        settlements.Writer.TryWrite(target);
    }
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

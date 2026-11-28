using AdamE.AppNav.Leases;
using AdamE.AppNav.Oahu;
using AdamE.AppNav.Oahu.Hosting;
using AppNav.SwiftUI.Prototype;
using Oahu.Testing;

internal static class TakenBackChecks
{
    // The SwiftUI shell reports a taken-back push as two readings from one native call: the staged push, then the
    // shorter stack. An operation already queued behind the push must wait for that Back. Otherwise it plans from
    // the state that still has the page, the Back is rejected as stale, and its projection pushes the page again.
    internal static async Task RunAsync(OahuTestLoop loop, Func<Action, Task> dispatch)
    {
        var leases = new RouteEntryLeaseRegistry(new LeaseHost(dispatch));
        using var presenter = new OahuNavigationPresenter();
        await using var router = new ProbeRouter(presenter, leases);
        NavigationShellViewModel? shell = null;
        long epoch = 0;
        shell = new NavigationShellViewModel(leases, (_, _, _) => new(loop.Mount<INavigationShellViewModel>(shell!)),
            dispatch, reading => presenter.ObserveAsync(epoch, reading.Revision, reading.Branch, reading.Entries,
                reading.ModalId, reading.ModalEntries), failure => throw failure);
        var gameShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var surface = new HeldGameSurface(shell, target =>
        {
            if (target.Branches.Single(b => b.Branch == "home").Entries.LastOrDefault()?.EntryId == "game")
            {
                gameShown.TrySetResult();
                return Task.CompletedTask;
            }
            return Observe(presenter, epoch, target);
        });
        epoch = presenter.Attach(surface);
        try
        {
            await router.StartAsync();
            var game = router.OpenGameAsync();
            await gameShown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var staged = shell.Target.Current;
            var modal = router.ModalAsync();

            using (loop.NativeCall())
            {
                _ = shell.Report(Reading(staged, ["home", "home-detail", "game"]));
                _ = shell.Report(Reading(staged, ["home", "home-detail"]));
            }
            try { await Task.WhenAll(game, modal).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                // A lost Back leaves the sheet's projection holding Game, which this surface never acknowledges.
                throw new Exception($"The queued sheet did not settle after the taken-back push: {router.Status}");
            }

            Check(router.HistoryCount == 4, $"Expected the push, a native Back and the sheet in history: {router.Status}");
            var home = shell.Target.Current.Branches.Single(b => b.Branch == "home").Entries.Select(e => e.EntryId);
            Check(home.SequenceEqual(["home", "home-detail"]) && shell.Target.Current.ModalId == "editor",
                $"The queued sheet put the taken-back page back under it: {router.Status}");
        }
        finally
        {
            presenter.Detach(epoch);
            await shell.DisposeAsync();
        }
        Console.WriteLine("PASS a queued operation waits for both readings of a taken-back push");
    }

    private static NativeObservation Reading(ShellTarget target, string[] home) =>
        new(target.Revision, "home", home, "", [], [], "");

    private static Task Observe(OahuNavigationPresenter presenter, long epoch, ShellTarget target) =>
        presenter.ObserveAsync(epoch, target.Revision, target.SelectedBranch,
            target.Branches.Single(b => b.Branch == target.SelectedBranch).Entries.Select(e => e.EntryId).ToArray(),
            target.ModalId, target.ModalEntries.Select(e => e.EntryId).ToArray());

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class HeldGameSurface(NavigationShellViewModel shell, Func<ShellTarget, Task> shown) : IOahuNavigationSurface
    {
        public async Task ShowAsync(OahuNavigationTarget target, CancellationToken token)
        {
            await shell.ShowAsync(target, token);
            await shown(shell.Target.Current);
        }

        public Task AcceptAsync(OahuNavigationTarget target, Func<Action, bool> applyIfCurrent, CancellationToken token) =>
            shell.AcceptAsync(target, applyIfCurrent, token);

        public Task SettleAsync(OahuNavigationTarget target, CancellationToken token) => shell.SettleAsync(target, token);
    }
}

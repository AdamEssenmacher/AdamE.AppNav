using AdamE.AppNav.Leases;
using AdamE.AppNav.Oahu;
using AdamE.AppNav.Oahu.Hosting;
using AppNav.SwiftUI.Prototype;
using Oahu.Testing;

internal static class AcceptanceChecks
{
    internal static async Task RunAsync(OahuTestLoop loop, Func<Action, Task> dispatch)
    {
        await RetirementAfterPublicationAsync(loop, dispatch);
        await UncertifiedCloseAsync(loop, dispatch);
    }

    private static async Task RetirementAfterPublicationAsync(OahuTestLoop loop, Func<Action, Task> dispatch)
    {
        var leases = new RouteEntryLeaseRegistry(new LeaseHost(dispatch));
        using var presenter = new OahuNavigationPresenter();
        await using var router = new ProbeRouter(presenter, leases);
        RouteEntryLease? detail = null;
        RouteEntryLease? game = null;
        bool retireAfterPublication = false;
        NavigationShellViewModel? shell = null;
        shell = new NavigationShellViewModel(leases, (entry, lease) =>
        {
            if (entry.Id == "home-detail") detail = lease;
            if (entry.Id == "game") game = lease;
            return loop.Mount<INavigationShellViewModel>(shell!);
        }, dispatch, _ => Task.CompletedTask, error => throw error);
        using (shell)
        {
            long epoch = 0;
            epoch = presenter.Attach(new PublishingWitness(shell, target => Report(presenter, epoch, target), () =>
            {
                if (!retireAfterPublication) return;
                retireAfterPublication = false;
                detail!.Retire();
            }));
            await router.StartAsync();
            await router.OpenGameAsync();
            await router.Navigator.BackAsync();
            await game!.Released.WaitAsync(TimeSpan.FromSeconds(5));
            if (router.HistoryCount != 3 || !router.CurrentEntryIds.SequenceEqual(["home", "home-detail"]))
                throw new InvalidOperationException("Back did not release the popped Game while Home remained idle.");
            retireAfterPublication = true;
            await router.Navigator.RefreshPresentationAsync();
            if (retireAfterPublication || router.HistoryCount != 3 || !detail!.IsRetired)
                throw new InvalidOperationException("Retirement after publication invalidated the accepted revision.");
            await router.Navigator.RefreshPresentationAsync();
            if (shell.Target.Current.Branches.Single(b => b.Branch == "home").Entries.Last().Mount != 0)
                throw new InvalidOperationException("Refresh did not replace the retired screen with its placeholder.");
            presenter.Detach(epoch);
            await shell.DisposeAsync();
        }
    }

    private static async Task UncertifiedCloseAsync(OahuTestLoop loop, Func<Action, Task> dispatch)
    {
        var leases = new RouteEntryLeaseRegistry(new LeaseHost(dispatch));
        using var presenter = new OahuNavigationPresenter();
        var router = new ProbeRouter(presenter, leases);
        var reports = new List<Exception>();
        bool deferNativeClose = false;
        RouteEntryLease? detail = null;
        bool cleaned = false;
        NavigationShellViewModel? shell = null;
        shell = new NavigationShellViewModel(leases, (entry, lease, _) =>
        {
            var mount = loop.Mount<INavigationShellViewModel>(shell!);
            if (entry.Id != "home-detail") return new(mount);
            detail = lease;
            return new(mount, () => cleaned = true);
        }, action => dispatch(() =>
        {
            // Fault injection: posting into an OAHU native callback defers Dispose's completion.
            using var nativeCall = deferNativeClose ? loop.NativeCall() : null;
            action();
        }), _ => Task.CompletedTask, reports.Add);
        long epoch = 0;
        epoch = presenter.Attach(new Witness(shell, target => Report(presenter, epoch, target)));
        await router.StartAsync();
        deferNativeClose = true;
        await router.Navigator.BackAsync();
        await router.Navigator.RefreshPresentationAsync();
        deferNativeClose = false;
        await router.Navigator.RefreshPresentationAsync();
        presenter.Detach(epoch);
        await shell.DisposeAsync();
        var shutdown = router.DisposeAsync().AsTask();
        if (reports.Count != 1 || !detail!.IsRetired || cleaned || detail.Released.IsCompleted || shutdown.IsCompleted)
            throw new InvalidOperationException("An uncertified close must report once and keep its lease attached.");
    }

    private static Task Report(OahuNavigationPresenter presenter, long epoch, ShellTarget target) =>
        presenter.ObserveAsync(epoch, target.Revision, target.SelectedBranch,
            target.Branches.Single(b => b.Branch == target.SelectedBranch).Entries.Select(e => e.EntryId).ToArray());

    private sealed class PublishingWitness(NavigationShellViewModel shell, Func<ShellTarget, Task> report,
        Action afterPublication) : IOahuNavigationSurface
    {
        public async Task ShowAsync(OahuNavigationTarget target, CancellationToken token)
        {
            await shell.ShowAsync(target, token);
            await report(shell.Target.Current);
        }

        public Task AcceptAsync(OahuNavigationTarget target,
            Func<Action, bool> applyIfCurrent, CancellationToken token) =>
            shell.AcceptAsync(target, publish => applyIfCurrent(() =>
            {
                publish();
                afterPublication();
            }), token);

        public Task SettleAsync(OahuNavigationTarget target, CancellationToken token) => shell.SettleAsync(target, token);
    }
}

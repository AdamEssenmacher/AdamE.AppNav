using AdamE.AppNav.Leases;
using AdamE.AppNav.Oahu;
using AdamE.AppNav.Oahu.Hosting;
using AppNav.SwiftUI.Prototype;
using Oahu.Testing;

// The wire carries a split's secondary column, mounts its entries, and releases them once a native Back closes it.
internal static class SplitChecks
{
    internal static async Task RunAsync(OahuTestLoop loop, Func<Action, Task> dispatch)
    {
        var leases = new RouteEntryLeaseRegistry(new LeaseHost(dispatch));
        using var presenter = ProbeRouter.CreatePresenter();
        await using var router = new ProbeRouter(presenter, leases, split: true);
        Dictionary<string, Oahu.OahuMount> mounts = [];
        NavigationShellViewModel? attached = null;
        var shell = NavigationShellViewModel.Attach(presenter, leases, (entry, _, _) =>
        {
            var owner = attached ?? throw new InvalidOperationException("The shell mounted a screen before it attached.");
            var mount = loop.Mount<INavigationShellViewModel>(owner);
            mounts[entry.Id] = mount;
            return new(mount);
        }, dispatch, failure => throw failure, presentsSplits: true);
        attached = shell;
        Check(shell.PresentsSplits, "The SwiftUI-style shell must declare split support");
        try
        {
            var start = router.StartAsync();
            var home = await StagedAsync(shell, 0);
            Check(home.Split is { SplitId: "games", SecondaryId: "", SecondaryEntries.Count: 0, CompactColumn: "primary" } &&
                home.SelectedBranch == "home-stack" && !home.IsBranchHost, $"Split root on the wire: {home.Split}");
            Report(loop, shell, home, ["home"], [], "primary");
            await start.WaitAsync(TimeSpan.FromSeconds(5));

            var select = router.SelectGameAsync("game-a", "split-cover-a", default);
            var game = await StagedAsync(shell, home.Revision);
            var secondary = game.Split.SecondaryEntries.Single();
            Check(game.Split is { SecondaryId: "game-a-stack", CompactColumn: "secondary" } && secondary.EntryId == "game-a" &&
                mounts.TryGetValue("game-a", out var gameMount) && secondary.Mount == gameMount.Id,
                $"The secondary column was not mounted on the wire: {game.Split}");
            Report(loop, shell, game, ["home"], ["game-a"], "secondary");
            await select.WaitAsync(TimeSpan.FromSeconds(5));

            // A collapsed window's Back from the game's root closes the column and releases its mount.
            Report(loop, shell, game, ["home"], [], "primary");
            await WaitUntilAsync(() => router.Status.StartsWith("split[home]@primary history=3", StringComparison.Ordinal),
                () => $"Closing the secondary column did not reconcile: {router.Status}");
            await WaitUntilAsync(() => mounts["game-a"].IsDisposed && shell.Target.Current.Split.SecondaryEntries.Count == 0,
                () => "The closed column's mount stayed attached");
            Check(!mounts["home"].IsDisposed, "Closing the secondary column released Home");
        }
        finally
        {
            shell.Detach();
            await shell.DisposeAsync();
        }
        Console.WriteLine("PASS a split's secondary column is mounted, observed and released after a native close");
    }

    private static void Report(OahuTestLoop loop, NavigationShellViewModel shell, ShellTarget target, string[] primary,
        string[] secondary, string compact)
    {
        using (loop.NativeCall())
            shell.Report(new(target.Revision, target.SelectedBranch, primary, "", [], secondary, compact));
    }

    private static async Task<ShellTarget> StagedAsync(NavigationShellViewModel shell, long after)
    {
        await WaitUntilAsync(() => shell.Target.Current.Revision > after, () => $"No revision after {after}");
        return shell.Target.Current;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception(failure());
            await Task.Delay(10);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

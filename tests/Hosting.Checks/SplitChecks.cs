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

            // Selecting another game replaces the column: its mount is staged and the old game's mount is released.
            var gameA = mounts["game-a"];
            var replace = router.SelectGameAsync("game-b", "", default);
            var switched = await StagedAsync(shell, game.Revision);
            Check(switched.Split is { SecondaryId: "game-b-stack" } && mounts.TryGetValue("game-b", out var gameBMount) &&
                switched.Split.SecondaryEntries.Single().Mount == gameBMount.Id && !gameA.IsDisposed,
                $"The replacement game was not mounted beside the old one before acceptance: {switched.Split}");
            Report(loop, shell, switched, ["home"], ["game-b"], "secondary");
            await replace.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => gameA.IsDisposed, () => "The replaced game's mount stayed attached");
            Check(!mounts["game-b"].IsDisposed && !mounts["home"].IsDisposed, "Replacing the game released a shown screen");

            // A collapsed window's Back from the game's root closes the column and releases its mount.
            Report(loop, shell, switched, ["home"], [], "primary");
            await WaitUntilAsync(() => router.Status.StartsWith("split[home]@primary history=4", StringComparison.Ordinal),
                () => $"Closing the secondary column did not reconcile: {router.Status}");
            await WaitUntilAsync(() => mounts["game-b"].IsDisposed && shell.Target.Current.Split.SecondaryEntries.Count == 0,
                () => "The closed column's mount stayed attached");
            Check(!mounts["home"].IsDisposed, "Closing the secondary column released Home");
        }
        finally
        {
            shell.Detach();
            await shell.DisposeAsync();
        }
        Console.WriteLine("PASS a split's secondary column is mounted, replaced, observed and released after a native close");
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

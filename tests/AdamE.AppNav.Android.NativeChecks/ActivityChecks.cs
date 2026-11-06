using AdamE.AppNav.Plans;
using AdamE.AppNav.State;
using AndroidX.Activity;
using static AdamE.AppNav.Android.NativeChecks.Checks;

namespace AdamE.AppNav.Android.NativeChecks;

/// <summary>The presenter outlives activities: a new one gets new pages for the same entries, and an operation waits
/// for an attached activity.</summary>
internal static class ActivityChecks
{
    public static IEnumerable<(string Name, Func<ComponentActivity, CancellationToken, Task> Check)> All =>
    [
        ("activity-recreation", RecreationAsync),
        ("activity-recreate", RecreatedAsync),
        ("operation-in-a-stopped-activity", StoppedActivityAsync),
        ("canceled-operation-in-a-stopped-activity", CanceledWhileStoppedAsync),
        ("operation-waits-for-next-activity", WaitsForNextActivityAsync),
        ("finishing-activity", FinishingActivityAsync),
        ("disposal-drains-operation", DisposalDrainsAsync)
    ];

    // Another activity attaching retires the first one's pages out of its window; a refresh builds the same entries'
    // pages in the new activity, and again in the first once it is back.
    private static async Task RecreationAsync(ComponentActivity activity, CancellationToken token)
    {
        var built = new List<(string Entry, ComponentActivity Activity, Java.Lang.Object Native)>();
        AndroidNavigationPresenter presenter = Presenter(request =>
        {
            AndroidPage page = request.Placement is AndroidPagePlacement.Modal
                ? ModalPage(new ProbeDialog(request.Activity))
                : Page(new ProbeView(request.Activity));
            built.Add((request.Entry.Id, request.Activity, (Java.Lang.Object?)page.View ?? page.Dialog
                ?? throw new InvalidOperationException("No native page.")));
            return page;
        });
        Mount(presenter, activity);
        NavigationState shown = Shows("root", "sheet-entry");
        ProbeActivity? second = null;
        try
        {
            await ApplyAsync(presenter, shown, NavigationState.Empty, token);
            var firstRoot = (ProbeView)built[0].Native;
            var firstSheet = (ProbeDialog)built[1].Native;

            ProbeActivity.Creating = probe => Mount(presenter, probe);
            try
            {
                second = await ProbeActivity.StartAsync(activity, token);
            }
            finally
            {
                ProbeActivity.Creating = null;
            }

            second.Destroying = presenter.Detach;
            Require(
                firstRoot is { Disposed: true, DetachedAtDisposal: true }
                && firstSheet is { Retired: true, GoneAtRetirement: true },
                "A new activity attaching did not retire the old one's pages out of its window.");
            await ApplyAsync(presenter, shown, shown, token, NavigationPlanKind.Refresh);
            Require(
                built.Count == 4
                && built.Skip(2).All(page => ReferenceEquals(page.Activity, second))
                && built.Skip(2).Select(static page => page.Entry).SequenceEqual(["root", "sheet-entry"])
                && presenter.ShownDialog is { IsShowing: true },
                "The refresh did not build the same entries' pages in the new activity.");

            second.Finish();
            await second.Destroyed.Task.WaitAsync(Soon, token);
            Require(
                ((ProbeView)built[2].Native).Disposed && ((ProbeDialog)built[3].Native).Retired,
                "The destroyed activity's pages were not retired.");
            Mount(presenter, activity);
            await ApplyAsync(presenter, shown, shown, token, NavigationPlanKind.Refresh);
            Require(
                built.Count == 6 && ReferenceEquals(built[4].Activity, activity) && Visible(activity) is ProbeView,
                "Coming back to the first activity did not build its pages again.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }

    // A real recreation, with a dialog showing: the old instance's OnDestroy detaches it and the new one's OnCreate
    // attaches, and a refresh shows the same entries in the new instance.
    private static async Task RecreatedAsync(ComponentActivity activity, CancellationToken token)
    {
        var views = new List<ProbeView>();
        var dialogs = new List<ProbeDialog>();
        AndroidNavigationPresenter presenter = Presenter(request =>
        {
            if (request.Placement is not AndroidPagePlacement.Modal)
            {
                views.Add(new ProbeView(request.Activity));
                return Page(views[^1]);
            }

            dialogs.Add(new ProbeDialog(request.Activity));
            return ModalPage(dialogs[^1]);
        });
        ProbeActivity.Creating = probe =>
        {
            Mount(presenter, probe);
            probe.Destroying = presenter.Detach;
        };
        ProbeActivity? current = null;
        NavigationState shown = Shows("root", "sheet-entry");
        try
        {
            current = await ProbeActivity.StartAsync(activity, token);
            await ApplyAsync(presenter, shown, NavigationState.Empty, token);
            ProbeActivity first = current;
            Task<ProbeActivity> next = ProbeActivity.NextCreated();
            first.Recreate();
            current = await next.WaitAsync(Soon, token);
            await first.Destroyed.Task.WaitAsync(Soon, token);
            Require(
                !ReferenceEquals(current, first)
                && views[0] is { Disposed: true, DetachedAtDisposal: true }
                && dialogs[0] is { Retired: true, GoneAtRetirement: true },
                "The recreated activity's old pages were not retired out of its window.");
            await ApplyAsync(presenter, shown, shown, token, NavigationPlanKind.Refresh);
            Require(
                views.Count == 2
                && dialogs.Count == 2
                && ReferenceEquals(views[1].Context, current)
                && presenter.ShownDialog == dialogs[1]
                && ReferenceEquals(Visible(current), views[1]),
                "A refresh did not show the same entries in the recreated activity.");
        }
        finally
        {
            ProbeActivity.Creating = null;
            if (current is not null)
            {
                current.Finish();
                await current.Destroyed.Task.WaitAsync(Soon, token);
            }

            await presenter.DisposeAsync();
        }
    }

    // An operation on an activity another one covers (stopped) changes nothing until that activity is started again,
    // then shows what it presented.
    private static async Task StoppedActivityAsync(ComponentActivity activity, CancellationToken token)
    {
        var views = new Dictionary<string, ProbeView>(StringComparer.Ordinal);
        AndroidNavigationPresenter presenter = Presenter(request =>
            Page(views[request.Entry.Id] = new ProbeView(request.Activity)));
        Mount(presenter, activity);
        ProbeActivity? cover = null;
        try
        {
            await ApplyAsync(presenter, Shows("root"), NavigationState.Empty, token);
            cover = await ProbeActivity.StartAsync(activity, token);
            await UntilAsync(
                () => !activity.Lifecycle.CurrentState.IsAtLeast(AndroidX.Lifecycle.Lifecycle.State.Started ?? throw new InvalidOperationException("No STARTED state.")),
                "The covered activity was never stopped.",
                token);
            Task push = ApplyAsync(presenter, Pushed("root", "pushed"), Shows("root"), token);
            await Task.WhenAny(push, Task.Delay(TimeSpan.FromSeconds(1), token));
            Require(
                !push.IsCompleted && ReferenceEquals(Visible(activity), views["root"]) && !views.ContainsKey("pushed"),
                "An operation built or changed native in a stopped activity.");
            cover.Finish();
            await cover.Destroyed.Task.WaitAsync(Soon, token);
            cover = null;
            await push.WaitAsync(Soon, token);
            Require(
                ReferenceEquals(Visible(activity), views["pushed"]) && views["pushed"].IsAttachedToWindow,
                "An operation on a stopped activity did not show its page once the activity was back.");
        }
        finally
        {
            cover?.Finish();
            await presenter.DisposeAsync();
        }
    }

    // An operation canceled while it waits for a stopped activity ends at once with nothing touched, and the next
    // operation runs once the activity is back.
    private static async Task CanceledWhileStoppedAsync(ComponentActivity activity, CancellationToken token)
    {
        var dialogs = new Dictionary<string, ProbeDialog>(StringComparer.Ordinal);
        AndroidNavigationPresenter presenter = Presenter(request => request.Placement is AndroidPagePlacement.Modal
            ? ModalPage(dialogs[request.Entry.Id] = new ProbeDialog(request.Activity))
            : Page(new ProbeView(request.Activity)));
        Mount(presenter, activity);
        ProbeActivity? cover = null;
        NavigationState shown = Shows("root", "sheet-entry");
        try
        {
            await ApplyAsync(presenter, shown, NavigationState.Empty, token);
            ProbeDialog original = dialogs["sheet-entry"];
            cover = await ProbeActivity.StartAsync(activity, token);
            await UntilAsync(
                () => !activity.Lifecycle.CurrentState.IsAtLeast(
                    AndroidX.Lifecycle.Lifecycle.State.Started ?? throw new InvalidOperationException("No STARTED.")),
                "The covered activity was never stopped.",
                token);
            // Twice: the second rollback restores a dialog the first showed again while the activity was stopped.
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
                Task closing = ApplyAsync(presenter, Shows("root"), shown, cancel.Token);
                bool closedModal = !original.IsShowing;
                await TurnsAsync(token);
                Require(!closedModal && original.IsShowing, "An operation changed native in a stopped activity.");
                cancel.Cancel();
                Exception failure = await FailsAsync(
                    () => closing.WaitAsync(Soon, token),
                    "A canceled operation in a stopped activity completed.");
                if (failure is TimeoutException)
                {
                    throw new TimeoutException(
                        $"Cancel {attempt} in a stopped activity did not end within {Soon.TotalSeconds}s: operation "
                        + $"{closing.Status}, presenter settled {presenter.IsSettled}, modal closed before the cancel "
                        + $"{closedModal}, modal showing {original.IsShowing}, activity "
                        + $"{activity.Lifecycle.CurrentState}.");
                }
                Require(
                    failure is OperationCanceledException
                    && original is { IsShowing: true, Retired: false }
                    && presenter.ShownDialog == original,
                    $"Cancel {attempt} in a stopped activity did not end promptly with the same modal restored "
                    + $"({failure.GetType().Name}: {failure.Message}).");
            }
            Task next = ApplyAsync(presenter, shown, shown, token, NavigationPlanKind.Refresh);
            cover.Finish();
            await Labelled(cover.Destroyed.Task, "The covering activity was not destroyed.");
            cover = null;
            try
            {
                await Labelled(next, "The refresh after the activity came back did not finish.");
            }
            catch (TimeoutException late)
            {
                throw new TimeoutException(
                    $"{late.Message} Modal showing {original.IsShowing}, its window attached "
                    + $"{original.Window?.DecorView.IsAttachedToWindow}, shown {original.Window?.DecorView.IsShown}, "
                    + $"laid out {original.Window?.DecorView.IsLaidOut}; activity {activity.Lifecycle.CurrentState}, "
                    + $"presenter settled {presenter.IsSettled}.");
            }
            Require(
                presenter.ShownDialog == original && dialogs["sheet-entry"] == original,
                "The operation after a canceled one in a stopped activity did not keep the restored modal.");
        }
        finally
        {
            cover?.Finish();
            await presenter.DisposeAsync();
        }
    }

    // A wait that names itself, and the presenter's and the activity's state, when it times out.
    private static async Task Labelled(Task task, string what)
    {
        try
        {
            await task.WaitAsync(Soon);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{what} (within {Soon.TotalSeconds}s)");
        }
    }

    // An activity that detaches while an operation builds its page cancels that build; the operation waits, and builds
    // the page again in the next attached activity.
    private static async Task WaitsForNextActivityAsync(ComponentActivity activity, CancellationToken token)
    {
        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool firstCanceled = false;
        int builds = 0;
        AndroidNavigationPresenter presenter = Presenter(async (request, cancellation) =>
        {
            if (++builds == 1)
            {
                building.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellation);
                }
                catch (OperationCanceledException)
                {
                    firstCanceled = true;
                    throw;
                }
            }

            return Page(new ProbeView(request.Activity));
        });
        Mount(presenter, activity);
        try
        {
            Task apply = ApplyAsync(presenter, Shows("root"), NavigationState.Empty, token);
            await building.Task.WaitAsync(Soon, token);
            presenter.Detach(activity);
            await TurnsAsync(token);
            Require(firstCanceled && !apply.IsCompleted, "Detaching did not cancel the build, or ended the operation.");
            Mount(presenter, activity);
            await apply.WaitAsync(Soon, token);
            Require(builds == 2 && Visible(activity) is ProbeView, "The operation did not finish in the next activity.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }

    // An activity already finishing when an operation reaches it (as a launcher relaunch that clears the task can
    // leave one) is detached; the operation waits for the next one.
    private static async Task FinishingActivityAsync(ComponentActivity activity, CancellationToken token)
    {
        var activities = new List<ComponentActivity>();
        AndroidNavigationPresenter presenter = Presenter(request =>
        {
            activities.Add(request.Activity);
            return Page(new ProbeView(request.Activity));
        });
        ProbeActivity? finishing = null;
        try
        {
            ProbeActivity.Creating = probe =>
            {
                Mount(presenter, probe);
                probe.Destroying = presenter.Detach;
                probe.Finish();
            };
            try
            {
                finishing = await ProbeActivity.StartAsync(activity, token);
            }
            finally
            {
                ProbeActivity.Creating = null;
            }

            Task apply = ApplyAsync(presenter, Shows("root"), NavigationState.Empty, token);
            await TurnsAsync(token);
            Require(!apply.IsCompleted && activities.Count == 0, "An operation built in, or ended on, a finishing activity.");
            await finishing.Destroyed.Task.WaitAsync(Soon, token);
            Mount(presenter, activity);
            await apply.WaitAsync(Soon, token);
            Require(
                activities.Count == 1 && ReferenceEquals(activities[0], activity) && Visible(activity) is ProbeView,
                "The operation did not finish in the next activity.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }

    // Disposal cancels the operation in flight, waits for it, then detaches the activity and retires every page.
    private static async Task DisposalDrainsAsync(ComponentActivity activity, CancellationToken token)
    {
        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = new ProbeView(activity);
        bool canceled = false;
        AndroidNavigationPresenter presenter = Presenter(async (request, cancellation) =>
        {
            if (request.Entry.Id == "root")
                return Page(root);
            building.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                throw;
            }

            return Page(new ProbeView(request.Activity));
        });
        Mount(presenter, activity);
        await ApplyAsync(presenter, Shows("root"), NavigationState.Empty, token);
        Task apply = ApplyAsync(presenter, Shows("slow"), Shows("root"), token);
        await building.Task.WaitAsync(Soon, token);
        await presenter.DisposeAsync().AsTask().WaitAsync(Soon, token);
        Exception failure = await FailsAsync(() => apply, "Disposal did not end the operation in flight.");
        Require(
            failure is OperationCanceledException
            && canceled
            && root is { Disposed: true, DetachedAtDisposal: true }
            && Visible(activity) is null,
            "Disposal did not cancel the operation, then retire the shown page out of the window.");
    }
}

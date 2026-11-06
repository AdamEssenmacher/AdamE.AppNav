using AdamE.AppNav.Plans;
using AdamE.AppNav.State;
using AndroidX.Activity;
using static AdamE.AppNav.Android.NativeChecks.Checks;

namespace AdamE.AppNav.Android.NativeChecks;

/// <summary>How a modal dialog closes: Back and its own cancel ask the router; anything else asks for a refresh.
/// </summary>
internal static class ModalChecks
{
    private const string SheetEntry = "sheet-entry";

    public static IEnumerable<(string Name, Func<ComponentActivity, CancellationToken, Task> Check)> All =>
    [
        ("modal-back", ModalBackAsync),
        ("back-key", BackKeyAsync),
        ("modal-cancel-refused", CancelRefusedAsync),
        ("modal-cancel-accepted", CancelAcceptedAsync),
        ("modal-unexpected-dismissal", UnexpectedDismissalAsync)
    ];

    private static async Task<(AndroidNavigationPresenter Presenter, RecordingHost Host, ProbeDialog Dialog)> ShownAsync(
        ComponentActivity activity,
        Dictionary<string, ProbeDialog> dialogs,
        CancellationToken token)
    {
        var host = new RecordingHost();
        AndroidNavigationPresenter presenter = Presenter(
            request => request.Placement is AndroidPagePlacement.Modal
                ? ModalPage(dialogs[request.Entry.Id] = new ProbeDialog(request.Activity))
                : Page(new ProbeView(request.Activity)),
            host);
        Mount(presenter, activity);
        await ApplyAsync(presenter, Shows("root", SheetEntry), NavigationState.Empty, token);
        return (presenter, host, dialogs[SheetEntry]);
    }

    // Back on the dialog is a Back request for its modal; the dialog stays until the router answers.
    private static async Task ModalBackAsync(ComponentActivity activity, CancellationToken token)
    {
        var dialogs = new Dictionary<string, ProbeDialog>(StringComparer.Ordinal);
        (AndroidNavigationPresenter presenter, RecordingHost host, ProbeDialog dialog) =
            await ShownAsync(activity, dialogs, token);
        try
        {
            dialog.OnBackPressedDispatcher.OnBackPressed();
            await UntilAsync(() => host.ModalBacks.Count == 1, "Back on a modal was not a modal Back request.", token);
            await TurnsAsync(token);
            Require(
                host.ModalBacks is [SheetEntry]
                && host is { Backs.Count: 0, Refreshes: 0, Reports.Count: 0, OffMain: false }
                && dialog.IsShowing,
                "Back on a modal closed it by itself, or was sent as something else.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }

    // The system's own Back key, as a person presses it: on the modal's dialog, then on a pushed page.
    private static async Task BackKeyAsync(ComponentActivity activity, CancellationToken token)
    {
        var dialogs = new Dictionary<string, ProbeDialog>(StringComparer.Ordinal);
        (AndroidNavigationPresenter presenter, RecordingHost host, ProbeDialog dialog) =
            await ShownAsync(activity, dialogs, token);
        try
        {
            await UntilAsync(() => dialog.Window?.DecorView.HasWindowFocus == true, "The dialog never had focus.", token);
            await PressBackAsync();
            await UntilAsync(() => host.ModalBacks.Count == 1, "The Back key on a modal was not a modal Back.", token);
            Require(dialog.IsShowing && host.Backs.Count == 0, "The Back key closed the modal by itself.");

            await ApplyAsync(presenter, Pushed("root", "pushed"), Shows("root", SheetEntry), token);
            await UntilAsync(() => activity.HasWindowFocus, "The activity never regained focus.", token);
            await PressBackAsync();
            await UntilAsync(() => host.Backs.Count == 1, "The Back key on a pushed page was not a Back request.", token);
            Require(host.ModalBacks.Count == 1 && host.Reports.Count == 0, "The Back key was sent as something else.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }

        return;

        // An app cannot inject keys, so eng/run-android-native-checks.sh presses Back with adb when the request file
        // appears, and deletes it once it has.
        async Task PressBackAsync()
        {
            string request = Path.Combine(
                activity.FilesDir?.AbsolutePath ?? throw new InvalidOperationException("No files directory."),
                "appnav-android-press");
            await File.WriteAllTextAsync(request, "back\n", token);
            await UntilAsync(() => !File.Exists(request), "The run script did not press Back.", token);
        }
    }

    // A cancel (here the dialog's own Cancel, as a tap outside it or a drag does) asks the router; when the router
    // keeps the modal, the presenter asks for a refresh, which shows the same dialog again.
    private static async Task CancelRefusedAsync(ComponentActivity activity, CancellationToken token)
    {
        var dialogs = new Dictionary<string, ProbeDialog>(StringComparer.Ordinal);
        (AndroidNavigationPresenter presenter, RecordingHost host, ProbeDialog dialog) =
            await ShownAsync(activity, dialogs, token);
        NavigationState shown = Shows("root", SheetEntry);
        host.OnRefresh = cancellation =>
            ApplyAsync(presenter, shown, shown, cancellation, NavigationPlanKind.Refresh);
        try
        {
            dialog.Cancel();
            await UntilAsync(() => host.Refreshes == 1, "A refused cancel did not ask for a refresh.", token);
            await UntilAsync(() => dialog.IsShowing, "The refresh did not show the kept modal again.", token);
            Require(
                host.ModalBacks is [SheetEntry]
                && host.Reports.Count == 0
                && !dialog.Retired
                && dialogs.Count == 1
                && presenter.ShownDialog == dialog,
                "A refused cancel was not a modal Back request, or the same dialog was not shown again.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }

    private static async Task CancelAcceptedAsync(ComponentActivity activity, CancellationToken token)
    {
        var dialogs = new Dictionary<string, ProbeDialog>(StringComparer.Ordinal);
        (AndroidNavigationPresenter presenter, RecordingHost host, ProbeDialog dialog) =
            await ShownAsync(activity, dialogs, token);
        host.OnModalBack = (_, cancellation) =>
            ApplyAsync(presenter, Shows("root"), Shows("root", SheetEntry), cancellation);
        try
        {
            dialog.Cancel();
            await UntilAsync(() => dialog.Retired, "An accepted cancel did not retire the modal.", token);
            await TurnsAsync(token);
            Require(
                host is { Refreshes: 0, Reports.Count: 0 } && host.ModalBacks is [SheetEntry] && dialog.GoneAtRetirement,
                "An accepted cancel asked for a refresh, or the modal was disposed in the window.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }

    // A dialog dismissed by something other than the presenter or its own cancel is reported and refreshed.
    private static async Task UnexpectedDismissalAsync(ComponentActivity activity, CancellationToken token)
    {
        var dialogs = new Dictionary<string, ProbeDialog>(StringComparer.Ordinal);
        (AndroidNavigationPresenter presenter, RecordingHost host, ProbeDialog dialog) =
            await ShownAsync(activity, dialogs, token);
        try
        {
            dialog.Dismiss();
            await UntilAsync(() => host.Refreshes == 1, "An unexpected dismissal did not ask for a refresh.", token);
            Require(
                host is { Reports.Count: 1, ModalBacks.Count: 0 },
                "An unexpected dismissal was not reported, or was taken for a Back request.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }
}

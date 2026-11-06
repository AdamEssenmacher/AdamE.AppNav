using AdamE.AppNav.State;
using AndroidX.Activity;
using static AdamE.AppNav.Android.NativeChecks.Checks;

namespace AdamE.AppNav.Android.NativeChecks;

/// <summary>The declared shape is checked before anything is built, and an owned modal stands only over its owner.
/// </summary>
internal static class TopologyChecks
{
    public static IEnumerable<(string Name, Func<ComponentActivity, CancellationToken, Task> Check)> All =>
    [
        ("topology-refused-before-building", RefusedBeforeBuildingAsync),
        ("topology-owned-modal", OwnedModalAsync)
    ];

    private static async Task RefusedBeforeBuildingAsync(ComponentActivity activity, CancellationToken token)
    {
        var built = new List<string>();
        var root = new ProbeView(activity);
        AndroidNavigationPresenter presenter = Presenter(request =>
        {
            built.Add(request.Entry.Id);
            return Page(request.Entry.Id == "root" ? root : new ProbeView(activity));
        });
        Mount(presenter, activity);
        try
        {
            NavigationState shown = Shows("root");
            await ApplyAsync(presenter, shown, NavigationState.Empty, token);
            built.Clear();
            NavigationState[] refused =
            [
                // Undeclared modal.
                new([new WindowNode(Window, new StackNode(Stack, [Entry("root")]), [new ModalNode("other", Entry("x"))])],
                    Window),
                // Deeper than this presenter shows.
                new([new WindowNode(Window, new StackNode(Stack, [Entry("root"), Entry("a"), Entry("b")]))], Window),
                // A modal declared over the root page alone, over a pushed page.
                Pushed("root", "pushed", new ModalNode(Sheet, Entry("sheet-entry"))),
                // Another stack.
                new([new WindowNode(Window, new StackNode("other", [Entry("root")]))], Window)
            ];
            foreach (NavigationState state in refused)
            {
                Exception failure = await FailsAsync(
                    () => ApplyAsync(presenter, state, shown, token),
                    "An undeclared shape was presented.");
                Require(failure is NotSupportedException, $"An undeclared shape failed with {failure.GetType().Name}.");
            }

            Require(
                built.Count == 0
                && ReferenceEquals(Visible(activity), root)
                && root is { Disposed: false, IsAttachedToWindow: true },
                "A refused shape built a page or disturbed the accepted one.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }

    private static async Task OwnedModalAsync(ComponentActivity activity, CancellationToken token)
    {
        var dialogs = new Dictionary<string, ProbeDialog>(StringComparer.Ordinal);
        AndroidNavigationPresenter presenter = Presenter(request => request.Placement is AndroidPagePlacement.Modal
            ? ModalPage(dialogs[request.Entry.Id] = new ProbeDialog(request.Activity))
            : Page(new ProbeView(request.Activity)));
        Mount(presenter, activity);
        try
        {
            NavigationState owned = Pushed("root", "pushed", new ModalNode(Owned, Entry("answer", owner: "pushed")));
            await ApplyAsync(presenter, owned, NavigationState.Empty, token);
            Require(dialogs["answer"].IsShowing, "A modal over its owner, the top entry, was not shown.");

            Exception failure = await FailsAsync(
                () => ApplyAsync(
                    presenter,
                    Pushed("root", "pushed", new ModalNode(Owned, Entry("stale", owner: "root"))),
                    owned,
                    token),
                "A modal over an entry that is not the top was shown.");
            Require(
                failure is NotSupportedException
                && !dialogs.ContainsKey("stale")
                && dialogs["answer"].IsShowing,
                "A modal whose owner is not the top entry was built, or the accepted modal was disturbed.");

            // An owned modal goes with its owner.
            await ApplyAsync(presenter, Shows("root"), owned, token);
            await TurnsAsync(token);
            Require(
                dialogs["answer"] is { Retired: true, GoneAtRetirement: true },
                "An owned modal was not retired with its owner, or was disposed while still in the window.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }
}

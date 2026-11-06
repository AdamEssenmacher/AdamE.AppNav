using AdamE.AppNav.State;
using AndroidX.Activity;
using static AdamE.AppNav.Android.NativeChecks.Checks;

namespace AdamE.AppNav.Android.NativeChecks;

/// <summary>Presentation, its retirement order, refused pages and unsafe retirement.</summary>
internal static class PresenterChecks
{
    public static IEnumerable<(string Name, Func<ComponentActivity, CancellationToken, Task> Check)> All =>
    [
        ("empty-startup", EmptyStartupAsync),
        ("healthy-presentation", HealthyPresentationAsync),
        ("retirement-order", RetirementOrderAsync),
        ("page-refused", RefusedAsync),
        ("retirement-failure-is-sticky", RetirementFailureAsync),
        ("factory-cleanup-failure", FactoryCleanupFailureAsync),
        ("activity-back", ActivityBackAsync)
    ];

    // A fresh frame does not draw before its first operation; a plan with no window presents nothing, and once it has
    // been presented the window draws.
    private static async Task EmptyStartupAsync(ComponentActivity activity, CancellationToken token)
    {
        int built = 0;
        AndroidNavigationPresenter presenter = Presenter(_ =>
        {
            built++;
            return Page(new ProbeView(activity));
        });
        Mount(presenter, activity);
        try
        {
            Require(
                !await DrawsAsync(activity, TimeSpan.FromMilliseconds(500), token),
                "A fresh frame drew before its first operation.");
            await ApplyAsync(presenter, NavigationState.Empty, NavigationState.Empty, token);
            Require(built == 0 && Visible(activity) is null, "An empty plan built or showed a page.");
            Require(await DrawsAsync(activity, Soon, token), "The window never drew after an empty plan was presented.");
            await ApplyAsync(presenter, Shows("root"), NavigationState.Empty, token);
            Require(built == 1 && Visible(activity) is ProbeView { IsAttachedToWindow: true }, "The root was not shown.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }

    // Root, a page pushed over it, back to the root, a modal over it, and closed again. The root page is built once and
    // stays built under the pushed page; each page that leaves is disposed after it left the window.
    private static async Task HealthyPresentationAsync(ComponentActivity activity, CancellationToken token)
    {
        var views = new Dictionary<string, ProbeView>(StringComparer.Ordinal);
        var dialogs = new Dictionary<string, ProbeDialog>(StringComparer.Ordinal);
        var requests = new List<AndroidPageRequest>();
        bool currencyOffMain = false;
        AndroidNavigationPresenter presenter = Presenter(request =>
        {
            requests.Add(request);
            return request.Placement is AndroidPagePlacement.Modal
                ? ModalPage(dialogs[request.Entry.Id] = new ProbeDialog(request.Activity), Current)
                : Page(views[request.Entry.Id] = new ProbeView(request.Activity), Current);
        });
        Mount(presenter, activity);
        try
        {
            NavigationState root = Shows("root");
            await ApplyAsync(presenter, root, NavigationState.Empty, token);
            Require(ReferenceEquals(Visible(activity), views["root"]), "The root page was not shown.");

            NavigationState pushed = Pushed("root", "pushed");
            // Applied from the thread pool, as a router does after its own awaits.
            await Task.Run(() => ApplyAsync(presenter, pushed, root, token), token);
            Require(
                ReferenceEquals(Visible(activity), views["pushed"])
                && views["root"] is { Disposed: false, Parent: null },
                "The pushed page did not take the frame, or the root page under it was retired.");

            await ApplyAsync(presenter, root, pushed, token);
            Require(
                ReferenceEquals(Visible(activity), views["root"])
                && views["pushed"] is { Disposed: true, DetachedAtDisposal: true },
                "Going back did not show the original root page, or the pushed page was disposed in the window.");

            NavigationState sheet = Shows("root", "sheet-entry");
            await ApplyAsync(presenter, sheet, root, token);
            ProbeDialog dialog = dialogs["sheet-entry"];
            Require(
                dialog.IsShowing && presenter.ShownDialog == dialog && ReferenceEquals(Visible(activity), views["root"]),
                "The modal was not shown over the root page.");

            await ApplyAsync(presenter, root, sheet, token);
            Require(
                dialog is { Retired: true, GoneAtRetirement: true }
                && presenter.ShownDialog is null,
                "The closed modal was not out of the window when its loan was released.");
            Require(
                requests.Count == 3
                && requests.All(request => ReferenceEquals(request.Activity, activity))
                && requests.Select(static request => request.Placement).SequenceEqual(
                    [AndroidPagePlacement.Root, AndroidPagePlacement.Pushed, AndroidPagePlacement.Modal]),
                "The factory was not asked once per page, with its placement and the attached activity.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }

        Require(
            views["root"] is { Disposed: true, DetachedAtDisposal: true },
            "Disposal did not retire the shown root page out of the window.");
        Require(!currencyOffMain, "A page's IsCurrent was called off the main thread.");
        return;

        bool Current()
        {
            currencyOffMain |= !OnMain;
            return true;
        }
    }

    // The binding first, before anything is disposed; then out of the window and disposed; then the loan.
    private static async Task RetirementOrderAsync(ComponentActivity activity, CancellationToken token)
    {
        var order = new List<string>();
        var view = new ProbeView(activity);
        var dialog = new ProbeDialog(activity);
        view.Disposing = () => order.Add("view-disposed");
        AndroidNavigationPresenter presenter = Presenter(request => request.Entry.Id switch
        {
            "root" => Page(
                view,
                binding: new ActionDisposable(() => order.Add(view.Disposed ? "binding-late" : "binding-first")),
                loan: new ActionDisposable(() => order.Add(view.Disposed ? "loan-after" : "loan-early"))),
            "sheet-entry" => ModalPage(
                dialog,
                binding: new ActionDisposable(() => order.Add("sheet-binding")),
                loan: new ActionDisposable(() => order.Add(dialog.Gone ? "sheet-loan-after" : "sheet-loan-early"))),
            _ => Page(new ProbeView(activity))
        });
        Mount(presenter, activity);
        try
        {
            await ApplyAsync(presenter, Shows("root", "sheet-entry"), NavigationState.Empty, token);
            await ApplyAsync(presenter, Shows("next"), Shows("root", "sheet-entry"), token);
        }
        finally
        {
            await presenter.DisposeAsync();
        }

        Require(
            order.SequenceEqual(
            [
                "sheet-binding",
                "sheet-loan-after",
                "binding-first",
                "view-disposed",
                "loan-after"
            ]),
            $"Pages did not retire binding, native, loan in order: {string.Join(", ", order)}.");
        Require(view.DetachedAtDisposal, "A page was disposed while still in the window.");
    }

    // A page of the wrong kind, a view that already has a parent, and one view for two entries are refused before
    // native changes; the refused pages are retired and the accepted page stays.
    private static async Task RefusedAsync(ComponentActivity activity, CancellationToken token)
    {
        var accepted = new ProbeView(activity);
        var shared = new ProbeView(activity);
        var parented = new ProbeView(activity);
        var foreignParent = new global::Android.Widget.FrameLayout(activity);
        foreignParent.AddView(parented);
        var wrongKind = new ProbeDialog(activity);
        string mode = "";
        AndroidNavigationPresenter presenter = Presenter(request => (mode, request.Entry.Id) switch
        {
            (_, "root") => Page(accepted),
            ("kind", _) => ModalPage(wrongKind),
            ("parent", _) => Page(parented),
            ("shared", "a") or ("shared", "b") => Page(shared),
            _ => Page(new ProbeView(activity))
        });
        Mount(presenter, activity);
        try
        {
            NavigationState root = Shows("root");
            await ApplyAsync(presenter, root, NavigationState.Empty, token);
            foreach ((string name, NavigationState state) in new[]
                     {
                         ("kind", Shows("other")),
                         ("parent", Shows("other")),
                         ("shared", new NavigationState(
                             [new WindowNode(Window, new StackNode(Stack, [Entry("a"), Entry("b")]))],
                             Window))
                     })
            {
                mode = name;
                Exception failure = await FailsAsync(
                    () => ApplyAsync(presenter, state, root, token),
                    $"A {name} page was presented.");
                Require(
                    failure is InvalidOperationException && ReferenceEquals(Visible(activity), accepted),
                    $"A {name} page was not refused before native changed ({failure.GetType().Name}).");
            }

            await TurnsAsync(token);
            Require(
                wrongKind.Retired && shared.Disposed && parented.Disposed,
                "A refused page was not retired.");
        }
        finally
        {
            await presenter.DisposeAsync();
        }
    }

    // A page whose retirement fails keeps its loan, and the failure fails every later operation and disposal.
    private static async Task RetirementFailureAsync(ComponentActivity activity, CancellationToken token)
    {
        var broken = new InvalidOperationException("binding retirement failed");
        bool loanReleased = false;
        AndroidNavigationPresenter presenter = Presenter(request => request.Entry.Id == "root"
            ? Page(
                new ProbeView(activity),
                binding: new ActionDisposable(() => throw broken),
                loan: new ActionDisposable(() => loanReleased = true))
            : Page(new ProbeView(activity)));
        Mount(presenter, activity);
        Exception? disposal = null;
        try
        {
            await ApplyAsync(presenter, Shows("root"), NavigationState.Empty, token);
            Exception first = await FailsAsync(
                () => ApplyAsync(presenter, Shows("next"), Shows("root"), token),
                "A failed retirement was not reported.");
            Exception later = await FailsAsync(
                () => ApplyAsync(presenter, Shows("later"), Shows("root"), token),
                "An operation after a failed retirement ran.");
            Require(
                Contains(first, broken) && Contains(later, broken) && first is AndroidCleanupFailureException or AggregateException,
                "The retirement failure did not fail the operation and the next one.");
        }
        finally
        {
            try
            {
                await presenter.DisposeAsync();
            }
            catch (Exception failure)
            {
                disposal = failure;
            }
        }

        Require(disposal is not null && Contains(disposal, broken), "Disposal did not keep the retirement failure.");
        Require(!loanReleased, "A page whose retirement failed released its loan.");
    }

    // A factory that cannot retire what it started keeps the entry's lease attached and fails what follows.
    private static async Task FactoryCleanupFailureAsync(ComponentActivity activity, CancellationToken token)
    {
        var leases = new AdamE.AppNav.Leases.RouteEntryLeaseRegistry(new MainThreadLeaseHost());
        var cause = new InvalidOperationException("half-built page");
        AndroidNavigationPresenter presenter = Presenter(
            (request, _) => request.Entry.Id == "broken"
                ? throw new AndroidCleanupFailureException("cleanup failed", cause)
                : ValueTask.FromResult(Page(new ProbeView(activity))),
            options: new AndroidNavigationPresenterOptions { EntryLeases = leases });
        Mount(presenter, activity);
        AdamE.AppNav.Leases.RouteEntryLease lease = leases.Open("broken");
        Exception? disposal = null;
        try
        {
            Exception failure = await FailsAsync(
                () => ApplyAsync(presenter, Shows("broken"), NavigationState.Empty, token),
                "A factory cleanup failure was not reported.");
            Exception later = await FailsAsync(
                () => ApplyAsync(presenter, Shows("fine"), NavigationState.Empty, token),
                "An operation after a factory cleanup failure ran.");
            Require(Contains(failure, cause) && Contains(later, cause), "The cleanup failure was not sticky.");
            lease.Retire();
            lease.Close();
            await TurnsAsync(token);
            Require(!lease.Released.IsCompleted, "The lease of a page that could not be retired released.");
        }
        finally
        {
            try
            {
                await presenter.DisposeAsync();
            }
            catch (Exception failure)
            {
                disposal = failure;
            }
        }

        Require(disposal is not null && Contains(disposal, cause), "Disposal did not keep the factory cleanup failure.");
    }

    // System Back is the router's while a page is pushed, and the activity's own over the root page alone. The host is
    // called on the main thread, in a later turn, and only if the activity is still attached by then.
    private static async Task ActivityBackAsync(ComponentActivity activity, CancellationToken token)
    {
        var host = new RecordingHost();
        AndroidNavigationPresenter presenter = Presenter(_ => Page(new ProbeView(activity)), host);
        Mount(presenter, activity);
        try
        {
            await ApplyAsync(presenter, Shows("root"), NavigationState.Empty, token);
            Require(
                !activity.OnBackPressedDispatcher.HasEnabledCallbacks,
                "The presenter took Back over the root page alone.");
            await ApplyAsync(presenter, Pushed("root", "pushed"), Shows("root"), token);
            Require(activity.OnBackPressedDispatcher.HasEnabledCallbacks, "The presenter did not take Back while pushed.");
            activity.OnBackPressedDispatcher.OnBackPressed();
            Require(host.Backs.Count == 0, "The host was called inside the Back event, not in a later turn.");
            await UntilAsync(() => host.Backs.Count == 1, "Back while pushed was not a Back request.", token);
            Require(!host.OffMain && host.ModalBacks.Count == 0, "Back was sent off the main thread or as a modal Back.");

            // A Back whose activity detaches before its request is delivered never reaches the host.
            activity.OnBackPressedDispatcher.OnBackPressed();
            presenter.Detach(activity);
            await TurnsAsync(token);
            Require(host.Backs.Count == 1, "A Back from an activity that detached reached the host.");
            Mount(presenter, activity);
        }
        finally
        {
            await presenter.DisposeAsync();
        }

        Require(
            !activity.OnBackPressedDispatcher.HasEnabledCallbacks,
            "The presenter's Back callback outlived its disposal.");
    }
}

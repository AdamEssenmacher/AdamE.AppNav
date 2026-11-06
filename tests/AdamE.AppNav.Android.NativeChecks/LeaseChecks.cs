using AdamE.AppNav.Leases;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;
using AndroidX.Activity;
using static AdamE.AppNav.Android.NativeChecks.Checks;

namespace AdamE.AppNav.Android.NativeChecks;

/// <summary>Leased pages through a real AppNav router: the presenter attaches each page to its entry's lease, and shows
/// a placeholder for a retired lease the router still shows.</summary>
internal static class LeaseChecks
{
    public static IEnumerable<(string Name, Func<ComponentActivity, CancellationToken, Task> Check)> All =>
    [
        ("lease-release-order", ReleaseOrderAsync),
        ("lease-retired-during-build", RetiredDuringBuildAsync),
        ("lease-custom-placeholders", CustomPlaceholdersAsync),
        ("lease-opened-by-presenter", OpenedByPresenterAsync),
        ("router-host", RouterHostAsync)
    ];

    private static async Task ReleaseOrderAsync(ComponentActivity activity, CancellationToken token)
    {
        var host = new MainThreadLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var opened = new Dictionary<string, RouteEntryLease>(StringComparer.Ordinal);
        var views = new Dictionary<string, ProbeView>(StringComparer.Ordinal);
        var navigationHost = new RecordingHost();
        string? retireWhilePlanning = null;
        int built = 0;
        AndroidNavigationPresenter presenter = Presenter(
            request =>
            {
                built++;
                return Page(views[request.Entry.Id] = new ProbeView(request.Activity));
            },
            navigationHost,
            new AndroidNavigationPresenterOptions { EntryLeases = leases });
        Mount(presenter, activity);
        IRouterNavigator router = RouterChecks.Router(
            new OneRootPlanner(name =>
            {
                if (name == retireWhilePlanning)
                    opened[name].Retire();
            }),
            presenter,
            leases);
        try
        {
            (RouteEntryLease first, ReleaseProbe firstProbe) = Open("lease-first");
            await NavigateAsync("lease-first");
            firstProbe.Watch(views["lease-first"]);
            Require(first.IsCommitted && !firstProbe.Released, "A shown leased root released what it borrows.");

            (RouteEntryLease second, ReleaseProbe secondProbe) = Open("lease-second");
            await NavigateAsync("lease-second");
            await first.Released.WaitAsync(Soon, token);
            Require(
                first.Retirement == RouteEntryLeaseRetirement.Dropped && firstProbe.ReleasedAfterNativeDisposal,
                "A dropped leased root released what it borrows before its view left and was disposed.");

            // A retired lease whose entry is still committed is shown as a placeholder that borrows nothing.
            ProbeView secondShown = views["lease-second"];
            secondProbe.Watch(secondShown);
            int beforePlaceholder = built;
            second.Retire();
            await router.RefreshPresentationAsync(token);
            global::Android.Views.View? placeholder = Visible(activity);
            await second.Released.WaitAsync(Soon, token);
            Require(
                placeholder is not null
                && !ReferenceEquals(placeholder, secondShown)
                && built == beforePlaceholder
                && second.IsCommitted
                && secondProbe.ReleasedAfterNativeDisposal,
                "A retired committed lease was not replaced by a placeholder, without the factory, before it released.");

            // A lease that retires before its entry is committed fails the presentation before anything is shown.
            (RouteEntryLease third, ReleaseProbe thirdProbe) = Open("lease-third");
            retireWhilePlanning = "lease-third";
            bool refused = false;
            try
            {
                await NavigateAsync("lease-third");
            }
            catch (OperationCanceledException failure)
            {
                refused = failure.Message.Contains("'lease-third' retired before its page was shown", StringComparison.Ordinal);
            }

            await third.Released.WaitAsync(Soon, token);
            Require(
                refused && !third.IsCommitted && ReferenceEquals(Visible(activity), placeholder) && thirdProbe.Released,
                "A lease retired before commit was shown, or did not release.");
        }
        finally
        {
            // The adapter's teardown starts alongside the router's disposal, never after awaiting it.
            Task disposal = router.DisposeAsync().AsTask();
            await presenter.DisposeAsync();
            await disposal.WaitAsync(Soon, token);
        }

        Require(
            host.Failures.Count == 0 && navigationHost.Reports.Count == 0,
            "Leased native presentation reported a defect or fatal failure.");
        return;

        (RouteEntryLease Lease, ReleaseProbe Probe) Open(string entryId)
        {
            RouteEntryLease lease = leases.Open(entryId);
            var probe = new ReleaseProbe();
            lease.Own(probe.Release);
            opened[entryId] = lease;
            return (lease, probe);
        }

        async Task NavigateAsync(string entryId)
        {
            RouteEntryLease lease = opened[entryId];
            try
            {
                _ = await router.NavigateAsync(
                    RouterNavigationRequest.FromRoute(new CheckRoute(entryId), NavigationRequestSource.Test),
                    lease,
                    token);
            }
            finally
            {
                lease.Close();
            }
        }
    }

    private static async Task RetiredDuringBuildAsync(ComponentActivity activity, CancellationToken token)
    {
        foreach (bool pushed in new[] { true, false })
        {
            await RetiredDuringBuildAsync(activity, inFactory: true, pushed, token);
            await RetiredDuringBuildAsync(activity, inFactory: false, pushed, token);
        }
    }

    // A committed entry's lease retires while a refresh is rebuilding its page: inside the factory (the presenter gives
    // the lease back and builds the placeholder), or after the page is built and before the presenter accepts it (the
    // presenter applies the plan again, and the retired lease then gets the placeholder). Either way the refresh
    // succeeds, the placeholder replaces the shown page, and the lease releases once that page is gone.
    private static async Task RetiredDuringBuildAsync(
        ComponentActivity activity,
        bool inFactory,
        bool pushed,
        CancellationToken token)
    {
        string name = (inFactory ? "lease-factory" : "lease-built") + (pushed ? "-pushed" : "");
        const string rootId = "lease-root";
        var rootView = new ProbeView(activity);
        var host = new MainThreadLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var navigationHost = new RecordingHost();
        RouteEntryLease lease = leases.Open(name);
        var probe = new ReleaseProbe();
        lease.Own(probe.Release);
        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool hold = false;
        bool firstReusable = true;
        int pages = 0;
        ProbeView? shown = null;
        AndroidNavigationPresenter presenter = Presenter(
            CreatePageAsync,
            navigationHost,
            new AndroidNavigationPresenterOptions { EntryLeases = leases });
        Mount(presenter, activity);
        IRouterNavigator router = RouterChecks.Router(
            pushed ? new PushedPlanner(rootId) : new OneRootPlanner(),
            presenter,
            leases);
        try
        {
            try
            {
                _ = await router.NavigateAsync(
                    RouterNavigationRequest.FromRoute(new CheckRoute(name), NavigationRequestSource.Test),
                    lease,
                    token);
            }
            finally
            {
                lease.Close();
            }

            ProbeView first = shown ?? throw new InvalidOperationException("The leased page was not shown.");
            probe.Watch(first);
            // Stands in for any reason the refresh cannot reuse the shown page: it is built again.
            firstReusable = false;
            hold = true;
            Task refresh = router.RefreshPresentationAsync(token).AsTask();
            await building.Task.WaitAsync(Soon, token);
            lease.Retire();
            await refresh.WaitAsync(Soon, token);
            global::Android.Views.View? placeholder = Visible(activity);
            await lease.Released.WaitAsync(Soon, token);
            Require(
                placeholder is not null
                && placeholder is not ProbeView
                && (!pushed || rootView is { Disposed: false })
                && lease.IsCommitted
                && probe.ReleasedAfterNativeDisposal,
                $"A lease retired {(inFactory ? "inside the factory" : "after its page was built")}"
                + $"{(pushed ? " (pushed over the root page)" : "")} was not replaced by a placeholder before it "
                + "released.");
        }
        finally
        {
            Task disposal = router.DisposeAsync().AsTask();
            await presenter.DisposeAsync();
            await disposal.WaitAsync(Soon, token);
        }

        Require(
            host.Failures.Count == 0 && navigationHost.Reports.Count == 0,
            "A lease retired during its build reported a defect or fatal failure.");
        return;

        async ValueTask<AndroidPage> CreatePageAsync(AndroidPageRequest request, CancellationToken cancellation)
        {
            if (request.Entry.Id == rootId)
                return Page(rootView);
            if (++pages == 1)
                return Page(shown = new ProbeView(request.Activity), () => firstReusable);
            if (!hold)
                return Page(new ProbeView(request.Activity));
            hold = false;
            if (inFactory)
            {
                building.TrySetResult();
                // Canceled by the lease's retirement, as a real factory's waits are.
                await Task.Delay(Timeout.Infinite, cancellation);
            }

            // The application retires the lease as the factory returns its page: after the build, before the presenter
            // accepts it.
            AndroidPage built = Page(new ProbeView(request.Activity));
            lease.Retire();
            building.TrySetResult();
            return built;
        }
    }

    // The application's placeholders replace retired entries the router still shows: a view for a root, a dialog for
    // a modal. The presenter owns both, and retires both once the router drops their entries.
    private static async Task CustomPlaceholdersAsync(ComponentActivity activity, CancellationToken token)
    {
        var host = new MainThreadLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var placeholderView = new ProbeView(activity);
        var placeholderDialog = new ProbeDialog(activity);
        var placements = new List<AndroidPagePlacement>();
        AndroidNavigationPresenter presenter = Presenter(
            request => request.Placement is AndroidPagePlacement.Modal
                ? ModalPage(new ProbeDialog(request.Activity))
                : Page(new ProbeView(request.Activity)),
            options: new AndroidNavigationPresenterOptions
            {
                EntryLeases = leases,
                CreatePlaceholderView = (_, placement, _) =>
                {
                    placements.Add(placement);
                    return placeholderView;
                },
                CreatePlaceholderDialog = (_, _) =>
                {
                    placements.Add(AndroidPagePlacement.Modal);
                    return placeholderDialog;
                }
            });
        Mount(presenter, activity);
        IRouterNavigator router = RouterChecks.Router(new PushedPlanner("custom-root"), presenter, leases);
        try
        {
            RouteEntryLease root = leases.Open("custom-root");
            RouteEntryLease modal = leases.Open("modal:custom");
            try
            {
                _ = await router.NavigateAsync(
                    RouterNavigationRequest.FromRoute(new CheckRoute("modal:custom"), NavigationRequestSource.Test),
                    modal,
                    token);
            }
            finally
            {
                modal.Close();
                root.Close();
            }

            modal.Retire();
            root.Retire();
            await router.RefreshPresentationAsync(token);
            await modal.Released.WaitAsync(Soon, token);
            await root.Released.WaitAsync(Soon, token);
            Require(
                placements.Order().SequenceEqual([AndroidPagePlacement.Root, AndroidPagePlacement.Modal])
                && ReferenceEquals(Visible(activity), placeholderView)
                && presenter.ShownDialog == placeholderDialog,
                "The custom placeholders were not shown for the retired root and modal.");
        }
        finally
        {
            Task disposal = router.DisposeAsync().AsTask();
            await presenter.DisposeAsync();
            await disposal.WaitAsync(Soon, token);
        }

        Require(
            placeholderView.Disposed && placeholderDialog.Gone && host.Failures.Count == 0,
            "The presenter did not retire its custom placeholders, or a lease failure was reported.");
    }

    // An entry the application opened no lease for gets one the presenter opens while presenting. Its page is attached
    // to it, and it releases once the router drops the entry and the page is gone.
    private static async Task OpenedByPresenterAsync(ComponentActivity activity, CancellationToken token)
    {
        var host = new MainThreadLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var shown = new Dictionary<string, (ProbeView View, RouteEntryLease? Lease)>(StringComparer.Ordinal);
        AndroidNavigationPresenter presenter = Presenter(
            request =>
            {
                var view = new ProbeView(request.Activity);
                shown[request.Entry.Id] = (view, request.Lease);
                return Page(view);
            },
            options: new AndroidNavigationPresenterOptions { EntryLeases = leases });
        Mount(presenter, activity);
        IRouterNavigator router = RouterChecks.Router(new OneRootPlanner(), presenter, leases);
        try
        {
            _ = await router.NavigateAsync(
                RouterNavigationRequest.FromRoute(new CheckRoute("presenter-opened"), NavigationRequestSource.Test),
                token);
            Require(
                shown.TryGetValue("presenter-opened", out var first)
                && first.Lease is { IsCommitted: true, IsRetired: false } opened
                && leases.TryGet("presenter-opened", out RouteEntryLease? registered)
                && ReferenceEquals(registered, opened)
                && ReferenceEquals(Visible(activity), first.View),
                "An entry without a lease was not shown with one the presenter opened.");
            _ = await router.NavigateAsync(
                RouterNavigationRequest.FromRoute(new CheckRoute("presenter-next"), NavigationRequestSource.Test),
                token);
            RouteEntryLease dropped = first.Lease ?? throw new InvalidOperationException("No lease.");
            await dropped.Released.WaitAsync(Soon, token);
            Require(
                dropped.Retirement == RouteEntryLeaseRetirement.Dropped && first.View is { Disposed: true },
                "The presenter-opened lease did not release after its page was gone.");
        }
        finally
        {
            Task disposal = router.DisposeAsync().AsTask();
            await presenter.DisposeAsync();
            await disposal.WaitAsync(Soon, token);
        }

        Require(host.Failures.Count == 0, "A presenter-opened lease reported a failure.");
    }

    // The router-backed host: Back over a pushed page pops it; Back and cancel on a modal close it; a modal Back that
    // arrives after its modal has gone does nothing.
    private static async Task RouterHostAsync(ComponentActivity activity, CancellationToken token)
    {
        var host = new MainThreadLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var survived = new List<Exception>();
        IRouterNavigator? router = null;
        var navigationHost = new AndroidRouterNavigationHost(
            () => router ?? throw new InvalidOperationException("No router yet."),
            Topology,
            survived.Add);
        var dialogs = new Dictionary<string, ProbeDialog>(StringComparer.Ordinal);
        var presenter = new AndroidNavigationPresenter(
            Topology,
            new RequestPages(request => request.Placement is AndroidPagePlacement.Modal
                ? ModalPage(dialogs[request.Entry.Id] = new ProbeDialog(request.Activity))
                : Page(new ProbeView(request.Activity))),
            navigationHost,
            new AndroidNavigationPresenterOptions { EntryLeases = leases });
        Mount(presenter, activity);
        router = RouterChecks.Router(new PushedPlanner("host-root"), presenter, leases);
        try
        {
            await NavigateAsync("host-pushed");
            Require(activity.OnBackPressedDispatcher.HasEnabledCallbacks, "Back was not taken while pushed.");
            activity.OnBackPressedDispatcher.OnBackPressed();
            await UntilAsync(
                () => router.CurrentState.ActiveWindow?.Root is StackNode { Entries.Count: 1 },
                "Back over a pushed page did not pop it.",
                token);

            foreach (bool cancel in new[] { false, true })
            {
                string id = cancel ? "modal:canceled" : "modal:back";
                await NavigateAsync(id);
                ProbeDialog dialog = dialogs[id];
                if (cancel)
                    dialog.Cancel();
                else
                    dialog.OnBackPressedDispatcher.OnBackPressed();
                await UntilAsync(
                    () => router.CurrentState.ActiveWindow is { Modals.Count: 0 } && dialog.Retired,
                    $"{(cancel ? "Cancel" : "Back")} on a modal did not close it through the router.",
                    token);
            }

            // A cancel that reaches the router after its modal has gone names that modal, so nothing else closes.
            await NavigateAsync("host-pushed-again");
            await navigationHost.RequestModalBackAsync("modal:canceled", token);
            Require(
                router.CurrentState.ActiveWindow?.Root is StackNode { Entries.Count: 2 },
                "A modal Back for a modal that had gone popped the stack.");
        }
        finally
        {
            Task disposal = router.DisposeAsync().AsTask();
            await presenter.DisposeAsync();
            await disposal.WaitAsync(Soon, token);
        }

        Require(
            host.Failures.Count == 0 && survived.Count == 0,
            $"The router host reported a failure: {string.Join("; ", survived.Select(static f => f.Message))}");
        return;

        async Task NavigateAsync(string id) =>
            _ = await router.NavigateAsync(
                RouterNavigationRequest.FromRoute(new CheckRoute(id), NavigationRequestSource.Test),
                token);
    }

    private sealed class RequestPages(Func<AndroidPageRequest, AndroidPage> pages) : IAndroidPageFactory
    {
        public ValueTask<AndroidPage> CreatePageAsync(AndroidPageRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(pages(request));
    }
}

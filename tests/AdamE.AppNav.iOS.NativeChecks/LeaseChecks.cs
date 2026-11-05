using AdamE.AppNav.Leases;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using ObjCRuntime;
using static AdamE.AppNav.iOS.NativeChecks.Checks;

namespace AdamE.AppNav.iOS.NativeChecks;

/// <summary>Leased pages through a real AppNav router: the presenter attaches each page to its entry's lease, and shows
/// a placeholder for a retired lease the router still shows.</summary>
internal static class LeaseChecks
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    public static IEnumerable<(string Name, Func<UIWindow, CancellationToken, Task> Check)> All =>
    [
        ("lease-release-order", ReleaseOrderAsync),
        ("lease-retired-during-build", RetiredDuringBuildAsync),
        ("router-host", RouterHostAsync),
        ("lease-custom-placeholder", CustomPlaceholderAsync)
    ];

    private static async Task ReleaseOrderAsync(UIWindow window, CancellationToken token)
    {
        var host = new MainThreadLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var opened = new Dictionary<string, RouteEntryLease>(StringComparer.Ordinal);
        var survived = new List<Exception>();
        string? retireWhilePlanning = null;
        int built = 0;
        UIKitNavigationPresenter presenter = Presenter(
            window,
            (_, _) =>
            {
                built++;
                return ValueTask.FromResult(Page(new UIViewController()));
            },
            report: survived.Add,
            options: new UIKitNavigationPresenterOptions { EntryLeases = leases });
        IRouterNavigator router = RouterNavigatorFactory.Create(
            new RouteTableBuilder().Build(),
            new OneRootPlanner(name =>
            {
                if (name == retireWhilePlanning)
                    opened[name].Retire();
            }),
            presenter,
            new RouterNavigatorFactoryOptions { EntryLeases = leases });
        try
        {
            // A dropped entry's lease releases only after its controller has left the window and been disposed. AppNav
            // retires a dropped lease at commit, after the presenter has already disposed it, so this step alone cannot
            // catch an early detach; the retired-while-shown step below does.
            (RouteEntryLease first, ReleaseProbe firstProbe) = Open("lease-first");
            await NavigateAsync("lease-first");
            UIViewController firstShown = window.RootViewController
                                          ?? throw new InvalidOperationException("The leased root was not shown.");
            firstProbe.Watch(firstShown);
            Require(first.IsCommitted && !firstProbe.Released, "A shown leased root released what it borrows.");

            (RouteEntryLease second, ReleaseProbe secondProbe) = Open("lease-second");
            await NavigateAsync("lease-second");
            await first.Released.WaitAsync(Soon, token);
            Require(
                first.Retirement == RouteEntryLeaseRetirement.Dropped && firstProbe.ReleasedAfterNativeDisposal,
                "A dropped leased root released what it borrows before its controller left and was disposed.");

            // A retired lease whose entry is still committed is shown as a placeholder that borrows nothing.
            UIViewController secondShown =
                window.RootViewController ?? throw new InvalidOperationException("The second root was not shown.");
            secondProbe.Watch(secondShown);
            int beforePlaceholder = built;
            second.Retire();
            await router.RefreshPresentationAsync(token);
            UIViewController? placeholder = window.RootViewController;
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
                // The presenter refused it from the lease itself, not from its later currency check.
                refused = failure.Message.Contains(
                    "'lease-third' retired before its page was shown",
                    StringComparison.Ordinal);
            }

            await third.Released.WaitAsync(Soon, token);
            Require(
                refused
                && !third.IsCommitted
                && ReferenceEquals(window.RootViewController, placeholder)
                && thirdProbe.Released,
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
            host.Failures.Count == 0 && survived.Count == 0,
            "Leased native presentation reported a defect or fatal failure.");
        return;

        (RouteEntryLease Lease, ReleaseProbe Probe) Open(string entryId)
        {
            RouteEntryLease lease = leases.Open(entryId);
            var probe = new ReleaseProbe(window);
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
                    RouterNavigationRequest.FromRoute(new LeaseRoute(entryId), NavigationRequestSource.Test),
                    lease,
                    token);
            }
            finally
            {
                lease.Close();
            }
        }
    }

    private static async Task RetiredDuringBuildAsync(UIWindow window, CancellationToken token)
    {
        foreach (bool pushed in new[] { true, false })
        {
            await RetiredDuringBuildAsync(window, inFactory: true, pushed, token);
            await RetiredDuringBuildAsync(window, inFactory: false, pushed, token);
        }
    }

    // A committed entry's lease retires while a refresh is rebuilding its page: inside the factory (the presenter gives
    // the lease back and builds the placeholder), or after the page is built and before the presenter accepts it (the
    // presenter applies the plan again, and the retired lease then gets the placeholder). Either way the refresh
    // succeeds, the placeholder replaces the shown page, and the lease releases once that page is gone. The page is the
    // window's root, or pushed over a root page that stays.
    private static async Task RetiredDuringBuildAsync(
        UIWindow window,
        bool inFactory,
        bool pushed,
        CancellationToken token)
    {
        string name = (inFactory ? "lease-factory" : "lease-built") + (pushed ? "-pushed" : "");
        const string rootId = "lease-root";
        var rootNavigation = new UINavigationController(new UIViewController());
        var host = new MainThreadLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var survived = new List<Exception>();
        RouteEntryLease lease = leases.Open(name);
        var probe = new ReleaseProbe(window);
        lease.Own(probe.Release);
        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // Never set.
        bool hold = false;
        bool firstReusable = true;
        int pages = 0;
        UIKitNavigationPresenter presenter = Presenter(
            window,
            CreatePageAsync,
            report: survived.Add,
            options: new UIKitNavigationPresenterOptions { EntryLeases = leases });
        IRouterNavigator router = RouterNavigatorFactory.Create(
            new RouteTableBuilder().Build(),
            pushed ? new PushedPlanner(rootId) : new OneRootPlanner(static _ => { }),
            presenter,
            new RouterNavigatorFactoryOptions { EntryLeases = leases });
        // The root entry is leased too, as every entry is under a registry; nothing borrows from it here.
        RouteEntryLease? rootLease = pushed ? leases.Open(rootId) : null;
        try
        {
            try
            {
                _ = await router.NavigateAsync(
                    RouterNavigationRequest.FromRoute(new LeaseRoute(name), NavigationRequestSource.Test),
                    lease,
                    token);
            }
            finally
            {
                lease.Close();
                rootLease?.Close();
            }

            UIViewController shown = Shown() ?? throw new InvalidOperationException("The leased page was not shown.");
            probe.Watch(shown);
            // Stands in for any reason the refresh cannot reuse the shown page, such as a surface a failed apply
            // cleared: the entry is still committed and its lease is not retired, so its page is built again.
            firstReusable = false;
            hold = true;
            Task refresh = router.RefreshPresentationAsync(token).AsTask();
            await building.Task.WaitAsync(Soon, token);
            // In the factory, only the retirement ends the build's wait, so the canceled build is what this proves.
            lease.Retire();
            await refresh.WaitAsync(Soon, token);
            UIViewController? placeholder = Shown();
            await lease.Released.WaitAsync(Soon, token);
            Require(
                placeholder is not null
                && !ReferenceEquals(placeholder, shown)
                && (!pushed || ReferenceEquals(window.RootViewController, rootNavigation))
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
            host.Failures.Count == 0 && survived.Count == 0,
            "A lease retired during its build reported a defect or fatal failure.");
        return;

        UIViewController? Shown() => pushed
            ? (window.RootViewController as UINavigationController)?.TopViewController
            : window.RootViewController;

        async ValueTask<UIKitPage> CreatePageAsync(RouteEntry entry, CancellationToken cancellation)
        {
            if (entry.Id == rootId)
                return StackPage(rootNavigation);
            if (++pages == 1)
                return Page(new UIViewController(), () => firstReusable);
            if (!hold)
                return Page(new UIViewController());
            hold = false;
            if (inFactory)
            {
                building.TrySetResult();
                // Canceled by the lease's retirement, as a real factory's waits are.
                await gate.Task.WaitAsync(cancellation);
                return Page(new UIViewController());
            }

            // The application retires the lease as the factory returns its page: after the build, before the presenter
            // accepts it.
            var built = Page(new UIViewController());
            lease.Retire();
            building.TrySetResult();
            return built;
        }
    }

    // The router-backed host: a pop UIKit commits is recorded as a reconciliation that shortens the stack, which retires
    // the popped entry's lease. Once on its own, and once crossing a refresh the app started on the main thread: the
    // presenter then reports the pop as that refresh ends, and the record must not run inside the refresh's router
    // operation, which AppNav refuses as reentrant.
    private static async Task RouterHostAsync(UIWindow window, CancellationToken token)
    {
        var host = new MainThreadLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var survived = new List<Exception>();
        IRouterNavigator? router = null;
        var navigationHost = new UIKitRouterNavigationHost(
            () => router ?? throw new InvalidOperationException("No router yet."),
            Topology,
            survived.Add);
        var rootContent = new UIViewController();
        var pushedPages = new Dictionary<string, UIViewController>(StringComparer.Ordinal);
        var presenter = new UIKitNavigationPresenter(
            window,
            Topology,
            new EntryPages(entry => entry.Id == "host-root"
                ? UIKitPage.InNavigationStack(rootContent)
                : new UIKitPage(pushedPages[entry.Id] = new UIViewController())),
            navigationHost,
            new UIKitNavigationPresenterOptions { EntryLeases = leases });
        router = RouterNavigatorFactory.Create(
            new RouteTableBuilder().Build(),
            new PushedPlanner("host-root"),
            presenter,
            new RouterNavigatorFactoryOptions { EntryLeases = leases });
        // The root entry is leased too, as every entry is under a registry; nothing borrows from it here.
        RouteEntryLease rootLease = leases.Open("host-root");
        try
        {
            foreach (bool crossing in new[] { false, true })
            {
                string id = crossing ? "host-crossed" : "host-pushed";
                RouteEntryLease lease = leases.Open(id);
                try
                {
                    _ = await router.NavigateAsync(
                        RouterNavigationRequest.FromRoute(new LeaseRoute(id), NavigationRequestSource.Test),
                        lease,
                        token);
                }
                finally
                {
                    lease.Close();
                    rootLease.Close();
                }

                var navigation = window.RootViewController as UINavigationController
                                 ?? throw new InvalidOperationException("The root page has no navigation stack.");
                UIViewController pushed = pushedPages[id];
                Require(
                    navigation.ViewControllers is { Length: 2 } && ReferenceEquals(pushed.View?.Window, window),
                    "The router did not push the page over the root page.");
                _ = navigation.PopViewController(true);
                Task refresh = Task.CompletedTask;
                if (crossing)
                {
                    // The pop is still animating: the refresh waits for it, stands aside for it, and reports it.
                    Require(
                        navigation.GetTransitionCoordinator() is not null,
                        "An animated pop had no transition in flight.");
                    refresh = router.RefreshPresentationAsync(token).AsTask();
                }

                try
                {
                    await refresh.WaitAsync(Soon, token);
                }
                // The record retires the refreshed entry; the refresh then reads as obsolete.
                catch (OperationCanceledException) when (lease.IsRetired) { }

                await lease.Released.WaitAsync(Soon, token);
                Require(
                    lease.Retirement == RouteEntryLeaseRetirement.Dropped
                    && router.CurrentState.ActiveWindow?.Root is StackNode { Entries: [{ Id: "host-root" }] }
                    && navigation.ViewControllers is { Length: 1 }
                    && pushed.Handle == NativeHandle.Zero
                    && survived.Count == 0,
                    $"A pop UIKit committed{(crossing ? " across a refresh" : "")} was not recorded as the router's "
                    + $"state without the popped entry ({string.Join("; ", survived.Select(static f => f.Message))}).");
            }
        }
        finally
        {
            Task disposal = router.DisposeAsync().AsTask();
            await presenter.DisposeAsync();
            await disposal.WaitAsync(Soon, token);
        }

        Require(host.Failures.Count == 0 && survived.Count == 0, "The router host reported a failure.");
    }

    // A custom placeholder replaces a retired root the router still shows: here a navigation controller of its own,
    // which the presenter shows as the root's stack as it is.
    private static async Task CustomPlaceholderAsync(UIWindow window, CancellationToken token)
    {
        var host = new MainThreadLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        var placeholderStack = new UINavigationController(new UIViewController());
        var placements = new List<UIKitPagePlacement>();
        UIKitNavigationPresenter presenter = Presenter(
            window,
            (_, _) => ValueTask.FromResult(Page(new UIViewController())),
            options: new UIKitNavigationPresenterOptions
            {
                EntryLeases = leases,
                CreatePlaceholder = (_, placement) =>
                {
                    placements.Add(placement);
                    return placeholderStack;
                }
            });
        IRouterNavigator router = RouterNavigatorFactory.Create(
            new RouteTableBuilder().Build(),
            new OneRootPlanner(static _ => { }),
            presenter,
            new RouterNavigatorFactoryOptions { EntryLeases = leases });
        try
        {
            RouteEntryLease lease = leases.Open("custom-placeholder");
            try
            {
                _ = await router.NavigateAsync(
                    RouterNavigationRequest.FromRoute(new LeaseRoute("custom-placeholder"), NavigationRequestSource.Test),
                    lease,
                    token);
            }
            finally
            {
                lease.Close();
            }

            lease.Retire();
            await router.RefreshPresentationAsync(token);
            await lease.Released.WaitAsync(Soon, token);
            Require(
                placements is [UIKitPagePlacement.Root]
                && ReferenceEquals(window.RootViewController, placeholderStack),
                "The custom placeholder was not shown, as its own stack, for the retired root.");
        }
        finally
        {
            Task disposal = router.DisposeAsync().AsTask();
            await presenter.DisposeAsync();
            await disposal.WaitAsync(Soon, token);
        }

        Require(
            placeholderStack.Handle == NativeHandle.Zero && host.Failures.Count == 0,
            "The presenter did not dispose its custom placeholder, or a lease failure was reported.");
    }

    private sealed class EntryPages(Func<RouteEntry, UIKitPage> pages) : IUIKitPageFactory
    {
        public ValueTask<UIKitPage> CreatePageAsync(UIKitPageRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(pages(request.Entry));
    }

    // Records, when the lease releases, whether the controller that showed it had already left the window and been
    // disposed.
    private sealed class ReleaseProbe(UIWindow window)
    {
        private UIViewController? _shown;

        public bool Released { get; private set; }

        public bool ReleasedAfterNativeDisposal { get; private set; }

        public void Watch(UIViewController shown) => _shown = shown;

        public void Release()
        {
            Released = true;
            ReleasedAfterNativeDisposal = _shown is { } shown
                                          && !ReferenceEquals(window.RootViewController, shown)
                                          && shown.Handle == NativeHandle.Zero;
        }
    }

    private sealed record LeaseRoute(string Name) : AppRoute;

    // Each request pushes its route's entry over one root entry that stays.
    private sealed class PushedPlanner(string rootId) : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            var route = (LeaseRoute)context.Route;
            var state = new NavigationState(
                [
                    new WindowNode(
                        Window,
                        new StackNode(
                            Stack,
                            [
                                new RouteEntry(rootId, new LeaseRoute(rootId)),
                                new RouteEntry(route.Name, route)
                            ]))
                ],
                Window);
            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }

    // Each request shows one root whose entry ID is its route's name, so the next request drops the previous root.
    private sealed class OneRootPlanner(Action<string> onPlanning) : IAppNavigationPlanner
    {
        public ValueTask<NavigationPlan> CreatePlanAsync(
            NavigationPlanningContext context,
            CancellationToken cancellationToken = default)
        {
            var route = (LeaseRoute)context.Route;
            onPlanning(route.Name);
            var state = new NavigationState(
                [new WindowNode(Window, new StackNode(Stack, [new RouteEntry(route.Name, route)]))],
                Window);
            return ValueTask.FromResult(new NavigationPlan(state));
        }
    }

    // Runs lease work on the main thread, completing only when the work's own task has.
    private sealed class MainThreadLeaseHost : IRouteEntryLeaseHost
    {
        private readonly List<Exception> _failures = [];

        public IReadOnlyList<Exception> Failures
        {
            get
            {
                lock (_failures)
                    return [.. _failures];
            }
        }

        public Task InvokeAsync(Func<ValueTask> work)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            UIApplication.SharedApplication.BeginInvokeOnMainThread(() => _ = RunAsync(work, done));
            return done.Task;
        }

        public void ReportDefect(RouteEntryLeaseReleaseException defect)
        {
            lock (_failures)
                _failures.Add(defect);
        }

        public void ReportFatal(RouteEntryLeaseReleaseException failure) => ReportDefect(failure);

        private static async Task RunAsync(Func<ValueTask> work, TaskCompletionSource done)
        {
            try
            {
                await work();
                _ = done.TrySetResult();
            }
            catch (Exception failure)
            {
                _ = done.TrySetException(failure);
            }
        }
    }
}

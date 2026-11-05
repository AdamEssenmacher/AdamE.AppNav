using AdamE.AppNav.Leases;
using AdamE.AppNav.State;
using static AdamE.AppNav.iOS.NativeChecks.Checks;

namespace AdamE.AppNav.iOS.NativeChecks;

/// <summary>What the presenter does with the pages a factory returns: leases on failure, pages it must refuse, pages
/// that share a controller, push configuration and custom placeholders.</summary>
internal static class PageChecks
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    public static IEnumerable<(string Name, Func<UIWindow, CancellationToken, Task> Check)> All =>
    [
        ("page-leased-factory-failure", LeasedFactoryFailureAsync),
        ("page-refused", RefusedAsync),
        ("page-shared-controller", SharedControllerAsync),
        ("page-push-replacement", PushReplacementAsync)
    ];

    // A factory that fails cleanly gives its entry's lease back at once. One that reports a cleanup failure keeps it
    // attached, so the lease never releases what the unretired native work may still use.
    private static async Task LeasedFactoryFailureAsync(UIWindow window, CancellationToken token)
    {
        foreach (bool cleanupFailure in new[] { false, true })
        {
            var leases = new RouteEntryLeaseRegistry(new ImmediateLeaseHost());
            RouteEntryLease rootLease = leases.Open("failing-root");
            RouteEntryLease lease = leases.Open("failing-pushed");
            bool released = false;
            lease.Own(() => released = true);
            var root = new ProbeController();
            UIKitNavigationPresenter presenter = Presenter(
                window,
                (entry, _) => entry.Id == "failing-root"
                    ? ValueTask.FromResult(UIKitPage.InNavigationStack(root))
                    : cleanupFailure
                        ? throw new UIKitCleanupFailureException(
                            "Controlled unretired candidate.",
                            new InvalidOperationException("Controlled factory failure."),
                            new InvalidOperationException("Controlled cleanup failure."))
                        : throw new InvalidOperationException("Controlled factory failure."),
                options: new UIKitNavigationPresenterOptions { EntryLeases = leases });
            NavigationState rootState = Shows("failing-root");
            await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
            try
            {
                await ApplyAsync(presenter, Pushed("failing-root", "failing-pushed"), rootState, token);
                throw new InvalidOperationException("A failing factory's page was presented.");
            }
            catch (Exception failure) when (failure is InvalidOperationException or AggregateException) { }

            lease.Retire();
            lease.Close();
            if (cleanupFailure)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
                Require(
                    !released && !lease.Released.IsCompleted,
                    "A lease whose factory reported a cleanup failure released what the native work may still use.");
                window.RootViewController = null; // The fixture owns what the sticky presenter keeps.
            }
            else
            {
                await lease.Released.WaitAsync(Soon, token);
                Require(released, "A lease whose factory failed cleanly did not release.");
                await presenter.DisposeAsync();
                rootLease.Retire();
                rootLease.Close();
                await rootLease.Released.WaitAsync(Soon, token);
            }
        }
    }

    // Pages UIKit cannot present, and pages whose delegates the presenter needs, are retired and refused before native
    // changes.
    private static async Task RefusedAsync(UIWindow window, CancellationToken token)
    {
        var root = new ProbeController();
        var pushedContent = new ProbeController();
        var delegatedContent = new ProbeController();
        var delegatedModal = new ProbeController { ModalPresentationStyle = UIModalPresentationStyle.PageSheet };
        using var foreignDelegate = new ForeignPresentationDelegate();
        using var foreignNavigationDelegate = new ForeignNavigationDelegate();
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            (entry, _) => ValueTask.FromResult(
                entry.Id switch
                {
                    "refused-root" => UIKitPage.InNavigationStack(root),
                    "pushed-navigation" => Page(new UINavigationController(pushedContent)),
                    "delegated-root" => StackPage(
                        new UINavigationController(delegatedContent) { Delegate = foreignNavigationDelegate }),
                    _ => Page(WithPresentationDelegate(delegatedModal, foreignDelegate))
                }));
        NavigationState rootState = Shows("refused-root");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        (NavigationState Plan, string Message, ProbeController Retired)[] refused =
        [
            (Pushed("refused-root", "pushed-navigation"), "cannot push", pushedContent),
            (Shows("delegated-root"), "navigation controller delegate", delegatedContent),
            (Shows("refused-root", "delegated-modal"), "presentation controller delegate", delegatedModal)
        ];
        foreach ((NavigationState plan, string message, ProbeController retired) in refused)
        {
            try
            {
                await ApplyAsync(presenter, plan, rootState, token);
                throw new InvalidOperationException($"A page that should be refused ({message}) was presented.");
            }
            catch (InvalidOperationException failure) when (failure.Message.Contains(message, StringComparison.Ordinal))
            {
            }

            Require(
                retired.Retired && !root.Retired && ReferenceEquals(root.View?.Window, window),
                $"A refused page ({message}) was not retired, or the accepted root page changed.");
        }

        static UIViewController WithPresentationDelegate(UIViewController modal, ForeignPresentationDelegate foreign)
        {
            UIPresentationController presentation = modal.PresentationController
                                                    ?? throw new InvalidOperationException("No presentation controller.");
            presentation.Delegate = foreign;
            return modal;
        }
    }

    // A factory that hands a pushed entry the root page's own controller. When the root page is already shown, the plan
    // fails, that controller stays shown and undisposed, and the pushed entry's lease is still given back. When both
    // pages are new, the controller is disposed once, and both leases are given back only after that.
    private static async Task SharedControllerAsync(UIWindow window, CancellationToken token)
    {
        await SharedWithShownAsync(window, token);
        await SharedBetweenNewPagesAsync(window, token);
    }

    private static async Task SharedBetweenNewPagesAsync(UIWindow window, CancellationToken token)
    {
        var host = new ImmediateLeaseHost();
        var leases = new RouteEntryLeaseRegistry(host);
        RouteEntryLease rootLease = leases.Open("both-root");
        RouteEntryLease pushedLease = leases.Open("both-pushed");
        var shared = new ProbeController();
        var releasedAfterDisposal = new List<bool>();
        rootLease.Own(() => releasedAfterDisposal.Add(shared.Retired));
        pushedLease.Own(() => releasedAfterDisposal.Add(shared.Retired));
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            (entry, _) => ValueTask.FromResult(
                entry.Id == "both-root" ? UIKitPage.InNavigationStack(shared) : Page(shared)),
            options: new UIKitNavigationPresenterOptions { EntryLeases = leases });
        try
        {
            await ApplyAsync(presenter, Pushed("both-root", "both-pushed"), NavigationState.Empty, token);
            throw new InvalidOperationException("Two new pages sharing a controller were presented.");
        }
        catch (InvalidOperationException failure) when (failure.Message.Contains(
                                                            "different controllers",
                                                            StringComparison.Ordinal))
        { }

        foreach (RouteEntryLease lease in new[] { rootLease, pushedLease })
        {
            lease.Retire();
            lease.Close();
            await lease.Released.WaitAsync(Soon, token);
        }

        Require(
            shared.Retired && releasedAfterDisposal is [true, true] && host.Failures.Count == 0,
            "A controller two new pages shared was not disposed before both leases released.");
    }

    private static async Task SharedWithShownAsync(UIWindow window, CancellationToken token)
    {
        var leases = new RouteEntryLeaseRegistry(new ImmediateLeaseHost());
        RouteEntryLease rootLease = leases.Open("shared-root");
        RouteEntryLease lease = leases.Open("shared-pushed");
        bool released = false;
        lease.Own(() => released = true);
        var root = new ProbeController();
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            (entry, _) => ValueTask.FromResult(
                entry.Id == "shared-root" ? UIKitPage.InNavigationStack(root) : Page(root)),
            options: new UIKitNavigationPresenterOptions { EntryLeases = leases });
        NavigationState rootState = Shows("shared-root");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        try
        {
            await ApplyAsync(presenter, Pushed("shared-root", "shared-pushed"), rootState, token);
            throw new InvalidOperationException("A pushed page sharing the root page's controller was presented.");
        }
        catch (InvalidOperationException failure) when (failure.Message.Contains(
                                                            "different controllers",
                                                            StringComparison.Ordinal))
        { }

        lease.Retire();
        lease.Close();
        await lease.Released.WaitAsync(Soon, token);
        Require(
            released && !root.Retired && ReferenceEquals(root.View?.Window, window),
            "The shared controller was disposed, or the pushed entry's lease was kept.");
        await presenter.DisposeAsync();
        rootLease.Retire();
        rootLease.Close();
        await rootLease.Released.WaitAsync(Soon, token);
    }

    // Replacing the pushed page configures the new one too, without animation, so an earlier push's transition can be
    // cleared.
    private static async Task PushReplacementAsync(UIWindow window, CancellationToken token)
    {
        var root = new ProbeController();
        var first = new ProbeController();
        var second = new ProbeController();
        var pushes = new List<UIKitPushContext>();
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            (entry, _) => ValueTask.FromResult(
                entry.Id switch
                {
                    "replace-root" => UIKitPage.InNavigationStack(root, navigationBarHidden: true),
                    "replace-first" => Page(first),
                    _ => Page(second)
                }),
            options: new UIKitNavigationPresenterOptions { ConfiguringPush = pushes.Add });
        NavigationState firstState = Pushed("replace-root", "replace-first");
        await ApplyAsync(presenter, Shows("replace-root"), NavigationState.Empty, token);
        await ApplyAsync(presenter, firstState, Shows("replace-root"), token);
        await ApplyAsync(presenter, Pushed("replace-root", "replace-second"), firstState, token);
        var navigation = window.RootViewController as UINavigationController
                         ?? throw new InvalidOperationException("The root page has no navigation stack.");
        Require(
            pushes is [{ Animated: true, Entry.Id: "replace-first" }, { Animated: false, Entry.Id: "replace-second" }]
            && ReferenceEquals(pushes[1].Pushed, second)
            && navigation.ViewControllers is { Length: 2 } stack
            && ReferenceEquals(stack[1], second)
            && first.Retired
            && navigation.NavigationBarHidden,
            "Replacing the pushed page did not configure it unanimated, or lost the stack's hidden bar.");
    }

    private sealed class ForeignNavigationDelegate : UINavigationControllerDelegate;

    private sealed class ForeignPresentationDelegate : UIAdaptivePresentationControllerDelegate;

    // Runs lease work inline on the calling thread: these checks drive the registry directly, without a router.
    private sealed class ImmediateLeaseHost : IRouteEntryLeaseHost
    {
        private readonly List<Exception> _failures = [];

        public IReadOnlyList<Exception> Failures => _failures;

        public async Task InvokeAsync(Func<ValueTask> work) => await work();

        public void ReportDefect(RouteEntryLeaseReleaseException defect) => _failures.Add(defect);

        public void ReportFatal(RouteEntryLeaseReleaseException failure) => _failures.Add(failure);
    }
}

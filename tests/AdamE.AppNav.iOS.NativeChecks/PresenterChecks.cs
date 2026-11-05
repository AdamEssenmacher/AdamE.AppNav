using AdamE.AppNav.Plans;
using AdamE.AppNav.State;
using ObjCRuntime;
using static AdamE.AppNav.iOS.NativeChecks.Checks;

namespace AdamE.AppNav.iOS.NativeChecks;

/// <summary>The presenter against real UIKit in the app's own window, without a router: each check applies plans
/// directly, as the router would.</summary>
internal static class PresenterChecks
{
    public static IEnumerable<(string Name, Func<UIWindow, CancellationToken, Task> Check)> All =>
    [
        ("empty-startup-refresh", EmptyStartupRefreshAsync),
        ("modal-host-events", ModalHostEventsAsync),
        ("failed-pop-record-refresh", FailedPopRecordRefreshAsync),
        ("modal-loan-retirement", ModalLoanRetirementAsync),
        ("healthy-presentation", HealthyPresentationAsync),
        ("presentation-retirement-failure", PresentationRetirementFailureAsync),
        ("factory-cleanup-failure", FactoryCleanupFailureAsync),
        ("stack-preparation", StackPreparationAsync),
        ("native-pop-is-a-fact", NativePopIsAFactAsync),
        ("pushed-page-confirmation", PushedPageConfirmationAsync),
        ("pushed-retirement-failure", PushedRetirementFailureAsync),
        ("completion-retirement", CompletionRetirementAsync),
        ("foreign-hierarchy-retirement", ForeignHierarchyRetirementAsync)
    ];

    private static async Task EmptyStartupRefreshAsync(UIWindow window, CancellationToken token)
    {
        using var hidden = new UIWindow(window.WindowScene!);
        await using var presenter = Presenter(hidden, _ => throw new InvalidOperationException("Empty state built a page."));
        await ApplyAsync(presenter, NavigationState.Empty, NavigationState.Empty, token, NavigationPlanKind.Refresh)
            .WaitAsync(TimeSpan.FromSeconds(5), token);
        Require(hidden.Hidden && hidden.RootViewController is null, "Empty refresh revealed or populated the window.");
    }

    private static async Task ModalHostEventsAsync(UIWindow window, CancellationToken token)
    {
        var root = new UIViewController();
        var modal = new UIViewController { ModalPresentationStyle = UIModalPresentationStyle.FullScreen };
        using var foreign = UIAlertController.Create("Sign in", null, UIAlertControllerStyle.Alert);
        foreign.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        var backStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int backCount = 0;
        await using var presenter = Presenter(
            window,
            (entry, _) => ValueTask.FromResult(Page(entry.Id == "modal-event-root" ? root : modal)),
            back: async _ =>
            {
                backCount++;
                backStarted.TrySetResult();
                await allowBack.Task.WaitAsync(token);
            },
            refresh: _ =>
            {
                refreshed.TrySetResult();
                return Task.CompletedTask;
            });
        NavigationState rootState = Shows("modal-event-root");
        NavigationState modalState = Shows("modal-event-root", "event-modal");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        await ApplyAsync(presenter, modalState, rootState, token);
        UIPresentationController presentation = modal.PresentationController!;
        presentation.Delegate.DidAttemptToDismiss(presentation);
        await backStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        presentation.Delegate.DidAttemptToDismiss(presentation);

        await PresentForeignAsync(modal, foreign, token);
        await ApplyAsync(presenter, modalState, modalState, token, NavigationPlanKind.Refresh);
        Require(ReferenceEquals(modal.PresentedViewController, foreign), "Refresh removed the modal's alert.");
        try
        {
            await ApplyAsync(presenter, rootState, modalState, token);
            throw new InvalidOperationException("Navigation dismissed a still-current modal's foreign alert.");
        }
        catch (InvalidOperationException failure) when (failure.Message.Contains("owned by its SDK", StringComparison.Ordinal))
        {
        }

        Require(ReferenceEquals(modal.PresentedViewController, foreign), "The rejected navigation changed native UI.");
        allowBack.TrySetResult();
        var dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        root.DismissViewController(false, () => dismissed.TrySetResult());
        await dismissed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        Require(backCount == 1, "Two attempts on the same modal queued two Back requests.");
    }

    private static async Task FailedPopRecordRefreshAsync(UIWindow window, CancellationToken token)
    {
        var root = new UIViewController();
        var pushed = new UIViewController();
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Controlled failed pop record.");
        await using var presenter = Presenter(
            window,
            (entry, _) => ValueTask.FromResult(entry.Id == "failed-pop-root"
                ? UIKitPage.InNavigationStack(root)
                : Page(pushed)),
            popped: (_, _) => Task.FromException(failure),
            report: error => reported.TrySetResult(error));
        NavigationState rootState = Shows("failed-pop-root");
        NavigationState pushedState = Pushed("failed-pop-root", "failed-pop-page");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        await ApplyAsync(presenter, pushedState, rootState, token);
        var stack = (UINavigationController)window.RootViewController!;
        _ = stack.PopViewController(true);
        Require(ReferenceEquals(await reported.Task.WaitAsync(TimeSpan.FromSeconds(5), token), failure),
            "The failed pop record was not reported.");
        await ApplyAsync(presenter, pushedState, pushedState, token, NavigationPlanKind.Refresh);
        Require(ReferenceEquals(stack.TopViewController, pushed), "Refresh did not restore the router's accepted stack.");
    }

    private static async Task ModalLoanRetirementAsync(UIWindow window, CancellationToken token)
    {
        foreach (int failureMode in new[] { 0, 1, 2 })
        {
            var cleanup = new InvalidOperationException("Controlled native page loan retirement failure.");
            int loans = 0;
            var survived = new List<Exception>();
            var root = new ProbeController();
            var modal = new ProbeController();
            if (failureMode == 1)
                modal.Retiring = () => throw cleanup;
            var loan = new ActionDisposable(() =>
            {
                Require(
                    // The fixture owns these controllers through awaited native retirement and callback cleanup.
                    // ReSharper disable once AccessToDisposedClosure
                    modal.DetachedAtRetirement
                    // UIKit may retain a custom controller's native peer after successful base disposal.
                    // ReSharper disable once AccessToDisposedClosure
                    && modal.DisposeReturned,
                    "A page loan released before actual controller disposal.");
                loans++;
                if (failureMode == 2)
                    throw cleanup;
            });
            UIKitNavigationPresenter presenter = Presenter(
                window,
                entry => entry.Id == "loan-root"
                    // ReSharper disable once AccessToDisposedClosure
                    ? Page(root)
                    // ReSharper disable once AccessToDisposedClosure
                    : Page(modal, loan: loan),
                report: survived.Add);
            NavigationState rootState = Shows("loan-root"),
                shown = Shows("loan-root", "loan-modal");
            try
            {
                await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
                await ApplyAsync(presenter, shown, rootState, token);
                Require(
                    ReferenceEquals(root.PresentedViewController, modal) && loans == 0,
                    "A mounted controller released its native loan.");
                try
                {
                    await ApplyAsync(presenter, rootState, shown, token);
                    Require(failureMode == 0, "Failed page retirement returned success.");
                }
                catch (AggregateException failure) when (failureMode != 0 && Contains(failure, cleanup)) { }

                Require(loans == (failureMode == 1 ? 0 : 1), "Failed physical retirement released a page loan.");
                for (int repeat = 0; repeat < 2; repeat++)
                    try
                    {
                        await presenter.DisposeAsync();
                        Require(failureMode == 0, "Unsafe page retirement became successful disposal.");
                    }
                    catch (AggregateException failure) when (failureMode != 0 && Contains(failure, cleanup)) { }

                Require(
                    loans == (failureMode == 1 ? 0 : 1) && survived.Count == 0,
                    "Page loan release was repeated or unsafe retirement was reported survived.");
            }
            finally
            {
                try { await presenter.DisposeAsync(); }
                catch (AggregateException failure) when (failureMode != 0 && Contains(failure, cleanup)) { }

                modal.Retiring = null;
                modal.Dispose();
                root.Dispose();
                window.RootViewController = null;
            }
        }
    }

    private static async Task HealthyPresentationAsync(UIWindow window, CancellationToken token)
    {
        int back = 0;
        var backRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<Exception>();
        var root = new ProbeController();
        using var retired = new CancellationTokenSource();
        CancellationToken retirement = retired.Token;
        // This callback is exercised and its presenter retired before the enclosing CTS is disposed.
        // ReSharper disable once AccessToDisposedClosure
        var target = new ProbeController(retired.Cancel);
        int targetResolutions = 0;
        var sheet = new ProbeController();
        using var disposalBoundary = new CancellationTokenSource();
        CancellationToken boundaryToken = disposalBoundary.Token;
        var boundaryTarget = new ProbeController();
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            entry => entry.Id switch
            {
                "root" => Page(root),
                "retiring" => ResolveRetiring(),
                "sheet" => Page(sheet),
                "dispose-boundary" => Page(boundaryTarget, retirement: boundaryToken),
                "factory-failure" => throw new InvalidOperationException("Controlled native factory failure."),
                _ => throw new InvalidOperationException("Unknown test route.")
            },
            _ =>
            {
                back++;
                Require(NSThread.IsMain, "The host's Back request did not run on the main thread.");
                backRequested.TrySetResult();
                return Task.CompletedTask;
            },
            report: failures.Add);

        UIKitPage ResolveRetiring()
        {
            targetResolutions++;
            return Page(target, retirement: retirement);
        }

        NavigationState rootState = Shows("root");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        Require(ReferenceEquals(window.RootViewController, root), "The root page was not the actual native root.");
        try
        {
            await ApplyAsync(presenter, Shows("retiring"), rootState, token);
            throw new InvalidOperationException("A page retired during native appearance committed.");
        }
        catch (OperationCanceledException) when (retired.IsCancellationRequested) { }

        Require(targetResolutions == 1, "Native completion resolved a newer page instead of retaining the original.");
        Require(ReferenceEquals(window.RootViewController, root), "Cancellation did not restore the prior native root.");
        Require(target.Retired, "An uncommitted native controller was not retired.");
        try
        {
            await ApplyAsync(presenter, Shows("factory-failure"), rootState, token);
            throw new InvalidOperationException("A factory failure was accepted.");
        }
        catch (InvalidOperationException failure) when (failure.Message == "Controlled native factory failure.") { }

        Require(ReferenceEquals(window.RootViewController, root), "A factory failure changed the native root.");
        // A plan of a shape the topology does not declare is refused before any page is built.
        try
        {
            await ApplyAsync(presenter, Pushed("root", "factory-failure"), rootState, token);
            throw new InvalidOperationException("A push over a root page without a navigation stack was accepted.");
        }
        catch (InvalidOperationException failure) when (failure.Message.Contains(
                                                            "navigation stack",
                                                            StringComparison.Ordinal))
        { }

        try
        {
            await ApplyAsync(presenter, UndeclaredModal("root", "factory-failure"), rootState, token);
            throw new InvalidOperationException("An undeclared modal was presented.");
        }
        catch (NotSupportedException) { }

        Require(ReferenceEquals(window.RootViewController, root), "A refused plan changed the native root.");
        NavigationState sheetShown = Shows("root", "sheet");
        await ApplyAsync(presenter, sheetShown, rootState, token);
        Require(ReferenceEquals(root.PresentedViewController, sheet), "The sheet was not the actual native presentation.");
        Require(sheet.ModalInPresentation, "Unmediated interactive dismissal bypasses AppNav Back policy.");
        UIPresentationController presentation = sheet.PresentationController
                                                ?? throw new InvalidOperationException("Missing native presentation.");
        // Exercise the installed UIKit delegate body. This is not a physical drag/gesture claim.
        presentation.Delegate.DidAttemptToDismiss(presentation);
        // The request reaches the host in a later turn of the main run loop, outside any router operation.
        await backRequested.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        Require(back == 1, "A native dismissal intent did not reach the host's Back request once.");
        Require(
            ReferenceEquals(root.PresentedViewController, sheet),
            "A Back intent changed native state without an accepted plan.");
        await ApplyAsync(presenter, rootState, sheetShown, token);
        Require(root.PresentedViewController is null, "An accepted Back did not remove the actual sheet controller.");
        Require(sheet.Retired, "The retired modal's native binding was retained.");
        // Native disposal is awaited inside this CTS lifetime.
        // ReSharper disable once AccessToDisposedClosure
        root.Retiring = disposalBoundary.Cancel;
        try
        {
            await ApplyAsync(presenter, Shows("dispose-boundary"), rootState, token);
            throw new InvalidOperationException("Cancellation at irreversible old disposal completed successfully.");
        }
        catch (OperationCanceledException) when (disposalBoundary.IsCancellationRequested) { }

        Require(
            root.Retired && boundaryTarget.Retired && window.RootViewController is null,
            "Disposal-boundary cancellation resurrected disposed native UI instead of clearing safely.");
        Require(failures.Count == 0, "A native consistency fault was reported in a controlled success case.");
    }

    private static NavigationState UndeclaredModal(string root, string modal) => new(
        [
            new WindowNode(
                Window,
                new StackNode(Stack, [new RouteEntry(root, new CheckRoute())]),
                [new ModalNode("undeclared", new RouteEntry(modal, new CheckRoute()))])
        ],
        Window);

    private static async Task PresentationRetirementFailureAsync(UIWindow window, CancellationToken token)
    {
        foreach (bool partialMaterialization in new[] { false, true })
        {
            var original = new InvalidOperationException("Controlled native materialization failure.");
            var cleanup = new InvalidOperationException("Controlled native controller retirement failure.");
            var survived = new List<Exception>();
            int releases = 0;

            void Retire()
            {
                releases++;
                throw cleanup;
            }

            var root = new ProbeController();
            var sheet = new ProbeController { Retiring = Retire };
            var replacement = new ProbeController { Retiring = Retire };
            UIKitNavigationPresenter owner = Presenter(
                window,
                entry => entry.Id switch
                {
                    "root" => Page(root),
                    "replacement" => Page(replacement),
                    _ => partialMaterialization ? throw original : Page(sheet)
                },
                report: survived.Add);
            try
            {
                NavigationState rootState = Shows("root");
                await ApplyAsync(owner, rootState, NavigationState.Empty, token);
                NavigationState sheetShown = Shows("root", "sheet");
                if (!partialMaterialization)
                    await ApplyAsync(owner, sheetShown, rootState, token);
                AggregateException failure;
                try
                {
                    await ApplyAsync(
                        owner,
                        partialMaterialization ? Shows("replacement", "sheet") : rootState,
                        partialMaterialization ? rootState : sheetShown,
                        token);
                    throw new InvalidOperationException("Failed presentation cleanup returned success.");
                }
                catch (AggregateException actual)
                {
                    failure = actual;
                }

                Require(
                    failure.Flatten().InnerExceptions.Any(value => ReferenceEquals(value, cleanup)),
                    "Presentation cleanup lost its original native retirement failure.");
                Require(
                    !partialMaterialization
                    || failure.Flatten().InnerExceptions.Any(value => ReferenceEquals(value, original)),
                    "Partial materialization cleanup replaced its original presentation failure.");
                try
                {
                    await ApplyAsync(owner, rootState, rootState, token);
                    throw new InvalidOperationException("Unsafe cleanup allowed a successful recovery.");
                }
                catch (AggregateException actual)
                {
                    Require(ReferenceEquals(failure, actual), "Recovery replaced the unsafe native outcome.");
                }

                for (int repeat = 0; repeat < 2; repeat++)
                    try
                    {
                        await owner.DisposeAsync();
                        throw new InvalidOperationException("Unsafe cleanup allowed successful disposal.");
                    }
                    catch (AggregateException actual)
                    {
                        Require(ReferenceEquals(failure, actual), "Disposal lost the unsafe native outcome.");
                    }

                Require(releases == 1 && survived.Count == 0, "Unsafe retirement was retried or reported survived.");
            }
            finally
            {
                // This dedicated fixture owns its test controllers. Reset the window only after the assertions; this is
                // never evidence that failed presenter disposal succeeded.
                window.RootViewController = null;
            }
        }
    }

    // A factory that cannot retire what it started building says so with UIKitCleanupFailureException (a host wraps its
    // own signal in one). The presenter then keeps the entry's loan and fails every later operation and its disposal
    // with that failure. A stale page the factory hands back after its page retired is still the presenter's to retire,
    // and a failed disposal of it is sticky the same way.
    private static async Task FactoryCleanupFailureAsync(UIWindow window, CancellationToken token)
    {
        foreach (bool staleCandidate in new[] { false, true })
        {
            var cleanup = new InvalidOperationException("Controlled unreturned native cleanup failure.");
            var root = new ProbeController();
            var navigation = new UINavigationController(root);
            var candidate = new ProbeController { Retiring = () => throw cleanup };
            using var lifetime = new CancellationTokenSource();
            UIKitNavigationPresenter presenter = Presenter(
                window,
                async (entry, operation) =>
                {
                    if (entry.Id == "cleanup-root")
                        // ReSharper disable once AccessToDisposedClosure
                        return StackPage(navigation);
                    await Task.Yield();
                    if (!staleCandidate)
                        throw new UIKitCleanupFailureException(
                            "The factory could not retire its unreturned page.",
                            new InvalidOperationException("Controlled factory failure."),
                            cleanup);
                    // ReSharper disable once AccessToDisposedClosure
                    await lifetime.CancelAsync();
                    // ReSharper disable once AccessToDisposedClosure
                    return Page(candidate, retirement: lifetime.Token);
                });
            NavigationState rootState = Shows("cleanup-root");
            await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
            try
            {
                await ApplyAsync(presenter, Pushed("cleanup-root", "cleanup-pushed"), rootState, token);
                throw new InvalidOperationException("A factory cleanup failure was accepted.");
            }
            catch (AggregateException failure) when (Contains(failure, cleanup)) { }

            Require(!staleCandidate || candidate.Retired, "A stale candidate the factory returned was not retired.");
            try
            {
                await ApplyAsync(presenter, rootState, rootState, token, NavigationPlanKind.Refresh);
                throw new InvalidOperationException("A cleanup failure lost its sticky authority over later operations.");
            }
            catch (AggregateException failure) when (Contains(failure, cleanup)) { }

            try
            {
                await presenter.DisposeAsync();
                throw new InvalidOperationException("A cleanup failure lost its sticky authority over disposal.");
            }
            catch (AggregateException failure) when (Contains(failure, cleanup)) { }

            // This fixture owns the residual objects after the expected fatal-boundary case.
            window.RootViewController = null;
            navigation.SetViewControllers([], false);
            root.Dispose();
            navigation.Dispose();
            candidate.Retiring = null;
            candidate.Dispose();
        }
    }

    // A pushed page is pushed onto the root page's own navigation controller only once it is built, under the root
    // page's bar with UIKit's own back button and back-swipes untouched. A refresh keeps foreign UI over the pushed
    // page, which also blocks popping it while it is current. A candidate whose page retired before its factory
    // returned is retired, never mounted.
    private static async Task StackPreparationAsync(UIWindow window, CancellationToken token)
    {
        var root = new ProbeController();
        var navigation = new UINavigationController(root);
        var pushed = new ProbeController();
        var unused = new ProbeController();
        var sdkChild = new ProbeController();
        using var pushedLifetime = new CancellationTokenSource();
        using var unusedLifetime = new CancellationTokenSource();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unusedRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unusedPrepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int back = 0;
        int originalRetirements = 0;
        var pushes = new List<UIKitPushContext>();
        var failures = new List<Exception>();
        root.Retiring = () => originalRetirements++;
        // UIKit's own pop gesture delegates, read before the presenter sees this stack; it must never replace them.
        UIGestureRecognizer[] pops = PopGestures(navigation);
        NativeHandle[] uikitDelegates =
            [.. pops.Select(static gesture => gesture.WeakDelegate?.Handle ?? NativeHandle.Zero)];
        Require(
            uikitDelegates.All(static handle => handle != NativeHandle.Zero),
            "A new navigation controller has no UIKit pop gesture delegate.");
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            CreatePageAsync,
            _ =>
            {
                back++;
                return Task.CompletedTask;
            },
            report: failures.Add,
            options: new UIKitNavigationPresenterOptions { ConfiguringPush = pushes.Add });

        async ValueTask<UIKitPage> CreatePageAsync(RouteEntry entry, CancellationToken operation)
        {
            if (entry.Id == "stack-root")
                return StackPage(navigation);
            if (entry.Id == "prepared")
            {
                requested.TrySetResult();
                await prepared.Task.WaitAsync(operation);
                // ReSharper disable once AccessToDisposedClosure
                return Page(pushed, retirement: pushedLifetime.Token);
            }

            if (entry.Id == "unused")
            {
                unusedRequested.TrySetResult();
                // The controlled factory deliberately delivers its original candidate after retirement. The presenter
                // must reject and dispose that exact candidate rather than adopt a newer page.
                // ReSharper disable once PossiblyMistakenUseOfCancellationToken
                await unusedPrepared.Task.WaitAsync(token);
                // ReSharper disable once AccessToDisposedClosure
                return Page(unused, retirement: unusedLifetime.Token);
            }

            throw new InvalidOperationException("Unknown stack route.");
        }

        NavigationState rootState = Shows("stack-root");
        NavigationState pushedState = Pushed("stack-root", "prepared");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        Task mounting = ApplyAsync(presenter, pushedState, rootState, token);
        await requested.Task.WaitAsync(token);
        Require(
            ReferenceEquals(window.RootViewController, navigation)
            && navigation.ViewControllers is { Length: 1 } before
            && ReferenceEquals(before[0], root)
            && pushed.View?.Window is null,
            "Preparing a pushed page replaced the accepted root before the candidate was ready.");
        prepared.TrySetResult();
        await mounting.WaitAsync(token);
        Require(
            navigation.ViewControllers is { Length: 2 } shown
            && ReferenceEquals(shown[0], root)
            && ReferenceEquals(shown[1], pushed)
            && ReferenceEquals(pushed.View?.Window, window),
            "The pushed page was not mounted above the exact original root page.");
        Require(
            pushes is [{ Animated: true, Entry.Id: "prepared" } push]
            && ReferenceEquals(push.RootContent, root)
            && ReferenceEquals(push.Pushed, pushed),
            "The push hook was not called once, animated, for the pushed page over the root page.");
        Require(
            !navigation.NavigationBarHidden
            // UIKit owns the back button and both back-swipes: its delegates, on and untouched.
            && PopGestures(navigation).SequenceEqual(pops)
            && pops.All(static gesture => gesture.Enabled)
            && pops.Select(static gesture => gesture.WeakDelegate?.Handle ?? NativeHandle.Zero)
                .SequenceEqual(uikitDelegates)
            && navigation.Delegate is not null
            && pushed.NavigationItem.BackAction is null
            && back == 0
            && navigation.ViewControllers is { Length: 2 },
            "The pushed page is not under the root page's bar with UIKit's own back button and back-swipes.");
        await PresentForeignAsync(pushed, sdkChild, token);
        await ApplyAsync(presenter, pushedState, pushedState, token, NavigationPlanKind.Refresh);
        Require(
            !sdkChild.Retired && ReferenceEquals(sdkChild.View?.Window, window),
            "An accepted refresh removed or adopted the pushed page's own SDK child.");
        try
        {
            await ApplyAsync(presenter, rootState, pushedState, token);
            throw new InvalidOperationException("A current pushed page's SDK hierarchy was popped.");
        }
        catch (InvalidOperationException failure) when (failure.Message.Contains(
                                                            "still-current",
                                                            StringComparison.Ordinal))
        { }

        await pushedLifetime.CancelAsync();
        await ApplyAsync(presenter, rootState, pushedState, token);
        Require(
            navigation.ViewControllers is { Length: 1 } returned
            && ReferenceEquals(returned[0], root)
            && pushed is { Retired: true, DetachedAtRetirement: true }
            && sdkChild.View?.Window is null
            && !sdkChild.Retired
            && originalRetirements == 0
            && !navigation.NavigationBarHidden,
            "Retiring the pushed page did not detach its exact native hierarchy while keeping the root page.");
        sdkChild.Dispose(); // The fixture remains the SDK object's owner.

        Task abandoned = ApplyAsync(presenter, Pushed("stack-root", "unused"), rootState, token);
        await unusedRequested.Task.WaitAsync(token);
        await unusedLifetime.CancelAsync();
        Require(!unused.Retired, "The controlled candidate was retired before its factory handed it off.");
        unusedPrepared.TrySetResult();
        try
        {
            await abandoned.WaitAsync(token);
            throw new InvalidOperationException("A prepared candidate whose page retired was mounted.");
        }
        catch (OperationCanceledException) when (unusedLifetime.IsCancellationRequested) { }

        Require(
            unused.Retired && navigation.ViewControllers?.Length == 1 && !root.Retired,
            "Unused candidate cleanup disposed the original root page or leaked its native artifact.");
        Require(failures.Count == 0, "Controlled stack consistency reported a survived defect.");
    }

    private enum PopCrossing
    {
        None,
        // A navigation that changes the stack (here, a replacement pushed page): it fails.
        Navigation,
        // A navigation that presents the same pages again (as a recovery re-presents its pushed page): it stands aside.
        SamePages,
        Refresh
    }

    // A pop UIKit commits is a fact: the presenter reports it once, never asks Back or refreshes, and the record (a
    // Reconcile plan, as the host submits it) retires the already detached pushed page while keeping the root page. The
    // push and pop are real animated UIKit transitions. A pop that crosses an operation wins: a navigation that would
    // change the stack fails without touching native, and one that presents the same pages again (a refresh, or a
    // recovery) stands aside; none pushes the popped page back.
    private static async Task NativePopIsAFactAsync(UIWindow window, CancellationToken token)
    {
        foreach (PopCrossing crossing in Enum.GetValues<PopCrossing>())
            await NativePopIsAFactAsync(window, crossing, token);
    }

    private static async Task NativePopIsAFactAsync(UIWindow window, PopCrossing crossing, CancellationToken token)
    {
        var root = new ProbeController();
        var pushed = new ProbeController();
        var replacement = new ProbeController();
        using var pushedLifetime = new CancellationTokenSource();
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new List<Exception>();
        var popped = new List<string>();
        int back = 0;
        int refreshes = 0;
        NavigationState rootState = Shows("pop-root");
        NavigationState pushedState = Pushed("pop-root", "pop-pushed");
        UIKitNavigationPresenter? presenter = null;
        await using UIKitNavigationPresenter owned = presenter = Presenter(
            window,
            (entry, _) => ValueTask.FromResult(
                entry.Id switch
                {
                    // The presenter's own stack around this root page, through the shortcut.
                    "pop-root" => UIKitPage.InNavigationStack(root),
                    // ReSharper disable once AccessToDisposedClosure
                    "pop-pushed" => Page(pushed, retirement: pushedLifetime.Token),
                    "pop-replacement" => Page(replacement),
                    _ => throw new InvalidOperationException("Unknown native pop route.")
                }),
            _ =>
            {
                back++;
                return Task.CompletedTask;
            },
            async (entry, _) =>
            {
                popped.Add(entry);
                // ReSharper disable once AccessToDisposedClosure
                await pushedLifetime.CancelAsync();
                await ApplyAsync(
                    presenter ?? throw new InvalidOperationException("No presenter."),
                    rootState,
                    pushedState,
                    token,
                    NavigationPlanKind.Reconcile);
                recorded.TrySetResult();
            },
            _ =>
            {
                refreshes++;
                return Task.CompletedTask;
            },
            failures.Add);
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        var navigation = window.RootViewController as UINavigationController
                         ?? throw new InvalidOperationException("The root page has no navigation stack.");
        await ApplyAsync(presenter, pushedState, rootState, token);
        Require(
            navigation.ViewControllers is { Length: 2 } && ReferenceEquals(pushed.View?.Window, window),
            "The page was not pushed onto the original root page's stack.");

        _ = navigation.PopViewController(true);
        Require(navigation.GetTransitionCoordinator() is not null, "An animated pop had no transition in flight.");
        switch (crossing)
        {
            case PopCrossing.Navigation:
                try
                {
                    await ApplyAsync(presenter, Pushed("pop-root", "pop-replacement"), pushedState, token);
                    throw new InvalidOperationException("A navigation that crossed a committed pop changed the stack.");
                }
                catch (UIKitNativePopWonException) { }

                Require(
                    replacement.Retired && replacement.View?.Window is null,
                    "A navigation that lost to a committed pop kept its unused replacement.");
                break;
            case PopCrossing.SamePages or PopCrossing.Refresh:
                try
                {
                    await ApplyAsync(
                        presenter,
                        pushedState,
                        pushedState,
                        token,
                        crossing == PopCrossing.Refresh ? NavigationPlanKind.Refresh : NavigationPlanKind.Navigate);
                }
                // The pop is reported as the operation ends, and recording it retires the pushed page.
                catch (OperationCanceledException) when (pushedLifetime.IsCancellationRequested) { }

                break;
            case PopCrossing.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(crossing), crossing, null);
        }

        await recorded.Task.WaitAsync(token);
        Require(
            popped is ["pop-pushed"]
            && back == 0
            && refreshes == 0
            && failures.Count == 0
            && navigation.ViewControllers is { Length: 1 } shown
            && ReferenceEquals(shown[0], root)
            && pushed is { Retired: true, DetachedAtRetirement: true }
            && !root.Retired,
            $"A committed pop ({crossing}) was not reported once and recorded without Back, refresh or a push back.");
    }

    // A pushed page shows its own confirmation (an alert, or a medium-detent sheet), and the root changes while that
    // confirmation is on screen, while it is still animating away after a tap, or while it is still animating in. Each
    // animating moment is checked to be in flight when the change is applied. The presenter dismisses it or waits for
    // it, retires the pushed page and the root page detached, and mounts the new root, with no defect. While the
    // confirmation is up, an accepted-state refresh keeps it and the pushed page.
    private static async Task PushedPageConfirmationAsync(UIWindow window, CancellationToken token)
    {
        foreach (bool sheet in new[] { false, true })
            foreach (ConfirmationMoment moment in Enum.GetValues<ConfirmationMoment>())
                await PushedPageConfirmationAsync(window, token, sheet, moment);
    }

    private static async Task PushedPageConfirmationAsync(
        UIWindow window,
        CancellationToken token,
        bool sheet,
        ConfirmationMoment moment)
    {
        var root = new ProbeController();
        var navigation = new UINavigationController(root);
        var pushed = new ProbeController();
        var next = new ProbeController();
        using var account = new CancellationTokenSource();
        var failures = new List<Exception>();
        CancellationToken retirement = account.Token;
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            entry => entry.Id switch
            {
                "confirm-root" => StackPage(navigation, retirement: retirement),
                "confirm-pushed" => Page(pushed, retirement: retirement),
                "confirm-next" => Page(next),
                _ => throw new InvalidOperationException("Unknown confirmation route.")
            },
            report: failures.Add);
        NavigationState rootState = Shows("confirm-root");
        NavigationState pushedState = Pushed("confirm-root", "confirm-pushed");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        await ApplyAsync(presenter, pushedState, rootState, token);
        Require(
            navigation.ViewControllers is { Length: 2 } stack && ReferenceEquals(stack[1], pushed),
            "The page was not pushed onto the original root page's stack.");

        UIViewController confirmation = sheet ? MediumSheet() : Alert();
        if (moment != ConfirmationMoment.Presenting)
        {
            await PresentForeignAsync(pushed, confirmation, token);
            Require(
                ReferenceEquals(confirmation.View?.Window, window),
                "The pushed page's own confirmation did not mount.");
            await ApplyAsync(presenter, pushedState, pushedState, token, NavigationPlanKind.Refresh);
            Require(
                ReferenceEquals(confirmation.View?.Window, window) && !pushed.Retired && !root.Retired,
                "An accepted-state refresh took down the pushed page's own confirmation.");
        }

        // The pages retire first, so nothing is awaited between starting an animation and applying the change.
        await account.CancelAsync();
        if (moment == ConfirmationMoment.Presenting)
        {
            // Animated, from the pushed child. Not awaited.
            pushed.PresentViewController(confirmation, true, null);
            Require(confirmation.IsBeingPresented, "The confirmation was not animating in when the pages retired.");
        }
        else if (moment == ConfirmationMoment.Dismissing)
        {
            confirmation.DismissViewController(true, null);
            Require(confirmation.IsBeingDismissed, "The confirmation was not animating away when the pages retired.");
        }

        await ApplyAsync(presenter, Shows("confirm-next"), pushedState, token);
        Require(
            ReferenceEquals(window.RootViewController, next)
            && confirmation.View?.Window is null
            && pushed is { Retired: true, DetachedAtRetirement: true }
            && root is { Retired: true, DetachedAtRetirement: true }
            && failures.Count == 0,
            "A root change under a pushed page's confirmation did not dismiss it, retire the stack detached and mount "
            + "the new root.");
        confirmation.Dispose(); // The page owns its confirmation; the presenter never disposes it.

        static UIViewController Alert()
        {
            var alert = UIAlertController.Create("Leave?", null, UIAlertControllerStyle.Alert);
            alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
            return alert;
        }

        static UIViewController MediumSheet()
        {
            var medium = new ProbeController { ModalPresentationStyle = UIModalPresentationStyle.PageSheet };
            medium.SheetPresentationController?.Detents = [UISheetPresentationControllerDetent.CreateMediumDetent()];
            return medium;
        }
    }

    private enum ConfirmationMoment { Shown, Dismissing, Presenting }

    private static async Task PushedRetirementFailureAsync(UIWindow window, CancellationToken token)
    {
        var original = new InvalidOperationException("Controlled pushed native retirement failure.");
        var root = new ProbeController();
        var navigation = new UINavigationController(root);
        var pushed = new ProbeController();
        pushed.Retiring = () =>
        {
            Require(
                pushed.ParentViewController is null && pushed.View?.Window is null,
                "The pushed page's disposal began before it detached.");
            throw original;
        };
        using var lifetime = new CancellationTokenSource();
        UIKitNavigationPresenter presenter = Presenter(
            window,
            entry => entry.Id == "failure-root"
                // ReSharper disable once AccessToDisposedClosure
                ? StackPage(navigation)
                // ReSharper disable once AccessToDisposedClosure
                : Page(pushed, retirement: lifetime.Token));
        NavigationState rootState = Shows("failure-root");
        NavigationState pushedState = Pushed("failure-root", "failure-pushed");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        await ApplyAsync(presenter, pushedState, rootState, token);
        await lifetime.CancelAsync();
        try
        {
            await ApplyAsync(presenter, rootState, pushedState, token);
            throw new InvalidOperationException("A failed native retirement of the pushed page was acknowledged.");
        }
        catch (AggregateException failure) when (Contains(failure, original)) { }

        try
        {
            await presenter.DisposeAsync();
            throw new InvalidOperationException("Later disposal erased the sticky native retirement failure.");
        }
        catch (AggregateException failure) when (Contains(failure, original)) { }

        // Only this fixture owns the residual native objects after the expected fatal-boundary failure.
        window.RootViewController = null;
        navigation.SetViewControllers([], false);
        root.Dispose();
        navigation.Dispose();
    }

    private static async Task CompletionRetirementAsync(UIWindow window, CancellationToken token)
    {
        using var sheetRetirement = new CancellationTokenSource();
        var root = new ProbeController { HoldPresentationCompletion = true };
        CancellationToken sheetToken = sheetRetirement.Token;
        var sheet = new ProbeController();
        var terminalSheet = new ProbeController();
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            entry => entry.Id switch
            {
                "root" => Page(root),
                "sheet" => Page(sheet, retirement: sheetToken),
                "terminal" => Page(terminalSheet),
                _ => throw new InvalidOperationException("Unknown completion-check route.")
            });
        NavigationState rootState = Shows("root");
        await ApplyAsync(presenter, rootState, NavigationState.Empty, token);
        Task pending = ApplyAsync(presenter, Shows("root", "sheet"), rootState, token);
        await root.PresentedNative.Task.WaitAsync(token);
        Require(!pending.IsCompleted, "Presentation completed before its actual native completion acknowledgment.");
        root.HoldNextDismissalCompletion();
        // Cancellation must revoke only the new target, not the still-current accepted frame's rollback lifetime.
        // ReSharper disable once MethodHasAsyncOverload
        sheetRetirement.Cancel();
        // This is the actual UIKit dismissal-completion callback, held before forwarding its acknowledgment.
        bool settledBeforeRollbackAcknowledgment;
        bool targetRetiredBeforeRollbackAcknowledgment;
        bool acceptedRootMountedBeforeAcknowledgment;
        try
        {
            await root.DismissedNative.Task.WaitAsync(token);
            settledBeforeRollbackAcknowledgment = pending.IsCompleted;
            targetRetiredBeforeRollbackAcknowledgment = sheet.Retired;
            acceptedRootMountedBeforeAcknowledgment = ReferenceEquals(window.RootViewController, root);
        }
        finally
        {
            // Release even when observation fails; do not mask the original control failure with held cleanup.
            root.DeliverDismissal();
        }

        try
        {
            await pending.WaitAsync(token);
            throw new InvalidOperationException("A retired presentation committed.");
        }
        catch (OperationCanceledException) when (sheetRetirement.IsCancellationRequested) { }

        Require(
            !settledBeforeRollbackAcknowledgment
            && !targetRetiredBeforeRollbackAcknowledgment
            && acceptedRootMountedBeforeAcknowledgment,
            "Accepted-frame rollback acknowledgment was bypassed: "
            + $"settled={settledBeforeRollbackAcknowledgment}, "
            + $"targetRetired={targetRetiredBeforeRollbackAcknowledgment}, "
            + $"originalRootMounted={acceptedRootMountedBeforeAcknowledgment}.");
        Require(pending.IsCompleted && sheet.Retired, "Retirement could not settle after native rollback acknowledgment.");
        Require(
            ReferenceEquals(window.RootViewController, root)
            && root.PresentedViewController is null
            && ReferenceEquals(root.View?.Window, window),
            "Rollback did not verify the same accepted root after actual dismissal acknowledgment.");
        UIViewController? beforeLate = window.RootViewController;
        root.DeliverPresentation();
        Require(ReferenceEquals(window.RootViewController, beforeLate), "A late completion regained native authority.");

        root.HoldPresentationCompletion = false;
        await ApplyAsync(presenter, rootState, rootState, token);
        await ApplyAsync(presenter, Shows("root", "terminal"), rootState, token);
        root.HoldNextDismissalCompletion();
        Task closing = ApplyAsync(presenter, rootState, Shows("root", "terminal"), token);
        await root.DismissedNative.Task.WaitAsync(token);
        Require(!closing.IsCompleted, "A dismissal committed without its native completion acknowledgment.");
        // Explicit mid-operation teardown is the assertion; await using also cleans failures safely.
        // ReSharper disable once DisposeOnUsingVariable
        Task disposal = presenter.DisposeAsync().AsTask();
        await disposal.WaitAsync(token);
        try
        {
            await closing.WaitAsync(token);
            throw new InvalidOperationException("Shutdown admitted a pending dismissal.");
        }
        catch (OperationCanceledException) when (closing.IsCanceled) { }

        Require(
            window.RootViewController is null && closing.IsCompleted,
            "Teardown waited indefinitely for a missing dismissal completion.");
        root.DeliverDismissal();
        Require(window.RootViewController is null, "A late dismissal completion resurrected disposed presentation.");
    }

    private static async Task ForeignHierarchyRetirementAsync(UIWindow window, CancellationToken token)
    {
        bool oldRetired = false;
        var original = new ProbeController();
        var replacement = new ProbeController();
        var uncommittedSheet = new ProbeController();
        var sdkChild = new ProbeController();
        var terminalChild = new ProbeController();
        await using UIKitNavigationPresenter presenter = Presenter(
            window,
            entry => entry.Id switch
            {
                // The original deliberately stops being current only at the explicit retirement step below.
                // ReSharper disable once AccessToModifiedClosure
                "original" => Page(original, () => !oldRetired),
                "replacement" => Page(replacement),
                "sheet" => Page(uncommittedSheet),
                _ => throw new InvalidOperationException("Unknown foreign-hierarchy route.")
            });
        NavigationState originalState = Shows("original");
        await ApplyAsync(presenter, originalState, NavigationState.Empty, token);
        await PresentForeignAsync(original, sdkChild, token);
        try
        {
            await ApplyAsync(presenter, Shows("original", "sheet"), originalState, token);
            throw new InvalidOperationException("A sheet dismissed foreign UI on a still-current root.");
        }
        catch (InvalidOperationException failure) when (failure.Message
                                                        == "A still-current foreign native presentation owns this surface.")
        {
        }

        Require(
            ReferenceEquals(original.PresentedViewController, sdkChild) && !sdkChild.Retired,
            "A current SDK-owned controller was adopted or retired as an AppNav route.");
        int dismissals = original.DismissalCalls;
        using (var canceled = new CancellationTokenSource())
        {
            await canceled.CancelAsync();
            try
            {
                await ApplyAsync(presenter, originalState, originalState, canceled.Token, NavigationPlanKind.Refresh);
                throw new InvalidOperationException("A canceled accepted-root refresh was acknowledged.");
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        }

        // Same topology alone is insufficient: only an actual accepted-state Refresh may preserve foreign UI.
        try
        {
            await ApplyAsync(presenter, originalState, originalState, token);
            throw new InvalidOperationException("Ordinary navigation bypassed foreign ownership.");
        }
        catch (InvalidOperationException failure) when (failure.Message
                                                        == "A still-current foreign native presentation owns this surface.")
        {
        }

        await ApplyAsync(presenter, originalState, originalState, token, NavigationPlanKind.Refresh);
        Require(
            ReferenceEquals(window.RootViewController, original)
            && ReferenceEquals(original.PresentedViewController, sdkChild)
            && ReferenceEquals(sdkChild.View?.Window, window)
            && !original.Retired
            && !sdkChild.Retired
            && original.DismissalCalls == dismissals,
            "An accepted root refresh dismissed or adopted foreign UI, or retired its still-current root.");
        oldRetired = true;
        await ApplyAsync(presenter, Shows("replacement"), originalState, token);
        Require(
            original.Retired && sdkChild.View?.Window is null && !sdkChild.Retired,
            "A retired root did not detach its original SDK hierarchy before releasing its native loan.");
        sdkChild.Dispose(); // This fixture is the SDK owner, not the presenter.
        await PresentForeignAsync(replacement, terminalChild, token);
        // This verifies terminal foreign-child retirement; await using remains the failure cleanup.
        // ReSharper disable once DisposeOnUsingVariable
        await presenter.DisposeAsync();
        Require(
            window.RootViewController is null && terminalChild.View?.Window is null && !terminalChild.Retired,
            "Terminal cleanup failed to detach its own child or disposed the SDK-owned object.");
        terminalChild.Dispose();
    }
}

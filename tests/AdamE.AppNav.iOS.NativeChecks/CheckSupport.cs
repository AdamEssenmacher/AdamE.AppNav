using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.State;

namespace AdamE.AppNav.iOS.NativeChecks;

/// <summary>The checks' shared shape: one window, its root stack, and a "sheet" modal over the root page.</summary>
internal static class Checks
{
    public const string Window = "main";
    public const string Stack = "stack";
    public const string Sheet = "sheet";

    public static UIKitNavigationTopology Topology { get; } =
        new UIKitNavigationTopology(Window, Stack).WithModal(Sheet, UIKitModalPlacement.OverRoot);

    public static UIKitNavigationPresenter Presenter(
        UIWindow window,
        Func<RouteEntry, CancellationToken, ValueTask<UIKitPage>> pages,
        Func<CancellationToken, Task>? back = null,
        Func<string, CancellationToken, Task>? popped = null,
        Func<CancellationToken, Task>? refresh = null,
        Action<Exception>? report = null,
        UIKitNavigationPresenterOptions? options = null,
        UIKitNavigationTopology? topology = null) =>
        new(
            window,
            topology ?? Topology,
            new DelegatePageFactory(pages),
            new DelegateHost(
                back ?? (static _ => Task.CompletedTask),
                popped ?? (static (_, _) => Task.CompletedTask),
                refresh ?? (static _ => Task.CompletedTask),
                report ?? (static _ => { })),
            options);

    public static UIKitNavigationPresenter Presenter(
        UIWindow window,
        Func<RouteEntry, UIKitPage> pages,
        Func<CancellationToken, Task>? back = null,
        Func<string, CancellationToken, Task>? popped = null,
        Func<CancellationToken, Task>? refresh = null,
        Action<Exception>? report = null) =>
        Presenter(window, (entry, _) => ValueTask.FromResult(pages(entry)), back, popped, refresh, report);

    /// <summary>A page that is current while <paramref name="current"/> says so and until
    /// <paramref name="retirement"/> is canceled.</summary>
    public static UIKitPage Page(
        UIViewController controller,
        Func<bool>? current = null,
        CancellationToken retirement = default,
        IDisposable? loan = null) =>
        new(controller) { IsCurrent = current, Retirement = retirement, Loan = loan };

    /// <summary>A root page whose own navigation controller entries are pushed onto.</summary>
    public static UIKitPage StackPage(
        UINavigationController navigation,
        Func<bool>? current = null,
        CancellationToken retirement = default) =>
        Page(navigation, current, retirement) with { IsNavigationStack = true };

    public static NavigationState Shows(string root, string? modal = null) => new(
        [
            new WindowNode(
                Window,
                new StackNode(Stack, [new RouteEntry(root, new CheckRoute())]),
                modal is null ? [] : [new ModalNode(Sheet, new RouteEntry(modal, new CheckRoute()))])
        ],
        Window);

    public static NavigationState Pushed(string root, string pushed) => new(
        [
            new WindowNode(
                Window,
                new StackNode(
                    Stack,
                    [new RouteEntry(root, new CheckRoute()), new RouteEntry(pushed, new CheckRoute())]),
                [])
        ],
        Window);

    public static Task ApplyAsync(
        UIKitNavigationPresenter presenter,
        NavigationState state,
        NavigationState current,
        CancellationToken token,
        NavigationPlanKind kind = NavigationPlanKind.Navigate)
    {
        var route = new CheckRoute();
        return presenter.ApplyAsync(
                new NavigationPlan(state, kind),
                new NavigationPresentationContext(
                    RouterNavigationRequest.FromRoute(route, NavigationRequestSource.Test),
                    route,
                    current,
                    "native-check"),
                token)
            .AsTask();
    }

    public static async Task PresentForeignAsync(UIViewController root, UIViewController child, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        root.PresentViewController(child, false, () => done.TrySetResult());
        await done.Task.WaitAsync(token);
    }

    public static bool Contains(Exception failure, Exception original) =>
        ReferenceEquals(failure, original)
        || (failure is AggregateException aggregate && aggregate.InnerExceptions.Any(cause => Contains(cause, original)))
        || (failure.InnerException is { } inner && Contains(inner, original));

    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    // The edge swipe, and on iOS 26+ the swipe from anywhere in the content.
    public static UIGestureRecognizer[] PopGestures(UINavigationController navigation) =>
        OperatingSystem.IsIOSVersionAtLeast(26)
            ? [
                EdgePop(navigation),
                navigation.InteractiveContentPopGestureRecognizer
                ?? throw new InvalidOperationException("iOS 26 has no content pop gesture.")
            ]
            : [EdgePop(navigation)];

    private static UIGestureRecognizer EdgePop(UINavigationController navigation) =>
        navigation.InteractivePopGestureRecognizer
        ?? throw new InvalidOperationException("A navigation controller has no edge pop gesture.");

    private sealed class DelegatePageFactory(Func<RouteEntry, CancellationToken, ValueTask<UIKitPage>> pages)
        : IUIKitPageFactory
    {
        public ValueTask<UIKitPage> CreatePageAsync(UIKitPageRequest request, CancellationToken cancellationToken) =>
            pages(request.Entry, cancellationToken);
    }

    private sealed class DelegateHost(
        Func<CancellationToken, Task> back,
        Func<string, CancellationToken, Task> popped,
        Func<CancellationToken, Task> refresh,
        Action<Exception> report) : IUIKitNavigationHost
    {
        public Task RequestBackAsync(CancellationToken cancellationToken) => back(cancellationToken);

        public Task RecordNativePopAsync(string entryId, CancellationToken cancellationToken) =>
            popped(entryId, cancellationToken);

        public Task RequestRefreshAsync(CancellationToken cancellationToken) => refresh(cancellationToken);

        public void ReportRecoveredFailure(Exception failure) => report(failure);
    }
}

internal sealed record CheckRoute : AppRoute;

internal sealed class ActionDisposable(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}

/// <summary>A controller that records how and when it was disposed, and can hold UIKit's completion callbacks.</summary>
internal sealed class ProbeController(Action? appeared = null) : UIViewController
{
    private bool _holdDismissalCompletion;
    private Action? _presentation;
    private Action? _dismissal;

    public bool Retired { get; private set; }
    public bool DisposeReturned { get; private set; }
    public bool DetachedAtRetirement { get; private set; }
    public int DismissalCalls { get; private set; }
    public Action? Retiring { get; set; }
    public bool HoldPresentationCompletion { get; set; }
    public TaskCompletionSource PresentedNative { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource DismissedNative { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override void PresentViewController(UIViewController controller, bool animated, Action? completionHandler) =>
        base.PresentViewController(
            controller,
            animated,
            () =>
            {
                _ = PresentedNative.TrySetResult();
                if (HoldPresentationCompletion)
                    _presentation = completionHandler;
                else
                    completionHandler?.Invoke();
            });

    public override void DismissViewController(bool animated, Action? completionHandler)
    {
        DismissalCalls++;
        base.DismissViewController(
            animated,
            () =>
            {
                // Terminal fire-and-forget dismissal cannot replace the held rollback acknowledgment.
                if (_holdDismissalCompletion && completionHandler is not null)
                {
                    _dismissal = completionHandler;
                    _ = DismissedNative.TrySetResult();
                }
                else
                    completionHandler?.Invoke();
            });
    }

    public void DeliverPresentation() => Interlocked.Exchange(ref _presentation, null)?.Invoke();

    public void HoldNextDismissalCompletion()
    {
        DismissedNative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _holdDismissalCompletion = true;
    }

    public void DeliverDismissal()
    {
        _holdDismissalCompletion = false;
        Interlocked.Exchange(ref _dismissal, null)?.Invoke();
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        View = new UIView { BackgroundColor = UIColor.White };
    }

    public override void ViewDidAppear(bool animated)
    {
        base.ViewDidAppear(animated);
        appeared?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Retired)
        {
            DetachedAtRetirement = ParentViewController is null && (!IsViewLoaded || View?.Window is null);
            Retired = true;
            Retiring?.Invoke();
        }

        base.Dispose(disposing);
        if (disposing)
            DisposeReturned = true;
    }
}

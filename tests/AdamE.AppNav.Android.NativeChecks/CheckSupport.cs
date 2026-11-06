using AdamE.AppNav.Leases;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;
using AdamE.AppNav.State;
using Android.Content;
using Android.OS;
using Android.Views;
using AndroidX.Activity;
using OperationCanceledException = System.OperationCanceledException;

namespace AdamE.AppNav.Android.NativeChecks;

/// <summary>The checks' shared shape: one window, its root stack, a "sheet" modal over the root page, a modal over any
/// stack, and an "owned" modal over the pushed entry it names.</summary>
internal static class Checks
{
    public const string Window = "main";
    public const string Stack = "stack";
    public const string Sheet = "sheet";
    public const string Anywhere = "anywhere";
    public const string Owned = "owned";
    public const string OwnerKey = "owner";

    public static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    public static NavigationTopology Topology { get; } = new NavigationTopology(Window, Stack)
        .WithModal(Sheet, NavigationModalPlacement.OverRoot)
        .WithModal(Anywhere, NavigationModalPlacement.OverAnyEntry)
        .WithOwnedModal(Owned, static entry => entry.Metadata?.TryGetValue(OwnerKey, out object? id) == true
            ? id as string
            : null);

    public static AndroidNavigationPresenter Presenter(
        Func<AndroidPageRequest, CancellationToken, ValueTask<AndroidPage>> pages,
        IAndroidNavigationHost? host = null,
        AndroidNavigationPresenterOptions? options = null,
        NavigationTopology? topology = null) =>
        new(topology ?? Topology, new DelegatePages(pages), host ?? new RecordingHost(), options);

    public static AndroidNavigationPresenter Presenter(
        Func<AndroidPageRequest, AndroidPage> pages,
        IAndroidNavigationHost? host = null,
        AndroidNavigationPresenterOptions? options = null) =>
        Presenter((request, _) => ValueTask.FromResult(pages(request)), host, options);

    /// <summary>Attaches <paramref name="activity"/> and puts the presenter's frame in its content.</summary>
    public static void Mount(AndroidNavigationPresenter presenter, ComponentActivity activity) =>
        activity.SetContentView(presenter.Attach(activity));

    /// <summary>The view the presenter's frame shows in <paramref name="activity"/>'s content, or null.</summary>
    public static View? Visible(ComponentActivity activity) =>
        activity.FindViewById<ViewGroup>(global::Android.Resource.Id.Content) is { ChildCount: 1 } content
        && content.GetChildAt(0) is ViewGroup { ChildCount: 1 } frame
            ? frame.GetChildAt(0)
            : null;

    /// <summary>A page that is current while <paramref name="current"/> says so and until
    /// <paramref name="retirement"/> is canceled.</summary>
    public static AndroidPage Page(
        View view,
        Func<bool>? current = null,
        CancellationToken retirement = default,
        IDisposable? loan = null,
        IDisposable? binding = null) =>
        AndroidPage.ForView(view) with { IsCurrent = current, Retirement = retirement, Loan = loan, Binding = binding };

    public static AndroidPage ModalPage(
        ComponentDialog dialog,
        Func<bool>? current = null,
        IDisposable? loan = null,
        IDisposable? binding = null) =>
        AndroidPage.ForDialog(dialog) with
        {
            IsCurrent = current, Loan = loan ?? (dialog as ProbeDialog)?.Loan(), Binding = binding
        };

    public static RouteEntry Entry(string id, string? owner = null) =>
        new(id, new CheckRoute(id), owner is null ? null : new Dictionary<string, object?> { [OwnerKey] = owner });

    public static NavigationState Shows(string root, string? modal = null, string modalId = Sheet) => new(
        [
            new WindowNode(
                Window,
                new StackNode(Stack, [Entry(root)]),
                modal is null ? [] : [new ModalNode(modalId, Entry(modal))])
        ],
        Window);

    public static NavigationState Pushed(string root, string pushed, ModalNode? modal = null) => new(
        [
            new WindowNode(
                Window,
                new StackNode(Stack, [Entry(root), Entry(pushed)]),
                modal is null ? [] : [modal])
        ],
        Window);

    public static Task ApplyAsync(
        AndroidNavigationPresenter presenter,
        NavigationState state,
        NavigationState current,
        CancellationToken token,
        NavigationPlanKind kind = NavigationPlanKind.Navigate)
    {
        var route = new CheckRoute("apply");
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

    /// <summary>Waits, on the main thread, until <paramref name="condition"/> holds.</summary>
    public static async Task UntilAsync(Func<bool> condition, string message, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(Soon);
        try
        {
            while (!condition())
                await Task.Delay(16, limit.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(message);
        }
    }

    /// <summary>Whether <paramref name="activity"/>'s window draws within <paramref name="within"/> of being
    /// invalidated.</summary>
    public static async Task<bool> DrawsAsync(ComponentActivity activity, TimeSpan within, CancellationToken token)
    {
        View decor = activity.Window?.DecorView ?? throw new InvalidOperationException("The activity has no window.");
        ViewTreeObserver observer = decor.ViewTreeObserver ?? throw new InvalidOperationException("No observer.");
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new DrawListener(() => drawn.TrySetResult());
        observer.AddOnDrawListener(listener);
        try
        {
            decor.Invalidate();
            Task first = await Task.WhenAny(drawn.Task, Task.Delay(within, token));
            token.ThrowIfCancellationRequested();
            return ReferenceEquals(first, drawn.Task);
        }
        finally
        {
            if (observer.IsAlive)
                observer.RemoveOnDrawListener(listener);
            else if (decor.ViewTreeObserver is { IsAlive: true } current)
                current.RemoveOnDrawListener(listener);
        }
    }

    public static bool OnMain => ReferenceEquals(Looper.MyLooper(), Looper.MainLooper);

    /// <summary>Lets the main looper run what is already queued, such as a dialog's dismiss message.</summary>
    public static Task TurnsAsync(CancellationToken token) => Task.Delay(100, token);

    public static bool Contains(Exception failure, Exception original) =>
        ReferenceEquals(failure, original)
        || (failure is AggregateException aggregate && aggregate.InnerExceptions.Any(cause => Contains(cause, original)))
        || (failure.InnerException is { } inner && Contains(inner, original));

    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static async Task<Exception> FailsAsync(Func<Task> operation, string message)
    {
        try
        {
            await operation();
        }
        catch (Exception failure)
        {
            return failure;
        }

        throw new InvalidOperationException(message);
    }

    private sealed class DelegatePages(Func<AndroidPageRequest, CancellationToken, ValueTask<AndroidPage>> pages)
        : IAndroidPageFactory
    {
        public ValueTask<AndroidPage> CreatePageAsync(AndroidPageRequest request, CancellationToken cancellationToken) =>
            pages(request, cancellationToken);
    }
}

internal sealed record CheckRoute(string Name) : AppRoute;

internal sealed class DrawListener(Action drawn) : Java.Lang.Object, ViewTreeObserver.IOnDrawListener
{
    public void OnDraw() => drawn();
}

/// <summary>Records every native event the presenter sends; each request can be answered by the check.</summary>
internal sealed class RecordingHost : IAndroidNavigationHost
{
    public List<string> Backs { get; } = [];
    public List<string> ModalBacks { get; } = [];
    public int Refreshes { get; private set; }
    public List<Exception> Reports { get; } = [];
    public bool OffMain { get; private set; }
    public Func<CancellationToken, Task>? OnBack { get; set; }
    public Func<string, CancellationToken, Task>? OnModalBack { get; set; }
    public Func<CancellationToken, Task>? OnRefresh { get; set; }

    public Task RequestBackAsync(CancellationToken cancellationToken)
    {
        Observe();
        Backs.Add("back");
        return OnBack?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task RequestModalBackAsync(string entryId, CancellationToken cancellationToken)
    {
        Observe();
        ModalBacks.Add(entryId);
        return OnModalBack?.Invoke(entryId, cancellationToken) ?? Task.CompletedTask;
    }

    public Task RequestRefreshAsync(CancellationToken cancellationToken)
    {
        Observe();
        Refreshes++;
        return OnRefresh?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public void ReportRecoveredFailure(Exception failure)
    {
        Observe();
        Reports.Add(failure);
    }

    private void Observe()
    {
        if (!ReferenceEquals(Looper.MyLooper(), Looper.MainLooper))
            OffMain = true;
    }
}

internal sealed class ActionDisposable(Action dispose) : IDisposable
{
    public void Dispose() => dispose();
}

/// <summary>A view that records when it was disposed, and whether it had left the window by then.</summary>
internal sealed class ProbeView(Context context) : View(context)
{
    public bool Disposed { get; private set; }
    public bool DetachedAtDisposal { get; private set; }
    public Action? Disposing { get; set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Disposed)
        {
            DetachedAtDisposal = Parent is null && !IsAttachedToWindow;
            Disposed = true;
            Disposing?.Invoke();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A dialog that records when the presenter retired it (released its loan), and whether it had left the
/// window by then. The presenter never disposes a dialog: Android may still deliver its queued dismiss messages.</summary>
internal sealed class ProbeDialog : ComponentDialog
{
    public ProbeDialog(Context context) : base(context) => SetContentView(new View(context));

    public bool Retired { get; private set; }
    public bool GoneAtRetirement { get; private set; }
    public bool Gone => !IsShowing && Window?.DecorView.IsAttachedToWindow != true;

    /// <summary>The loan a page of this dialog gives the presenter, released once the dialog is out of the window.
    /// </summary>
    public IDisposable Loan() => new ActionDisposable(() =>
    {
        GoneAtRetirement = Gone;
        Retired = true;
    });
}

/// <summary>Records, when a lease releases, whether the page that showed it had already left the window and been
/// disposed.</summary>
internal sealed class ReleaseProbe
{
    private Func<bool>? _gone;

    public bool Released { get; private set; }
    public bool ReleasedAfterNativeDisposal { get; private set; }

    public void Watch(ProbeView view) => _gone = () => view is { Disposed: true, DetachedAtDisposal: true };

    public void Watch(ProbeDialog dialog) => _gone = () => dialog.Gone;

    public void Release()
    {
        Released = true;
        ReleasedAfterNativeDisposal = _gone?.Invoke() == true;
    }
}

/// <summary>Runs lease work on the main thread, completing only when the work's own task has.</summary>
internal sealed class MainThreadLeaseHost : IRouteEntryLeaseHost
{
    private readonly Handler _main = new(Looper.MainLooper ?? throw new InvalidOperationException("No main looper."));
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
        _ = _main.Post(() => _ = RunAsync(work, done));
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

/// <summary>Each request shows one root whose entry ID is its route's name, so the next request drops the previous
/// root.</summary>
internal sealed class OneRootPlanner(Action<string>? onPlanning = null) : IAppNavigationPlanner
{
    public ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        var route = (CheckRoute)context.Route;
        onPlanning?.Invoke(route.Name);
        var state = new NavigationState(
            [new WindowNode(Checks.Window, new StackNode(Checks.Stack, [new RouteEntry(route.Name, route)]))],
            Checks.Window);
        return ValueTask.FromResult(new NavigationPlan(state));
    }
}

/// <summary>Each request pushes its route's entry over one root entry that stays; a route named with a "modal:" prefix
/// opens the modal over the root page instead.</summary>
internal sealed class PushedPlanner(string rootId) : IAppNavigationPlanner
{
    public ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        var route = (CheckRoute)context.Route;
        var root = new RouteEntry(rootId, new CheckRoute(rootId));
        var state = new NavigationState(
            [
                route.Name.StartsWith("modal:", StringComparison.Ordinal)
                    ? new WindowNode(
                        Checks.Window,
                        new StackNode(Checks.Stack, [root]),
                        [new ModalNode(Checks.Sheet, new RouteEntry(route.Name, route))])
                    : new WindowNode(
                        Checks.Window,
                        new StackNode(Checks.Stack, [root, new RouteEntry(route.Name, route)]))
            ],
            Checks.Window);
        return ValueTask.FromResult(new NavigationPlan(state));
    }
}

internal static class RouterChecks
{
    public static IRouterNavigator Router(
        IAppNavigationPlanner planner,
        INavigationPresenter presenter,
        RouteEntryLeaseRegistry leases) =>
        RouterNavigatorFactory.Create(
            new RouteTableBuilder().Build(),
            planner,
            presenter,
            new RouterNavigatorFactoryOptions { EntryLeases = leases });
}

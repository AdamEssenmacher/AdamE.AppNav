using System.Runtime.ExceptionServices;
using AdamE.AppNav.Leases;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Activity;
using OperationCanceledException = System.OperationCanceledException;

namespace AdamE.AppNav.Android;

/// <summary>
/// An <see cref="INavigationPresenter"/> for one activity at a time: a frame showing the window's root page or the page
/// pushed over it, and a declared modal as a dialog over them, in the shape its <see cref="NavigationTopology"/>
/// declares and <see cref="Support"/> allows.
/// </summary>
/// <remarks>
/// <para>
/// AppNav alone owns history, and on Android every Back is a request. While a page is pushed over the root page, or a
/// modal is open, system Back goes to the router through <see cref="IAndroidNavigationHost"/>; otherwise the presenter
/// leaves it to the activity, which by default finishes. A modal dialog's Back, and its own cancel, ask the router to
/// close that modal (<see cref="AndroidPage"/>).
/// </para>
/// <para>
/// The presenter outlives activities. Each activity attaches (<see cref="Attach"/>) and puts the frame it is given in
/// its content, and detaches (<see cref="Detach"/>) when it is destroyed; a recreated activity attaches again and gets
/// new pages for the same entries. An operation waits for an attached activity, and one whose activity detaches under
/// it waits for the next. A fresh activity's window draws nothing until the first operation in it ends.
/// </para>
/// <para>
/// Each operation builds the plan's pages, changes native state, waits for a laid-out frame and verifies the result
/// before it returns. A failed or canceled operation restores the accepted presentation, or clears the frame when that
/// presentation is no longer current, before it throws, so the router never commits a state native does not show. A
/// page is disposed only after it has physically left the window, and what it borrows is released only after that. If
/// a page's native retirement fails, the presenter keeps that page's loan and lease attachment and fails each later
/// operation, attachment and its own disposal with that failure.
/// </para>
/// <para>Dispose it alongside the router's disposal (the adapter contract's shutdown): disposal detaches the activity
/// and retires every page.</para>
/// </remarks>
public sealed class AndroidNavigationPresenter : INavigationPresenter, IAsyncDisposable
{
    private readonly NavigationTopology _topology;
    private readonly IAndroidPageFactory _pages;
    private readonly IAndroidNavigationHost _host;
    private readonly AndroidNavigationPresenterOptions _options;
    private readonly Lock _attachmentGate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Handler _main = new(Looper.MainLooper ?? throw new InvalidOperationException("No main looper."));
    private TaskCompletionSource<Epoch> _attached = NewAttachment();
    private Epoch? _epoch;
    private int _disposed;
    private AggregateException? _retirementFailure;
    private Task? _disposal;

    /// <summary>
    /// What this version presents of a <see cref="NavigationTopology"/>: a stack of the root page and one page pushed
    /// over it. Later versions widen it without changing the topology.
    /// </summary>
    public static NavigationTopologySupport Support { get; } = new(maxStackEntries: 2);

    /// <summary>A presenter that shows its pages in whichever activity is attached.</summary>
    /// <param name="topology">The shape every plan must have.</param>
    /// <param name="pages">Builds each entry's page.</param>
    /// <param name="host">Receives native events: Back requests, refresh requests and recovered failures.
    /// <see cref="AndroidRouterNavigationHost"/> sends them to the router.</param>
    /// <param name="options">Leases and placeholders.</param>
    public AndroidNavigationPresenter(
        NavigationTopology topology,
        IAndroidPageFactory pages,
        IAndroidNavigationHost host,
        AndroidNavigationPresenterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(host);
        _topology = topology;
        _pages = pages;
        _host = host;
        _options = options ?? new AndroidNavigationPresenterOptions();
    }

    /// <summary>Not raised. Every native event is a Back or refresh request through the
    /// <see cref="IAndroidNavigationHost"/>.</summary>
    public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Makes <paramref name="activity"/> the one the presenter shows its pages in, and returns the frame to put in its
    /// content (<c>SetContentView</c>). A previously attached activity is detached first. Then ask the router to
    /// present its accepted state again (<c>RefreshPresentationAsync</c>), or start it: the frame stays blank until an
    /// operation fills it.
    /// </summary>
    /// <remarks>Call it on the main thread, typically from <c>OnCreate</c>. The presenter registers its own Back
    /// callback with the activity's <c>OnBackPressedDispatcher</c>, enabled only while it has a Back to ask for.
    /// </remarks>
    public FrameLayout Attach(ComponentActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        MainOnly();
        ThrowIfRetirementFailed();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Epoch? previous;
        lock (_attachmentGate)
            previous = _epoch;
        if (previous is not null)
            Detach(previous.Activity);
        var next = new Epoch(activity);
        try
        {
            next.FirstDraw = new FirstDrawGate(next.Frame, () => FirstDrawReady(next));
            next.FirstDraw.Start();
            next.Back = new ActivityBack(this, next);
            activity.OnBackPressedDispatcher.AddCallback(activity, next.Back);
            lock (_attachmentGate)
            {
                _epoch = next;
                _ = _attached.TrySetResult(next);
            }
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            try { next.FirstDraw?.Stop(); }
            catch (Exception cleanup) { failures.Add(cleanup); }

            try { next.Back?.Stop(); }
            catch (Exception cleanup) { failures.Add(cleanup); }

            try { next.Frame.Dispose(); }
            catch (Exception cleanup) { failures.Add(cleanup); }

            if (failures.Count > 1)
            {
                var unsafeCleanup = new AggregateException("Native attachment and cleanup failed.", failures);
                RememberUnsafeCleanup(unsafeCleanup);
                throw unsafeCleanup;
            }

            throw;
        }

        return next.Frame;
    }

    /// <summary>
    /// Detaches <paramref name="activity"/> if it is the attached one: its pages are retired and its frame emptied,
    /// and an operation in progress waits for the next activity. Call it on the main thread from <c>OnDestroy</c>. An
    /// activity found finishing or destroyed during an operation is detached by the presenter itself.
    /// </summary>
    public void Detach(ComponentActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        MainOnly();
        Epoch? retired;
        lock (_attachmentGate)
        {
            if (_epoch is not { } current || !ReferenceEquals(current.Activity, activity))
                return;
            retired = current;
            _epoch = null;
            _attached = NewAttachment();
        }

        // Revoke queued native layout callbacks before any observed view is disposed.
        var failures = new List<Exception>();
        try
        {
            retired.FirstDraw?.Stop();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            retired.StopLayout?.Invoke();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }
        finally
        {
            retired.StopLayout = null;
        }

        try
        {
            retired.Back?.Stop();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            retired.Stopped.Cancel();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            Clear(retired);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            ReleaseDifference(retired.Staged ?? Surface.Empty, Surface.Empty);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            ReleaseDifference(retired.Surface, Surface.Empty);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        try
        {
            ReleaseDifference(retired.Spare, Surface.Empty);
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        retired.Staged = null;
        retired.Surface = Surface.Empty;
        retired.Spare = Surface.Empty;
        try
        {
            if (retired.Frame.Parent is ViewGroup parent)
                parent.RemoveView(retired.Frame);
            retired.Frame.Dispose();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        if (failures.Count == 0)
            return;
        AggregateException unsafeRetirement = RetainUnsafeFailure(
            new AggregateException("Native activity teardown failed.", failures));
        // A detached activity is not proof that its pages safely released what they borrow. Wake admitted work with
        // the same failure; a fresh activity must not hide an unsafe old one.
        _ = _attached.TrySetException(unsafeRetirement);
        throw unsafeRetirement;
    }

    /// <summary>Whether an activity is attached and no operation is applying to it: native Back is answered only then.
    /// Main thread.</summary>
    internal bool IsSettled
    {
        get
        {
            MainOnly();
            Epoch? epoch;
            lock (_attachmentGate)
                epoch = _epoch;
            return epoch is not null && IsAttached(epoch) && !epoch.Applying;
        }
    }

    /// <summary>The dialog the attached activity shows for the accepted modal, or null. Main thread.</summary>
    internal ComponentDialog? ShownDialog
    {
        get
        {
            MainOnly();
            Epoch? epoch;
            lock (_attachmentGate)
                epoch = _epoch;
            return epoch is not null && IsAttached(epoch) && epoch.ShownModal is { Dialog: { IsShowing: true } dialog }
                ? dialog
                : null;
        }
    }

    /// <inheritdoc />
    public async ValueTask ApplyAsync(
        NavigationPlan plan,
        NavigationPresentationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfRetirementFailed();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        CancellationToken operationToken = operation.Token;
        await _serial.WaitAsync(operationToken).ConfigureAwait(false);
        Epoch? last = null;
        try
        {
            while (true)
            {
                ThrowIfRetirementFailed();
                operationToken.ThrowIfCancellationRequested();
                Epoch epoch = await AttachedAsync(operationToken).ConfigureAwait(false);
                last = epoch;
                bool nativeCompleted = false;
                Surface? attempted = null;
                try
                {
                    await OnMainAsync(() => ApplyAttachedAsync(
                            epoch,
                            plan.TargetState,
                            operationToken,
                            resolved => attempted = resolved))
                        .ConfigureAwait(false);
                    nativeCompleted = true;
                    // The pages' own currency was last checked on the main thread, as the operation ended there: an
                    // application's IsCurrent is never called from here.
                    operationToken.ThrowIfCancellationRequested();
                    if (!IsAttached(epoch))
                        throw new OperationCanceledException(epoch.Stopped.Token);
                    return;
                }
                catch (OperationCanceledException) when (epoch.Stopped.IsCancellationRequested
                                                         && !operation.IsCancellationRequested
                                                         && Volatile.Read(ref _retirementFailure) is null)
                {
                    // The activity ended, not the navigation. Wait for the next one, which gets new pages for the same
                    // entries: their leases keep what they own, so no newer owner can take an entry's place.
                }
                catch (OperationCanceledException) when (!operation.IsCancellationRequested
                                                         && Volatile.Read(ref _retirementFailure) is null
                                                         && attempted is { } surface
                                                         && RetiredWhileShown(surface))
                {
                    // A lease retired after its page was built, while the router still shows its entry. The attempt
                    // has restored the accepted surface, or cleared native; apply the same plan again: the retired
                    // lease refuses to attach, so the entry gets its placeholder. A placeholder holds no lease, so this
                    // repeats at most once per leased page.
                }
                catch (OperationCanceledException failure)
                {
                    await OnMainAsync(() =>
                        {
                            if (nativeCompleted && IsAttached(epoch))
                            {
                                var failures = new List<Exception> { failure };
                                try
                                {
                                    Clear(epoch);
                                }
                                catch (Exception cleanup)
                                {
                                    failures.Add(cleanup);
                                }

                                try
                                {
                                    ReleaseDifference(epoch.Surface, Surface.Empty);
                                }
                                catch (Exception cleanup)
                                {
                                    failures.Add(cleanup);
                                }

                                epoch.Surface = Surface.Empty;
                                UpdateBack(epoch);
                                if (failures.Count > 1)
                                    throw new AggregateException(
                                        "Canceled native presentation cleanup failed.",
                                        failures);
                            }

                            return Task.CompletedTask;
                        })
                        .ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch (Exception failure) when (Volatile.Read(ref _retirementFailure) is not null)
        {
            throw RetainUnsafeFailure(failure);
        }
        finally
        {
            // Whatever the operation's outcome, the activity now shows what it will show: the gate that kept a fresh
            // frame from drawing empty opens. Not after an attempt the operation retries, which would flash it.
            if (last is { } ended)
                _main.Post(() => ended.FirstDraw?.Open());
            _ = _serial.Release();
        }
    }

    private async Task ApplyAttachedAsync(
        Epoch epoch,
        NavigationState state,
        CancellationToken operation,
        Action<Surface> resolved)
    {
        MainOnly();
        ThrowIfRetirementFailed();
        operation.ThrowIfCancellationRequested();
        RequireEpoch(epoch); // Nothing is built for a finishing activity.
        Surface previous = epoch.Surface;
        // The pages this operation may reuse: those shown, or those a rollback took out of the frame but kept, still
        // current, when it could not restore them.
        Surface kept = previous.With(epoch.Spare);
        Surface? staged = null;
        bool accepted = false;
        CancellationTokenSource? owned = null;
        epoch.Applying = true;
        UpdateBack(epoch);
        try
        {
            using (var building = CancellationTokenSource.CreateLinkedTokenSource(operation, epoch.Stopped.Token))
                staged = await MaterializeAsync(epoch, state, kept, building.Token);
            resolved(staged);
            owned = CancellationTokenSource.CreateLinkedTokenSource(
                [operation, epoch.Stopped.Token, .. Retirements(staged)]);
            RequireEpoch(epoch);
            RequireCurrent(staged);
            epoch.Staged = staged;
            owned.Token.ThrowIfCancellationRequested();
            _ = Mutate(epoch, staged);
            owned.Token.ThrowIfCancellationRequested();
            if (staged.Root is not null)
                await NativeLayoutAsync(epoch, staged, owned.Token);
            owned.Token.ThrowIfCancellationRequested();
            RequireEpoch(epoch);
            RequireCurrent(staged);
            Verify(epoch, staged);
            owned.Token.ThrowIfCancellationRequested();
            RequireCurrent(staged);
            epoch.Surface = staged;
            accepted = true;
            // No rollback may resurrect these pages once their disposal begins.
            epoch.Spare = Surface.Empty;
            ReleaseDifference(kept, staged);
            owned.Token.ThrowIfCancellationRequested();
            RequireEpoch(epoch);
            RequireCurrent(staged);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            // Whatever failed, a finishing activity takes no rollback: it detaches here, so the operation waits for the
            // next activity. A rollback would reach RequireEpoch, which detaches, and then clear a disposed frame.
            try
            {
                DetachIfFinishing(epoch);
            }
            catch (Exception teardown)
            {
                failures.Add(teardown);
            }

            if (epoch.Stopped.IsCancellationRequested)
                throw DetachedFailure(epoch, staged, failure, failures);

            if (accepted)
            {
                try
                {
                    Clear(epoch);
                }
                catch (Exception cleanup)
                {
                    failures.Add(cleanup);
                }

                if (staged is not null)
                    try
                    {
                        ReleaseDifference(staged, Surface.Empty);
                    }
                    catch (Exception cleanup)
                    {
                        failures.Add(cleanup);
                    }

                epoch.Surface = Surface.Empty;
                if (failures.Count > 1)
                    throw new AggregateException("Accepted native state could not retire safely.", failures);
                throw;
            }

            // Restore the accepted surface as the operation found it. The rollback does not wait for layout: a view
            // put back in the attached frame is attached at once, and a dialog shown again attaches its window on
            // Android's next traversal, which a hidden activity may not run until it is started again; waiting would
            // hold every later operation. If the surface is not current any more, or cannot be restored, clear the
            // frame and give the accepted surface up, so the presenter never claims what native does not show.
            bool restored = false;
            bool restoreFailed = false;
            try
            {
                if (Current(previous))
                {
                    _ = Mutate(epoch, previous);
                    RequireEpoch(epoch);
                    RequireCurrent(previous);
                    // Its dialog is verified as showing, whether this rollback showed it or an earlier one did without
                    // seeing its window attach.
                    Verify(epoch, previous, previous.Modal);
                    restored = true;
                }
            }
            catch (OperationCanceledException) when (epoch.Stopped.IsCancellationRequested || !Current(previous))
            {
                // The accepted surface stopped mattering while it was being restored.
            }
            catch (Exception rollback)
            {
                failures.Add(rollback);
                restoreFailed = true;
            }

            if (epoch.Stopped.IsCancellationRequested)
                throw DetachedFailure(epoch, staged, failure, failures);
            if (!restored)
            {
                // Nothing is shown now. Pages that are still current are kept, out of the window, for the next
                // operation to reuse (a root page keeps its state across a retired pushed page); the others go now.
                // Pages that just failed to be restored are not kept: reusing them would fail the same way.
                try
                {
                    Clear(epoch);
                }
                catch (Exception cleanup)
                {
                    failures.Add(cleanup);
                }

                Surface spare = restoreFailed ? Surface.Empty : kept.Current();
                try
                {
                    ReleaseDifference(kept, spare);
                }
                catch (Exception cleanup)
                {
                    failures.Add(cleanup);
                }

                epoch.Surface = Surface.Empty;
                epoch.Spare = spare;
            }

            if (staged is not null)
                try
                {
                    ReleaseDifference(staged, kept.With(epoch.Spare));
                }
                catch (Exception cleanup)
                {
                    failures.Add(cleanup);
                }

            if (failures.Count > 1)
                throw new AggregateException("Android presentation and cleanup failed.", failures);
            throw;
        }
        finally
        {
            owned?.Dispose();
            epoch.Staged = null;
            epoch.Applying = false;
            UpdateBack(epoch);
        }
    }

    // An operation whose activity detached under it. This can run inline inside Detach's Stopped.Cancel(), before
    // Detach clears the activity's pages, or after Detach returned and disposed the frame. So it does not clear the
    // frame or dispose it: it lets go of the shown dialog and takes the staged pages out of the frame and retires them,
    // and Detach does the rest. A staged dialog must no longer be the shown dialog, or Detach's clear dismisses it again.
    // If that retirement fails, the presenter keeps the failure as unsafe and stops.
    private Exception DetachedFailure(Epoch epoch, Surface? staged, Exception failure, List<Exception> failures)
    {
        if (staged?.Modal is { } stagedModal && ReferenceEquals(epoch.ShownModal, stagedModal))
            epoch.ShownModal = null;
        if (staged is not null)
            try
            {
                ReleaseDifference(staged, Surface.Empty);
            }
            catch (Exception cleanup)
            {
                failures.Add(cleanup);
            }

        if (failures.Count > 1)
            return new AggregateException("Detached native presentation could not retire safely.", failures);
        if (Volatile.Read(ref _retirementFailure) is not null)
            return RetainUnsafeFailure(failure);
        return new OperationCanceledException("The attached activity detached.", failure, epoch.Stopped.Token);
    }

    private static CancellationToken[] Retirements(Surface surface) =>
    [
        .. new[] { surface.Root, surface.Top, surface.Modal }
            .OfType<NativePage>()
            .SelectMany(static page => new[] { page.LeaseRetirement, page.PageRetirement })
    ];

    // Whether a page of this attempt holds a lease that retired while the router still shows its entry.
    private static bool RetiredWhileShown(Surface surface) =>
        new[] { surface.Root, surface.Top, surface.Modal }
            .Any(static page => page?.Lease is { IsRetired: true, IsCommitted: true });

    // The declared shape is checked before anything is built. A page the accepted surface still shows is kept while
    // it is current; every other is built for this activity.
    private async Task<Surface> MaterializeAsync(
        Epoch epoch,
        NavigationState state,
        Surface previous,
        CancellationToken cancellationToken)
    {
        if (_topology.Match(state, Support) is not { } match)
            return Surface.Empty;
        StackNode stack = match.Stack;
        // A topology matches at most one modal, with nothing nested in it.
        ModalNode? modal = match.Modals.SingleOrDefault()?.Node;
        NativePage? root = null;
        NativePage? top = null;
        NativePage? sheet = null;
        try
        {
            root = await ReuseAsync(epoch, previous.Root, stack.Entries[0], AndroidPagePlacement.Root, cancellationToken);
            Validate(root, previous, null, null);
            RequireEpoch(epoch);
            cancellationToken.ThrowIfCancellationRequested();
            if (stack.Entries.Count == 2)
            {
                top = await ReuseAsync(
                    epoch,
                    previous.Top,
                    stack.Entries[1],
                    AndroidPagePlacement.Pushed,
                    cancellationToken);
                Validate(top, previous, root, null);
                RequireEpoch(epoch);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (modal is not null)
            {
                sheet = await ReuseAsync(
                    epoch,
                    previous.Modal,
                    modal.RouteEntry,
                    AndroidPagePlacement.Modal,
                    cancellationToken);
                Validate(sheet, previous, root, top);
                if (!ReferenceEquals(sheet, previous.Modal))
                    WireModal(epoch, sheet);
            }

            return new Surface(root, top, sheet);
        }
        catch (Exception failure)
        {
            if (AndroidCleanupFailureException.IsIn(failure))
                RememberUnsafeCleanup(failure);
            try
            {
                ReleaseCandidates([root, top, sheet], previous);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("Native materialization and cleanup failed.", failure, cleanup);
            }

            throw;
        }
    }

    // The pages a failed operation built, in order. A view or dialog the accepted surface still shows is that surface's
    // to dispose, and one an earlier candidate here has already disposed is gone: a candidate sharing either only
    // gives back what it borrows.
    private void ReleaseCandidates(NativePage?[] candidates, Surface previous)
    {
        var owners = new List<NativePage>(new[] { previous.Root, previous.Top, previous.Modal }.OfType<NativePage>());
        var failures = new List<Exception>();
        foreach (NativePage candidate in candidates.OfType<NativePage>())
        {
            if (owners.Any(owner => ReferenceEquals(owner, candidate)))
                continue;
            try
            {
                candidate.Release(native: !owners.Any(candidate.SharesNativeWith));
            }
            catch (Exception cleanup)
            {
                failures.Add(cleanup);
            }
            finally
            {
                owners.Add(candidate);
            }
        }

        if (failures.Count == 0)
            return;
        var failure = new AggregateException("Native page retirement failed.", failures);
        RememberUnsafeCleanup(failure);
        throw failure;
    }

    private Task<NativePage> ReuseAsync(
        Epoch epoch,
        NativePage? old,
        RouteEntry entry,
        AndroidPagePlacement placement,
        CancellationToken cancellationToken) =>
        old is not null && old.Placement == placement && Same(old.Entry, entry) && old.IsCurrent
            ? Task.FromResult(old)
            : CreatePageAsync(epoch, entry, placement, cancellationToken);

    /// <summary>
    /// A leased entry's page is attached before its view exists, and detached only after the presenter has taken that
    /// view or dialog out of the window and disposed it. A retired lease whose entry the router still shows gets a
    /// placeholder that borrows nothing.
    /// </summary>
    private async Task<NativePage> CreatePageAsync(
        Epoch epoch,
        RouteEntry entry,
        AndroidPagePlacement placement,
        CancellationToken cancellationToken)
    {
        if (_options.EntryLeases is not { } leases)
            return await BuildAsync(epoch, entry, placement, null, null, cancellationToken);
        // With a registry, every page is leased. An entry the application opened no lease for, such as an ancestor its
        // planner added, gets one opened here, during presentation, which core closes when the operation ends.
        RouteEntryLease lease = leases.TryGet(entry.Id, out RouteEntryLease? found) && found is not null
            ? found
            : leases.GetOrOpen(entry.Id, out _);
        // TryAttach is the check: the application may retire the lease at any time, so an earlier IsRetired could be
        // stale. A retired lease may already have released what the page would borrow, so build nothing from it.
        if (!lease.TryAttach(out RouteEntryLeaseAttachment? attachment))
            return lease.IsCommitted
                ? Placeholder(epoch, entry, placement)
                : throw new OperationCanceledException(
                    $"The lease of route entry '{entry.Id}' retired before its page was shown.");
        try
        {
            return await BuildAsync(epoch, entry, placement, lease, attachment, cancellationToken);
        }
        catch (OperationCanceledException canceled) when (!cancellationToken.IsCancellationRequested
                                                          && lease is { IsRetired: true, IsCommitted: true }
                                                          && !AndroidCleanupFailureException.IsIn(canceled))
        {
            // The lease retired during the build while the router still shows its entry. Nothing native survived the
            // canceled build, so give the lease back and show the placeholder instead.
            attachment.Detach();
            return Placeholder(epoch, entry, placement);
        }
        catch (Exception failure) when (!AndroidCleanupFailureException.IsIn(failure))
        {
            // Nothing native survived the failed build, so nothing still uses the lease.
            attachment.Detach();
            throw;
        }
    }

    private async Task<NativePage> BuildAsync(
        Epoch epoch,
        RouteEntry entry,
        AndroidPagePlacement placement,
        RouteEntryLease? lease,
        RouteEntryLeaseAttachment? attachment,
        CancellationToken cancellationToken)
    {
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lease?.RetiredToken ?? CancellationToken.None);
        owned.Token.ThrowIfCancellationRequested();
        AndroidPage page = await _pages.CreatePageAsync(
                               new AndroidPageRequest(entry, placement, lease, epoch.Activity),
                               owned.Token)
                           ?? throw new InvalidOperationException($"The page factory returned no page for '{entry.Id}'.");
        // A stale page is still handed back, so the presenter retires it and a failed retirement stays sticky; its own
        // currency is checked once the presenter owns it.
        return new NativePage(entry, placement, page, lease, attachment, placeholder: false);
    }

    private NativePage Placeholder(Epoch epoch, RouteEntry entry, AndroidPagePlacement placement)
    {
        AndroidPage page;
        if (placement is AndroidPagePlacement.Modal)
        {
            ComponentDialog dialog = _options.CreatePlaceholderDialog?.Invoke(entry, epoch.Activity) ?? Blank(epoch);
            page = AndroidPage.ForDialog(dialog);
        }
        else
            page = AndroidPage.ForView(
                _options.CreatePlaceholderView?.Invoke(entry, placement, epoch.Activity) ?? new View(epoch.Activity));

        return new NativePage(entry, placement, page, null, null, placeholder: true);

        static ComponentDialog Blank(Epoch epoch)
        {
            var blank = new ComponentDialog(epoch.Activity);
            blank.SetContentView(new View(epoch.Activity));
            return blank;
        }
    }

    // A page of the wrong kind, a view that already has a parent, a dialog already showing, or a native object another
    // page of this surface already uses is refused before anything native changes. The page is retired with the
    // operation's other new pages.
    private static void Validate(NativePage page, Surface previous, NativePage? root, NativePage? top)
    {
        bool modal = page.Placement is AndroidPagePlacement.Modal;
        if (modal ? page.Dialog is null : page.View is null)
            throw new InvalidOperationException(
                $"The page for {page.Placement} entry '{page.Entry.Id}' must be "
                + (modal ? "a dialog (AndroidPage.ForDialog)." : "a view (AndroidPage.ForView)."));
        foreach (NativePage? other in new[] { previous.Root, previous.Top, previous.Modal, root, top })
            if (other is not null
                && !ReferenceEquals(other, page)
                && ((page.View is not null && ReferenceEquals(other.View, page.View))
                    || (page.Dialog is not null && ReferenceEquals(other.Dialog, page.Dialog))))
                throw new InvalidOperationException("Different route pages must own different views and dialogs.");
        if (ReferenceEquals(page, previous.Root) || ReferenceEquals(page, previous.Top)
                                                 || ReferenceEquals(page, previous.Modal))
            return;
        if (page.View is { Parent: not null })
            throw new InvalidOperationException($"The view for entry '{page.Entry.Id}' already has a parent.");
        if (page.Dialog is { IsShowing: true })
            throw new InvalidOperationException($"The dialog for entry '{page.Entry.Id}' is already showing.");
    }

    // A modal's Back and its own cancel ask the router to close it; any other dismissal the presenter did not make
    // asks for a refresh. The handlers are removed before the page's binding is disposed.
    private void WireModal(Epoch epoch, NativePage page)
    {
        ComponentDialog dialog = page.Dialog ?? throw new InvalidOperationException("A modal page has no dialog.");
        // Added without a lifecycle owner: a ComponentDialog's lifecycle is destroyed each time it is dismissed, which
        // would drop the callback before the presenter shows the dialog again. Its dispatcher outlives that.
        var back = new ModalBack(() => ModalBackIntent(epoch, page));
        page.Back = back;
        // A late cancel message for a dialog the presenter has shown again since is not this showing's cancel.
        page.Canceled = (_, _) => page.CancelSeen = !dialog.IsShowing;
        page.Dismissed = (_, _) => Dismissed(epoch, page);
        dialog.OnBackPressedDispatcher.AddCallback(back);
        dialog.CancelEvent += page.Canceled;
        dialog.DismissEvent += page.Dismissed;
    }

    private static bool Same(RouteEntry left, RouteEntry right) =>
        left.Id == right.Id && Equals(left.Route, right.Route);

    // Returns the modal page whose dialog it showed, if it showed one.
    private NativePage? Mutate(Epoch epoch, Surface target)
    {
        MainOnly();
        RequireEpoch(epoch);
        if (epoch.ShownModal is { } old && !ReferenceEquals(old, target.Modal))
        {
            old.DismissByPresenter();
            epoch.ShownModal = null;
        }

        View? visible = target.VisibleRoot?.View;
        if (!ReferenceEquals(epoch.Frame.GetChildAt(0), visible) || epoch.Frame.ChildCount != (visible is null ? 0 : 1))
        {
            epoch.Frame.RemoveAllViews();
            if (visible is not null)
            {
                if (visible.Parent is not null)
                    throw new InvalidOperationException("A page's view belongs to another native parent.");
                epoch.Frame.AddView(visible, new FrameLayout.LayoutParams(-1, -1));
                RequireEpoch(epoch);
                visible.RequestApplyInsets();
            }
        }

        RequireEpoch(epoch);
        if (target.Modal is { Dialog: { } dialog } modal)
        {
            epoch.ShownModal = modal;
            if (!dialog.IsShowing)
            {
                modal.CancelSeen = false;
                modal.PresenterDismissing = false;
                modal.ClosedByOthers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                dialog.Show();
                return modal;
            }
        }

        return null;
    }

    // justShown: a modal whose dialog may have been shown without waiting for layout, which attaches its window on
    // Android's next traversal: showing is all it can be verified for yet.
    private void Verify(Epoch epoch, Surface target, NativePage? justShown = null)
    {
        MainOnly();
        RequireEpoch(epoch);
        View? visible = target.VisibleRoot?.View;
        if (epoch.Frame.ChildCount != (visible is null ? 0 : 1)
            || !ReferenceEquals(epoch.Frame.GetChildAt(0), visible)
            || !ReferenceEquals(epoch.ShownModal, target.Modal)
            || visible is { IsAttachedToWindow: false }
            // A dialog the person or the application closed while the operation ran is accepted: its own dismissal
            // handler asks the router about it. Any other must be showing, in the window.
            || (target.Modal is { Dialog: { } dialog } modal
                && !modal.ClosedByOthersOrNotShowing
                && !(dialog.IsShowing
                     && (ReferenceEquals(modal, justShown) || dialog.Window?.DecorView.IsAttachedToWindow == true))))
            throw new InvalidOperationException("Native hierarchy does not match the accepted AppNav target.");
    }

    private void Clear(Epoch epoch)
    {
        try
        {
            if (epoch.ShownModal is { } modal)
            {
                modal.DismissByPresenter();
                epoch.ShownModal = null;
            }

            epoch.Frame.RemoveAllViews();
        }
        catch (Exception failure)
        {
            RememberUnsafeCleanup(failure);
            throw;
        }
    }

    private bool IsAttached(Epoch epoch)
    {
        lock (_attachmentGate)
            return ReferenceEquals(_epoch, epoch) && !epoch.Stopped.IsCancellationRequested;
    }

    private void RequireEpoch(Epoch epoch)
    {
        DetachIfFinishing(epoch);
        if (!IsAttached(epoch))
            throw new OperationCanceledException("The activity is no longer attached.", epoch.Stopped.Token);
    }

    // An attached activity that is finishing detaches here, as its OnDestroy will: an operation on it then waits for
    // the next activity like any other. Android can finish an activity before its OnCreate returns (a launcher relaunch
    // that clears the task while the process starts), so an operation can start on one that is already finishing.
    private void DetachIfFinishing(Epoch epoch)
    {
        if (IsAttached(epoch) && (epoch.Activity.IsFinishing || epoch.Activity.IsDestroyed))
            Detach(epoch.Activity);
    }

    private async Task<Epoch> AttachedAsync(CancellationToken token)
    {
        while (true)
        {
            Task<Epoch> task;
            lock (_attachmentGate)
                task = _attached.Task;
            Epoch epoch = await task.WaitAsync(token).ConfigureAwait(false);
            if (IsAttached(epoch))
                return epoch;
        }
    }

    private static TaskCompletionSource<Epoch> NewAttachment() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool Current(Surface surface) =>
        (surface.Root?.IsCurrent ?? true) && (surface.Top?.IsCurrent ?? true) && (surface.Modal?.IsCurrent ?? true);

    private static void RequireCurrent(Surface surface)
    {
        if (!Current(surface))
            throw new OperationCanceledException("A page stopped belonging to the application's current state.");
    }

    private void UpdateBack(Epoch epoch)
    {
        if (epoch.Back is { } callback)
            callback.Enabled = IsAttached(epoch)
                               && (epoch.Applying
                                   || epoch.Surface.Modal is not null
                                   || epoch.ShownModal is not null
                                   || epoch.Surface.Top is not null);
    }

    // System Back on the activity: for the accepted modal when one is open (its dialog was canceled and is waiting for
    // the router), otherwise for the window. Back during an operation is swallowed: the operation decides what shows.
    private void BackIntent(Epoch epoch)
    {
        if (!IsAttached(epoch) || epoch.Applying || Volatile.Read(ref _disposed) != 0)
            return;
        if (epoch.Surface.Modal is { } modal)
        {
            if (modal.IsCurrent)
                _ = RequestModalBackAsync(epoch, modal);
        }
        else if ((epoch.Surface.Top ?? epoch.Surface.Root) is { IsCurrent: true } original)
            _ = RequestAsync(
                epoch,
                original,
                // Still the page this Back was pressed on, with no modal over it and no operation changing it.
                () => !epoch.Applying
                      && epoch.Surface.Modal is null
                      && ReferenceEquals(epoch.Surface.Top ?? epoch.Surface.Root, original),
                () => _host.RequestBackAsync(_shutdown.Token));
    }

    private void ModalBackIntent(Epoch epoch, NativePage modal)
    {
        if (IsAttached(epoch)
            && !epoch.Applying
            && Volatile.Read(ref _disposed) == 0
            && modal.IsCurrent
            && ReferenceEquals(epoch.ShownModal, modal))
            _ = RequestModalBackAsync(epoch, modal);
    }

    private void Dismissed(Epoch epoch, NativePage modal)
    {
        // The presenter's own dismissal, or a late event for a dialog it has shown again since.
        if (modal.PresenterDismissing || modal.Dialog is not { IsShowing: false })
            return;
        // An operation waiting for this dialog's layout stops waiting: it accepts a dialog the person or the
        // application closed, and this handler asks the router what that means.
        modal.ClosedByOthers.TrySetResult();
        if (!IsAttached(epoch) || Volatile.Read(ref _disposed) != 0 || !modal.IsCurrent)
            return;
        if (modal.CancelSeen)
            // The dialog's own cancel (a tap outside it, a sheet dragged away) is a Back request for it.
            _ = RequestModalBackAsync(epoch, modal);
        else if (ReferenceEquals(epoch.ShownModal, modal))
            _ = RefreshAsync(
                epoch,
                modal,
                new InvalidOperationException(
                    $"The dialog for modal entry '{modal.Entry.Id}' was dismissed by something other than the presenter."));
    }

    // A modal Back request; if the router keeps the modal, its dialog is shown again by a refresh. An operation still
    // applying may yet remove the modal; the refresh then only presents what the router accepted.
    private async Task RequestModalBackAsync(Epoch epoch, NativePage modal)
    {
        await RequestAsync(
            epoch,
            modal,
            () => ReferenceEquals((epoch.Staged ?? epoch.Surface).Modal, modal),
            () => _host.RequestModalBackAsync(modal.Entry.Id, _shutdown.Token));
        if (IsAttached(epoch)
            && Volatile.Read(ref _disposed) == 0
            && ReferenceEquals((epoch.Staged ?? epoch.Surface).Modal, modal)
            && modal is { IsCurrent: true, Dialog.IsShowing: false })
            await RefreshAsync(epoch, modal, null);
    }

    private async Task RefreshAsync(Epoch epoch, NativePage original, Exception? cause)
    {
        if (cause is not null)
            _host.ReportRecoveredFailure(cause);
        await RequestAsync(epoch, original, static () => true, () => _host.RequestRefreshAsync(_shutdown.Token));
    }

    // A host request that cannot fail the presenter: its failure is reported, and a cancellation by shutdown, by the
    // activity's end or by the page's retirement is expected. The request is made in a later turn of the looper, and
    // only if then the activity is still attached, the page it came from still current, and stillValid still holds:
    // an event from an activity that detached, or about a page that has gone, never reaches the host. A request
    // already made is not canceled by the activity's end: an operation it starts waits for the next activity.
    private async Task RequestAsync(Epoch epoch, NativePage original, Func<bool> stillValid, Func<Task> request)
    {
        try
        {
            if (IsAttached(epoch))
                await DetachedAsync(() => IsAttached(epoch) && original.IsCurrent && stillValid(), request);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested
                                                 || epoch.Stopped.IsCancellationRequested
                                                 || !original.IsCurrent)
        {
        }
        catch (Exception failure)
        {
            _host.ReportRecoveredFailure(failure);
        }
    }

    // A host callback runs on the main thread, in a later turn of its looper, with no execution context from the
    // operation that reported it: a host that calls the router from inside an operation's context would be refused as
    // a reentrant operation. It calls nothing once valid no longer holds, and completes on the main thread.
    private Task DetachedAsync(Func<bool> valid, Func<Task> callback)
    {
        var done = new TaskCompletionSource();
        using (ExecutionContext.SuppressFlow())
            // Handler accepts Action; the whole awaited body reports through done, never an async-void escape.
            // ReSharper disable once AsyncVoidLambda
            _ = _main.Post(async () =>
            {
                try
                {
                    // Disposal may have begun since the event was posted: a host is never called after it.
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        _ = done.TrySetCanceled(_shutdown.Token);
                        return;
                    }

                    if (valid())
                        await callback();
                    _ = done.TrySetResult();
                }
                catch (OperationCanceledException canceled)
                {
                    _ = done.TrySetCanceled(canceled.CancellationToken);
                }
                catch (Exception failure)
                {
                    _ = done.TrySetException(failure);
                }
            });
        return done.Task;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_attachmentGate)
        {
            if (_disposal is { } existing)
                return new ValueTask(existing);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
            _ = Interlocked.Exchange(ref _disposed, 1);
        }

        _ = DisposeCoreAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            // Synchronous cancellation revokes authority before awaiting native teardown.
            // ReSharper disable once MethodHasAsyncOverload
            _shutdown.Cancel();
            // Drain the operation in flight before detaching: every wait in it takes the shutdown token, so it ends
            // promptly, and it never sees its activity's views disposed under it.
            await _serial.WaitAsync().ConfigureAwait(false);
            try
            {
                await OnMainAsync(() =>
                    {
                        ThrowIfRetirementFailed();
                        Epoch? epoch;
                        lock (_attachmentGate)
                            epoch = _epoch;
                        if (epoch is not null)
                            Detach(epoch.Activity);
                        ThrowIfRetirementFailed();
                        return Task.CompletedTask;
                    })
                    .ConfigureAwait(false);
            }
            finally
            {
                _ = _serial.Release();
            }

            _ = completion.TrySetResult();
        }
        catch (Exception failure)
        {
            // Repeated disposal must keep this outcome; success would authorize releasing what pages still borrow.
            _ = completion.TrySetException(
                Volatile.Read(ref _retirementFailure) is null
                    ? failure
                    : RetainUnsafeFailure(failure));
        }
    }

    private AggregateException RetainUnsafeFailure(Exception failure)
    {
        AggregateException? unsafeCleanup = Volatile.Read(ref _retirementFailure);
        Exception combined = unsafeCleanup is null
                             || unsafeCleanup.Flatten().InnerExceptions.All(cause => ContainsCause(failure, cause))
            ? failure
            : new AggregateException("Native operation and unsafe cleanup failed.", failure, unsafeCleanup);
        AggregateException retained = combined as AggregateException
                                      ?? new AggregateException(
                                          "Native cleanup did not establish safe retirement.",
                                          combined);
        Volatile.Write(ref _retirementFailure, retained);
        return retained;
    }

    private static bool ContainsCause(Exception failure, Exception cause) =>
        ReferenceEquals(failure, cause)
        || (failure is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(nested => ContainsCause(nested, cause))
            : failure.InnerException is { } inner && ContainsCause(inner, cause));

    private void RememberUnsafeCleanup(Exception failure)
    {
        AggregateException unsafeCleanup = failure as AggregateException
                                           ?? new AggregateException(
                                               "Native cleanup did not establish safe retirement.",
                                               failure);
        _ = Interlocked.CompareExchange(ref _retirementFailure, unsafeCleanup, null);
    }

    private void ThrowIfRetirementFailed()
    {
        if (Volatile.Read(ref _retirementFailure) is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private Task OnMainAsync(Func<Task> action)
    {
        if (ReferenceEquals(Looper.MyLooper(), Looper.MainLooper))
            return action();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Handler accepts Action; every awaited failure is forwarded through done rather than escaping async void.
        // ReSharper disable once AsyncVoidLambda
        _ = _main.Post(async () =>
        {
            try
            {
                await action();
                _ = done.TrySetResult();
            }
            catch (Exception failure)
            {
                _ = done.TrySetException(failure);
            }
        });
        return done.Task;
    }

    private static void MainOnly()
    {
        if (!ReferenceEquals(Looper.MyLooper(), Looper.MainLooper))
            throw new InvalidOperationException("Native presentation requires the main thread.");
    }

    // Waits for the surface to be laid out and drawn: its dialog's window when it has a modal, otherwise the frame. A
    // dialog closed by the person or the application while this waits ends the wait, since it will not be drawn.
    private async Task NativeLayoutAsync(Epoch epoch, Surface surface, CancellationToken token)
    {
        MainOnly();
        View view = surface.Modal?.Dialog?.Window?.DecorView ?? epoch.Frame;
        ViewTreeObserver observer =
            view.ViewTreeObserver ?? throw new InvalidOperationException("No native layout observer.");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new LayoutAcknowledgement(view, done);
        // A view not yet in a window has a floating observer, merged into the window's once it attaches: stopping
        // then removes the listener from the observer the view has by then.
        Action stop = () => listener.Stop(observer, view);
        epoch.StopLayout = stop;
        Exception? pendingFailure = null;
        try
        {
            observer.AddOnPreDrawListener(listener);
            view.RequestLayout();
            view.Invalidate();
            if (surface.Modal?.ClosedByOthersOrNotShowing != true)
                await (surface.Modal is { } modal
                        ? Task.WhenAny(done.Task, modal.ClosedByOthers.Task)
                        : done.Task)
                    .WaitAsync(token);
        }
        catch (Exception failure)
        {
            pendingFailure = failure;
            throw;
        }
        finally
        {
            if (ReferenceEquals(epoch.StopLayout, stop))
                epoch.StopLayout = null;
            try
            {
                stop();
            }
            catch (Exception cleanup)
            {
                RememberUnsafeCleanup(cleanup);
                if (pendingFailure is not null)
                    throw new AggregateException(
                        "Native layout and listener retirement failed.",
                        pendingFailure,
                        cleanup);
                throw;
            }
        }
    }

    private void ReleaseDifference(Surface retired, Surface retained)
    {
        try
        {
            var failures = new List<Exception>();
            foreach (NativePage page in new[] { retired.Modal, retired.Top, retired.Root }.OfType<NativePage>())
                if (!ReferenceEquals(page, retained.Modal)
                    && !ReferenceEquals(page, retained.Top)
                    && !ReferenceEquals(page, retained.Root))
                    try
                    {
                        page.Release();
                    }
                    catch (Exception failure)
                    {
                        failures.Add(failure);
                    }

            if (failures.Count != 0)
                throw new AggregateException("Native page retirement failed.", failures);
        }
        catch (Exception failure)
        {
            RememberUnsafeCleanup(failure);
            throw;
        }
    }

    private bool FirstDrawReady(Epoch epoch)
    {
        if (!IsAttached(epoch))
            return false;
        Surface target = epoch.Staged ?? epoch.Surface;
        return Current(target)
               && target.VisibleRoot?.View is { } root
               && epoch.Frame.ChildCount == 1
               && ReferenceEquals(epoch.Frame.GetChildAt(0), root)
               && root is { IsAttachedToWindow: true, IsLaidOut: true, Width: > 0, Height: > 0 }
               && ReferenceEquals(epoch.ShownModal, target.Modal)
               && (target.Modal is null
                   || target.Modal.Dialog is { IsShowing: true, Window.DecorView.IsAttachedToWindow: true });
    }

    private sealed record Surface(NativePage? Root, NativePage? Top, NativePage? Modal)
    {
        public NativePage? VisibleRoot => Top ?? Root;
        public static Surface Empty { get; } = new(null, null, null);

        /// <summary>This surface's pages, and <paramref name="other"/>'s where this one has none.</summary>
        public Surface With(Surface other) => new(Root ?? other.Root, Top ?? other.Top, Modal ?? other.Modal);

        /// <summary>The pages that are still current.</summary>
        public Surface Current() => new(
            Root is { IsCurrent: true } ? Root : null,
            Top is { IsCurrent: true } ? Top : null,
            Modal is { IsCurrent: true } ? Modal : null);
    }

    // A fresh frame must not draw empty while the operation that fills it is queued. Stop revokes captures before
    // removing the listener; JNI owns any already queued callback's peer lifetime.
    private sealed class FirstDrawGate(FrameLayout frame, Func<bool> ready)
        : Java.Lang.Object, ViewTreeObserver.IOnPreDrawListener
    {
        private FrameLayout? _frame = frame;
        private Func<bool>? _ready = ready;
        private ViewTreeObserver? _observer;
        private bool _released;

        public void Start()
        {
            _observer = _frame?.ViewTreeObserver
                        ?? throw new InvalidOperationException("No first-frame observer.");
            _observer.AddOnPreDrawListener(this);
        }

        public bool OnPreDraw()
        {
            if (_ready is not { } canDraw)
                return _released;
            if (!canDraw() || !ReferenceEquals(_ready, canDraw))
                return false;
            _released = true;
            _ready = null;
            return true;
        }

        /// <summary>Lets the window draw from now on, whatever the frame holds.</summary>
        public void Open()
        {
            if (_ready is null)
                return;
            _ready = null;
            _released = true;
            _frame?.Invalidate();
        }

        public void Stop()
        {
            FrameLayout? original = _frame;
            ViewTreeObserver? observer = _observer;
            _frame = null;
            _observer = null;
            _ready = null;
            _released = false;
            if (observer?.IsAlive == true)
                observer.RemoveOnPreDrawListener(this);
            else if (original?.ViewTreeObserver is { IsAlive: true } current)
                current.RemoveOnPreDrawListener(this);
        }
    }

    private sealed class Epoch(ComponentActivity activity)
    {
        public ComponentActivity Activity { get; } = activity;
        public FrameLayout Frame { get; } = new(activity);
        public CancellationTokenSource Stopped { get; } = new();
        public ActivityBack? Back { get; set; }
        public Surface Surface { get; set; } = Surface.Empty;
        public Surface? Staged { get; set; }

        /// <summary>Pages out of the window that are still current, kept for the next operation to reuse.</summary>
        public Surface Spare { get; set; } = Surface.Empty;
        public NativePage? ShownModal { get; set; }
        public bool Applying { get; set; }

        public Action? StopLayout { get; set; }
        public FirstDrawGate? FirstDraw { get; set; }
    }

    /// <summary>
    /// One page the presenter owns: its view or dialog, what it borrows, and the lease attachment it holds.
    /// </summary>
    private sealed class NativePage(
        RouteEntry entry,
        AndroidPagePlacement placement,
        AndroidPage page,
        RouteEntryLease? lease,
        RouteEntryLeaseAttachment? attachment,
        bool placeholder)
    {
        private int _released;
        public RouteEntry Entry { get; } = entry;
        public AndroidPagePlacement Placement { get; } = placement;
        public View? View { get; } = page.View;
        public ComponentDialog? Dialog { get; } = page.Dialog;

        /// <summary>The entry's lease, or null for an entry without one and for a placeholder.</summary>
        public RouteEntryLease? Lease { get; } = lease;

        public bool IsPlaceholder { get; } = placeholder;
        public CancellationToken PageRetirement { get; } = page.Retirement;
        public CancellationToken LeaseRetirement => Lease?.RetiredToken ?? CancellationToken.None;

        /// <summary>A placeholder borrows nothing, so it is current until the router drops its entry.</summary>
        public bool IsCurrent => Lease?.IsRetired != true
                                 && !PageRetirement.IsCancellationRequested
                                 && (page.IsCurrent?.Invoke() ?? true);

        public ModalBack? Back { get; set; }
        public EventHandler? Canceled { get; set; }
        public EventHandler? Dismissed { get; set; }
        public bool CancelSeen { get; set; }
        public bool PresenterDismissing { get; set; }

        /// <summary>Completed when something other than the presenter closes the dialog it last showed.</summary>
        public TaskCompletionSource ClosedByOthers { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the dialog the presenter last showed has been closed by something else.</summary>
        public bool ClosedByOthersOrNotShowing => Dialog is { IsShowing: false } && !PresenterDismissing;

        public void DismissByPresenter()
        {
            PresenterDismissing = true;
            Dialog?.Dismiss();
        }

        public bool SharesNativeWith(NativePage other) =>
            !ReferenceEquals(this, other)
            && ((View is not null && ReferenceEquals(View, other.View))
                || (Dialog is not null && ReferenceEquals(Dialog, other.Dialog)));

        /// <summary>Retires the page. Without <paramref name="native"/>, its view or dialog belongs to another page,
        /// which disposes it: this page only gives back what it borrows.</summary>
        public void Release(bool native = true)
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                ReleaseNative(native);
        }

        // The binding first, then out of the window, then a view is disposed; the loan and the lease go last, and only
        // if everything before them succeeded.
        private void ReleaseNative(bool native)
        {
            var failures = new List<Exception>();
            if (native && Dialog is { } dialog)
            {
                PresenterDismissing = true;
                // Java may still own a queued callback; interop GC collects its peer, never a forced dispose.
                try { Back?.Remove(); }
                catch (Exception failure) { failures.Add(failure); }

                Back = null;
                dialog.CancelEvent -= Canceled;
                dialog.DismissEvent -= Dismissed;
            }

            try { page.Binding?.Dispose(); }
            catch (Exception failure) { failures.Add(failure); }

            if (failures.Count == 0 && native)
                try
                {
                    if (View is { } view)
                    {
                        // Leave the parent first: .NET for Android frees the Java peer on Dispose, and a view still in
                        // a frame is called back (OnDetachedFromWindow) when the frame drops it.
                        if (view.Parent is ViewGroup parent)
                            parent.RemoveView(view);
                        if (view.IsAttachedToWindow)
                            throw new InvalidOperationException(
                                $"The view for entry '{Entry.Id}' did not physically detach.");
                        view.Dispose();
                    }
                    else if (Dialog is { } shown)
                    {
                        shown.Dismiss();
                        if (shown.IsShowing || shown.Window?.DecorView.IsAttachedToWindow == true)
                            throw new InvalidOperationException(
                                $"The dialog for entry '{Entry.Id}' did not physically detach.");
                        // Not disposed: Android may still deliver the dismiss and cancel messages it queued when the
                        // dialog closed, and a disposed peer of a dialog subclass cannot be activated again for them.
                        // Out of the window is what makes the loan safe to release; the collector frees the peer.
                    }
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }

            // Failed native retirement keeps the loan and the lease: what the page borrows may still be in use.
            if (failures.Count == 0)
                try
                {
                    page.Loan?.Dispose();
                    attachment?.Detach();
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }

            if (failures.Count != 0)
                throw new AndroidCleanupFailureException(
                    $"Native retirement of route entry '{Entry.Id}' failed.",
                    new AggregateException(failures));
        }
    }

    private sealed class ModalBack(Action back) : OnBackPressedCallback(true)
    {
        public override void HandleOnBackPressed() => back();
    }

    private sealed class ActivityBack(AndroidNavigationPresenter owner, Epoch epoch) : OnBackPressedCallback(false)
    {
        private AndroidNavigationPresenter? _owner = owner;
        private Epoch? _epoch = epoch;

        public override void HandleOnBackPressed()
        {
            if (_epoch is { } current)
                _owner?.BackIntent(current);
        }

        public void Stop()
        {
            Enabled = false;
            Remove();
            _owner = null;
            _epoch = null;
        }
    }

    // JNI may already have queued one callback. Stop clears captures; normal interop GC owns the peer.
    private sealed class LayoutAcknowledgement(View view, TaskCompletionSource done)
        : Java.Lang.Object, ViewTreeObserver.IOnPreDrawListener
    {
        private View? _view = view;
        private TaskCompletionSource? _done = done;
        private int _stopped;

        public bool OnPreDraw()
        {
            if (_view is { IsAttachedToWindow: true, IsLaidOut: true, IsShown: true, Width: > 0, Height: > 0 })
                _ = _done?.TrySetResult();
            return true;
        }

        public void Stop(ViewTreeObserver observer, View view)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            _view = null;
            _done = null;
            if (observer.IsAlive)
                observer.RemoveOnPreDrawListener(this);
            else if (view.ViewTreeObserver is { IsAlive: true } current)
                current.RemoveOnPreDrawListener(this);
        }
    }
}

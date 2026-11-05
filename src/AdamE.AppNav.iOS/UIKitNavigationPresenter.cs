using System.Runtime.ExceptionServices;
using AdamE.AppNav.Leases;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;
using CoreAnimation;

namespace AdamE.AppNav.iOS;

/// <summary>
/// An <see cref="INavigationPresenter"/> for one <see cref="UIWindow"/>: the window's root page, a page pushed over it on
/// a <see cref="UINavigationController"/> the presenter owns, and a declared modal over them, in the shape its
/// <see cref="UIKitNavigationTopology"/> declares.
/// </summary>
/// <remarks>
/// <para>
/// AppNav alone owns history. UIKit owns the stack's back button and back-swipes: a pop UIKit commits is a fact the
/// presenter reports through <see cref="IUIKitNavigationHost.RecordNativePopAsync"/>, never a request to refuse. A
/// modal's swipe-down is refused and becomes a Back request through <see cref="IUIKitNavigationHost.RequestBackAsync"/>,
/// so the router's Back policy decides.
/// </para>
/// <para>
/// Each operation resolves the plan into pages, changes native state, waits for a displayed frame and verifies the
/// result before it returns. A failed or canceled operation restores the accepted presentation, or clears the window
/// when that presentation is no longer current, before it throws, so the router never commits a state native does not
/// show. A page is disposed only after it has physically left the window, and what it borrows is released only after
/// that. If a page's native retirement fails, the presenter keeps that page's loan and lease attachment and fails each
/// later operation and its own disposal with that failure; pages whose retirement succeeded still release.
/// </para>
/// <para>Create it on the main thread. Dispose it alongside the router's disposal (the adapter contract's shutdown):
/// disposal clears the window and retires every page.</para>
/// </remarks>
public sealed class UIKitNavigationPresenter : INavigationPresenter, IAsyncDisposable
{
    private readonly UIWindow _window;
    private readonly UIKitNavigationTopology _topology;
    private readonly IUIKitPageFactory _pages;
    private readonly IUIKitNavigationHost _host;
    private readonly UIKitNavigationPresenterOptions _options;

    /// <summary>A presenter for <paramref name="window"/>.</summary>
    /// <param name="window">The window the presenter alone shows pages in. It reveals the window only once it has
    /// installed its first page.</param>
    /// <param name="topology">The shape every plan must have.</param>
    /// <param name="pages">Builds each entry's page.</param>
    /// <param name="host">Receives native events: Back requests, committed pops, refresh requests and recovered
    /// failures. <see cref="UIKitRouterNavigationHost"/> sends them to the router.</param>
    /// <param name="options">Leases, push transitions and placeholders.</param>
    public UIKitNavigationPresenter(
        UIWindow window,
        UIKitNavigationTopology topology,
        IUIKitPageFactory pages,
        IUIKitNavigationHost host,
        UIKitNavigationPresenterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(host);
        _window = window;
        _topology = topology;
        _pages = pages;
        _host = host;
        _options = options ?? new UIKitNavigationPresenterOptions();
    }

    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Surface _surface = new(null, null, null, null);
    private ModalDelegate? _modalDelegate;

    private StackDelegate? _stackDelegate;

    // The stack the presenter last set; anything else UIKit shows is a pop the person committed.
    private (UINavigationController Navigation, UIViewController[] Stack)? _lastSet;
    private PresentedPage? _reportedPop;
    private UIViewController? _nativeRoot;

    // The navigation stack of the root page the presenter last installed: the only navigation controller it ever
    // pushes onto, pops or collapses.
    private UINavigationController? _nativeStack;
    private UIViewController? _shownModal;
    private bool _applying;
    private bool _windowShown;
    private int _disposed;
    private readonly Lock _disposalGate = new();
    private Task? _disposal;
    private AggregateException? _retirementFailure;

    /// <summary>Not raised. A modal's swipe-down is a Back request, and a pop UIKit commits is reported through
    /// <see cref="IUIKitNavigationHost.RecordNativePopAsync"/>, which submits its own reconciliation.</summary>
    public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
    {
        add { }
        remove { }
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
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        CancellationToken admissionToken = admission.Token;
        await _serial.WaitAsync(admissionToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                Surface? attempted = null;
                try
                {
                    await ApplyOnceAsync(plan, context, admissionToken, resolved => attempted = resolved)
                        .ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (!admissionToken.IsCancellationRequested
                                                         && Volatile.Read(ref _retirementFailure) is null
                                                         && attempted is { } surface
                                                         && RetiredWhileShown(surface))
                {
                    // A lease retired after its page was built, while the router still shows its entry. The attempt
                    // has restored the accepted surface, or cleared native when that surface was no longer current (a
                    // shown page holding the same lease) or the attempt had been accepted; a read-only acknowledgment
                    // touched nothing. Apply the same plan again: the retired lease refuses to attach, so the entry
                    // gets its placeholder. A placeholder holds no lease, so this repeats at most once per leased page.
                    // The pages cannot be kept and swapped, because the failed attempt has already disposed them. A
                    // pop the person committed during an attempt that cleared native, the usual path here, is lost
                    // either way, and the router still shows that entry, so this shows its placeholder until AppNav
                    // drops it.
                }
            }
        }
        catch (Exception failure) when (Volatile.Read(ref _retirementFailure) is not null)
        {
            throw RetainUnsafeFailure(failure);
        }
        finally
        {
            _ = _serial.Release();
        }
    }

    // One attempt at the plan: resolve, mutate native, verify, and on failure roll back to the accepted surface.
    // resolved receives the attempt's target as soon as it is built, so ApplyAsync can tell why it was cancelled.
    private async Task ApplyOnceAsync(
        NavigationPlan plan,
        NavigationPresentationContext context,
        CancellationToken admissionToken,
        Action<Surface> resolved)
    {
        Surface? completed = null;
        bool unchangedAcceptedRoot = false;
        await OnMainAsync(async () =>
            {
                Surface previous = _surface;
                Surface? target = null;
                bool accepted = false;
                bool mutated = false;
                bool preservingAcceptedRoot = false;
                CancellationTokenSource? owned = null;
                _applying = true;
                try
                {
                    ThrowIfRetirementFailed();
                    admissionToken.ThrowIfCancellationRequested();
                    target = await ResolveAsync(plan.TargetState, previous, admissionToken);
                    resolved(target);
                    owned = CancellationTokenSource.CreateLinkedTokenSource(
                        [admissionToken, .. Retirements(target)]);
                    RequireCurrent(target);
                    if (await PersonPoppedAsync(owned.Token) && !StackMatches(target))
                    {
                        // A pop the person committed while this operation prepared wins. It is reported when this
                        // operation ends and AppNav records it then. An operation that would present the same pages
                        // again (a refresh, or a recovery re-presenting the same pushed page) stands aside for
                        // that record; any other fails before touching native. The popped page is not checked
                        // again: its record is already retiring it.
                        if (!SameScreens(previous, target))
                            throw new UIKitNativePopWonException();
                        preservingAcceptedRoot = true;
                        completed = target with
                        {
                            TopEntry = null,
                            Top = null
                        };
                        unchangedAcceptedRoot = true;
                        return;
                    }

                    if (CanAcknowledgeAcceptedRoot(plan, context, previous, target))
                    {
                        // Refresh does not change topology. Foreign SDK UI is not an AppNav modal to remove.
                        preservingAcceptedRoot = true;
                        VerifyAcceptedRoot(target);
                        await DisplayedFrameAsync(owned.Token);
                        await SettleStackAsync(owned.Token);
                        owned.Token.ThrowIfCancellationRequested();
                        RequireCurrent(target);
                        VerifyAcceptedRoot(target);
                        owned.Token.ThrowIfCancellationRequested();
                        completed = target;
                        unchangedAcceptedRoot = true;
                        return;
                    }

                    Preflight(target);
                    mutated = true;
                    await MutateAsync(target, owned.Token);
                    await DisplayedFrameAsync(owned.Token);
                    await SettleStackAsync(owned.Token);
                    owned.Token.ThrowIfCancellationRequested();
                    RequireCurrent(target);
                    Verify(target);
                    owned.Token.ThrowIfCancellationRequested();
                    RequireCurrent(target);
                    _surface = target;
                    accepted = true;
                    // Old artifacts cannot be rolled back after native disposal starts.
                    ReleaseDifference(previous, target);
                    owned.Token.ThrowIfCancellationRequested();
                    RequireCurrent(target);
                    completed = target;
                }
                catch (Exception failure)
                {
                    if (UIKitCleanupFailureException.IsIn(failure))
                        RememberUnsafeCleanup(failure);
                    // Nothing was mutated or newly borrowed on this path. Replacement/shutdown owns retirement.
                    if (preservingAcceptedRoot)
                        throw;
                    if (accepted && target is not null)
                    {
                        var failures = new List<Exception> { failure };
                        try
                        {
                            ClearNative();
                        }
                        catch (Exception cleanup)
                        {
                            failures.Add(cleanup);
                        }

                        try
                        {
                            ReleaseDifference(target, new Surface(null, null, null, null));
                        }
                        catch (Exception cleanup)
                        {
                            failures.Add(cleanup);
                        }

                        _surface = new Surface(null, null, null, null);
                        if (failures.Count > 1)
                            throw new AggregateException(
                                "Accepted native presentation could not be safely retired.",
                                failures);
                        throw;
                    }

                    var rollbackFailures = new List<Exception> { failure };
                    try
                    {
                        if (!mutated && Current(previous))
                        {
                            // A foreign still-current provider surface was never ours to mutate or roll back.
                        }
                        else if (Volatile.Read(ref _disposed) == 0 && Current(previous))
                        {
                            // The failed target/caller does not own the accepted frame's compensation lifetime.
                            using var rollback = CancellationTokenSource.CreateLinkedTokenSource(
                                [_shutdown.Token, .. Retirements(previous)]);
                            RequireCurrent(previous);
                            // An animated push or pop the failed target started must end before restoring.
                            if (_nativeStack is { } moving)
                                await SettleTransitionAsync(moving, rollback.Token);
                            await MutateAsync(previous, rollback.Token);
                            rollback.Token.ThrowIfCancellationRequested();
                            RequireCurrent(previous);
                            Verify(previous);
                        }
                        else
                            ClearNative(); // Never resurrect a retired native surface.
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested
                                                             || !Current(previous))
                    {
                        try
                        {
                            ClearNative();
                            if (previous.Root?.IsCurrent == true)
                                _ = RepairAsync(previous.Root);
                        }
                        catch (Exception cleanup)
                        {
                            rollbackFailures.Add(cleanup);
                        }
                    }
                    catch (Exception rollback)
                    {
                        rollbackFailures.Add(rollback);
                        try
                        {
                            ClearNative();
                        }
                        catch (Exception cleanup)
                        {
                            rollbackFailures.Add(cleanup);
                        }
                    }

                    if (target is not null)
                        try
                        {
                            ReleaseDifference(target, previous);
                        }
                        catch (Exception cleanup)
                        {
                            rollbackFailures.Add(cleanup);
                        }

                    if (rollbackFailures.Count > 1)
                        throw new AggregateException(
                            "Native iOS presentation and cleanup failed.",
                            rollbackFailures);
                    throw;
                }
                finally
                {
                    owned?.Dispose();
                    _applying = false;
                    ReportCommittedPop();
                }
            })
            .ConfigureAwait(false);
        try
        {
            admissionToken.ThrowIfCancellationRequested();
            if (completed is not null)
                RequireCurrent(completed);
        }
        catch (Exception failure)
        {
            if (unchangedAcceptedRoot)
                throw; // A canceled read-only acknowledgment must not dismiss the original SDK hierarchy.
            await OnMainAsync(() =>
                {
                    var failures = new List<Exception> { failure };
                    try
                    {
                        ClearNative();
                    }
                    catch (Exception cleanup)
                    {
                        failures.Add(cleanup);
                    }

                    if (completed is not null)
                        try
                        {
                            ReleaseDifference(completed, new Surface(null, null, null, null));
                        }
                        catch (Exception cleanup)
                        {
                            failures.Add(cleanup);
                        }

                    _surface = new Surface(null, null, null, null);
                    if (failures.Count > 1)
                        throw new AggregateException("Canceled native presentation cleanup failed.", failures);
                    return Task.CompletedTask;
                })
                .ConfigureAwait(false);
            throw;
        }
    }

    private static CancellationToken[] Retirements(Surface surface) =>
    [
        .. new[] { surface.Root, surface.Modal, surface.Top }
            .OfType<PresentedPage>()
            .SelectMany(static page => new[] { page.LeaseRetirement, page.PageRetirement })
    ];

    // Whether a page of this attempt holds a lease that retired while the router still shows its entry.
    private static bool RetiredWhileShown(Surface surface) =>
        RetiredWhileShown(surface.Root) || RetiredWhileShown(surface.Top) || RetiredWhileShown(surface.Modal);

    private static bool RetiredWhileShown(PresentedPage? page) =>
        page?.Lease is { IsRetired: true, IsCommitted: true };

    private async ValueTask<Surface> ResolveAsync(NavigationState state, Surface previous, CancellationToken token)
    {
        // The declared shape is checked before anything is built.
        if (_topology.Validate(state) is not var (stack, modal, owner))
            return new Surface(null, null, null, null);
        PresentedPage root = await ReuseAsync(
            previous.RootEntry,
            previous.Root,
            stack.Entries[0],
            UIKitPagePlacement.Root,
            null,
            token);
        PresentedPage? sheet = null;
        PresentedPage? top = null;
        try
        {
            if (stack.Entries.Count == 2)
            {
                if (root.Stack is null)
                    throw new InvalidOperationException(
                        "A pushed page needs a root page that is a navigation stack (UIKitPage.IsNavigationStack).");
                top = await ReuseAsync(
                    previous.TopEntry,
                    previous.Top,
                    stack.Entries[1],
                    UIKitPagePlacement.Pushed,
                    null,
                    token);
                if (ReferenceEquals(root.Controller, top.Controller)
                    || ReferenceEquals(root.OriginalContent, top.Controller))
                    throw new InvalidOperationException("The root page and a pushed page must own different controllers.");
            }

            sheet = modal is null
                ? null
                : await ReuseAsync(
                    previous.ModalEntry,
                    previous.Modal,
                    modal.RouteEntry,
                    UIKitPagePlacement.Modal,
                    owner,
                    token);
            if (top is not null && ReferenceEquals(top.Controller, sheet?.Controller))
                throw new InvalidOperationException("A pushed page and a modal must own different controllers.");
            if (ReferenceEquals(root.Controller, sheet?.Controller)
                || (sheet is not null && ReferenceEquals(root.OriginalContent, sheet.Controller)))
                throw new InvalidOperationException("The root page and a modal must own different controllers.");
            return new Surface(
                stack.Entries[0],
                root,
                modal?.RouteEntry,
                sheet,
                stack.Entries.Count == 2 ? stack.Entries[1] : null,
                top);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            PresentedPage?[] all = [root, top, sheet, previous.Root, previous.Top, previous.Modal];
            foreach (PresentedPage? candidate in new[] { top, sheet, root })
                if (candidate is not null
                    && !ReferenceEquals(candidate, previous.Root)
                    && !ReferenceEquals(candidate, previous.Top)
                    && !ReferenceEquals(candidate, previous.Modal))
                    try
                    {
                        // A controller another page also shows is that page's to dispose: this one only gives back
                        // what it borrows.
                        if (SharesController(candidate, all))
                            ReleaseOnly(candidate);
                        else
                            Retire(candidate);
                    }
                    catch (Exception cleanup)
                    {
                        failures.Add(cleanup);
                    }

            if (failures.Count > 1)
                throw new AggregateException("Native materialization and cleanup failed.", failures);
            throw;
        }
    }

    private static bool SharesController(PresentedPage page, PresentedPage?[] pages) =>
        pages.Any(other => other is not null
                           && !ReferenceEquals(other, page)
                           && (ReferenceEquals(other.Controller, page.Controller)
                               || ReferenceEquals(other.OriginalContent, page.Controller)
                               || ReferenceEquals(other.Controller, page.OriginalContent)));

    private void ReleaseOnly(PresentedPage page)
    {
        try
        {
            page.ReleaseLoan();
        }
        catch (Exception failure)
        {
            RememberUnsafeCleanup(failure);
            throw;
        }
    }

    private ValueTask<PresentedPage> ReuseAsync(
        RouteEntry? oldEntry,
        PresentedPage? oldPage,
        RouteEntry entry,
        UIKitPagePlacement placement,
        string? owner,
        CancellationToken token) =>
        oldEntry?.Id == entry.Id && Equals(oldEntry.Route, entry.Route) && oldPage?.IsCurrent == true
            ? ValueTask.FromResult(oldPage)
            : CreatePageAsync(entry, placement, owner, token);

    /// <summary>
    /// A leased entry's page is attached before its controller exists, and detached only after the presenter has taken
    /// that controller out of the window and disposed it, through the page's loan. A retired lease whose entry the router
    /// still shows gets a placeholder that borrows nothing.
    /// </summary>
    private async ValueTask<PresentedPage> CreatePageAsync(
        RouteEntry entry,
        UIKitPagePlacement placement,
        string? owner,
        CancellationToken token)
    {
        if (_options.EntryLeases is not { } leases)
            return await BuildAsync(entry, placement, owner, null, null, token);
        // With a registry, every entry is leased: an entry without one would get no lifetime guarantee, silently.
        if (!leases.TryGet(entry.Id, out RouteEntryLease? found) || found is not { } lease)
            throw new InvalidOperationException(
                $"Route entry '{entry.Id}' has no lease in EntryLeases: open one before navigating to it.");
        // TryAttach is the check: the application may retire the lease at any time, so an earlier IsRetired could be
        // stale. A retired lease may already have released what the page would borrow, so build nothing from it.
        if (!lease.TryAttach(out RouteEntryLeaseAttachment? attachment))
            return lease.IsCommitted
                ? Placeholder(entry, placement, owner)
                : throw new OperationCanceledException(
                    $"The lease of route entry '{entry.Id}' retired before its page was shown.");
        try
        {
            return await BuildAsync(entry, placement, owner, lease, attachment, token);
        }
        catch (OperationCanceledException cancelled) when (!token.IsCancellationRequested
                                                           && lease is { IsRetired: true, IsCommitted: true }
                                                           && !UIKitCleanupFailureException.IsIn(cancelled))
        {
            // The lease retired during the build while the router still shows its entry. Nothing native survived the
            // canceled build, so give the lease back and show the placeholder instead.
            attachment.Detach();
            return Placeholder(entry, placement, owner);
        }
        catch (Exception failure) when (!UIKitCleanupFailureException.IsIn(failure))
        {
            // Nothing native survived the failed build, so nothing still uses the lease.
            attachment.Detach();
            throw;
        }
    }

    private async ValueTask<PresentedPage> BuildAsync(
        RouteEntry entry,
        UIKitPagePlacement placement,
        string? owner,
        RouteEntryLease? lease,
        RouteEntryLeaseAttachment? attachment,
        CancellationToken token)
    {
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(
            token,
            lease?.RetiredToken ?? CancellationToken.None);
        // A stale candidate is still handed back as a page, so a later retirement disposes it and a failed disposal
        // stays sticky; the page's own retirement is checked once the presenter owns it.
        owned.Token.ThrowIfCancellationRequested();
        UIKitPage page = await _pages.CreatePageAsync(new UIKitPageRequest(entry, placement, lease), owned.Token)
                         ?? throw new InvalidOperationException($"The page factory returned no page for '{entry.Id}'.");
        if (page.Controller is null)
            throw new InvalidOperationException($"The page factory returned no controller for '{entry.Id}'.");
        if (Misuse(page, entry, placement) is { } misuse)
        {
            // The presenter owns the returned page, so it retires it before rejecting it.
            RetireRejected(new PresentedPage(page.Controller, page.Loan, null, null, null, default, null, false), misuse);
            throw misuse;
        }

        PresentedPage presented;
        try
        {
            presented = new PresentedPage(
                page.Controller,
                page.Loan,
                lease,
                attachment,
                page.IsCurrent,
                page.Retirement,
                owner,
                page.IsNavigationStack);
        }
        catch (InvalidOperationException malformed)
        {
            RetireRejected(
                new PresentedPage(page.Controller, page.Loan, null, null, null, default, null, false),
                malformed);
            throw;
        }

        return presented;
    }

    // A page UIKit would refuse, or one whose delegates the presenter needs and the application already set, is
    // rejected before anything native changes.
    private static InvalidOperationException? Misuse(UIKitPage page, RouteEntry entry, UIKitPagePlacement placement)
    {
        if (page.IsNavigationStack && placement is not UIKitPagePlacement.Root)
            return new InvalidOperationException(
                $"Only a root page can be a navigation stack; '{entry.Id}' is placed {placement}.");
        if (placement is UIKitPagePlacement.Pushed && page.Controller is UINavigationController)
            return new InvalidOperationException(
                $"Pushed page '{entry.Id}' is a navigation controller, which UIKit cannot push onto another.");
        if (page.IsNavigationStack && page.Controller is UINavigationController { WeakDelegate: not null })
            return new InvalidOperationException(
                $"Root page '{entry.Id}' has a navigation controller delegate: the presenter is that delegate.");
        if (placement is UIKitPagePlacement.Modal
            && page.Controller.PresentationController is { WeakDelegate: not null })
            return new InvalidOperationException(
                $"Modal page '{entry.Id}' has a presentation controller delegate: the presenter is that delegate.");
        return null;
    }

    // A page the presenter rejected before showing it: retired as any other, and a failed retirement stays sticky.
    private void RetireRejected(PresentedPage rejected, Exception misuse)
    {
        try
        {
            Retire(rejected);
        }
        catch (Exception cleanup)
        {
            throw new UIKitCleanupFailureException("A rejected page could not be retired.", misuse, cleanup);
        }
    }

    private PresentedPage Placeholder(RouteEntry entry, UIKitPagePlacement placement, string? owner)
    {
        UIViewController blank = _options.CreatePlaceholder?.Invoke(entry, placement)
                                 ?? new UIViewController { View = new UIView { BackgroundColor = UIColor.SystemBackground } };
        UIViewController controller = placement switch
        {
            // A page may be pushed over the root while the router still shows it, so the root keeps a stack: the
            // placeholder's own, when it is one, or one around it.
            UIKitPagePlacement.Root when blank is UINavigationController { ViewControllers.Length: 1 } own => own,
            UIKitPagePlacement.Root => new UINavigationController(blank),
            UIKitPagePlacement.Pushed when blank is UINavigationController => throw new InvalidOperationException(
                $"The placeholder for pushed entry '{entry.Id}' is a navigation controller, which UIKit cannot push."),
            UIKitPagePlacement.Modal when _options.CreatePlaceholder is null =>
                new UINavigationController(blank) { ModalPresentationStyle = UIModalPresentationStyle.PageSheet },
            _ => blank
        };
        return new PresentedPage(
            controller,
            null,
            null,
            null,
            null,
            default,
            owner,
            stack: placement is UIKitPagePlacement.Root,
            placeholder: true);
    }

    private bool CanAcknowledgeAcceptedRoot(
        NavigationPlan plan,
        NavigationPresentationContext context,
        Surface previous,
        Surface target) =>
        plan.Kind == NavigationPlanKind.Refresh
        && ReferenceEquals(plan.TargetState, context.CurrentState)
        && previous.Modal is null
        && target.Modal is null
        && ReferenceEquals(previous.Top, target.Top)
        && previous.TopEntry?.Id == target.TopEntry?.Id
        && Equals(previous.TopEntry?.Route, target.TopEntry?.Route)
        && _shownModal is null
        && previous.Root is { IsCurrent: true } original
        && ReferenceEquals(target.Root, original)
        && previous.RootEntry?.Id == target.RootEntry?.Id
        && Equals(previous.RootEntry?.Route, target.RootEntry?.Route)
        && ReferenceEquals(_window.RootViewController, original.Controller)
        && ReferenceEquals(_nativeRoot, original.Controller)
        && RootHierarchyMounted(original.Controller)
        // A native pop AppNav did not accept leaves the stack short; that refresh must push the page back.
        && StackMatches(target);

    private bool RootHierarchyMounted(UIViewController root) =>
        (root.IsViewLoaded && ReferenceEquals(root.View?.Window, _window))
        || PresentedChildren(root).Any(child => child.IsViewLoaded && ReferenceEquals(child.View?.Window, _window));

    private void VerifyAcceptedRoot(Surface target)
    {
        MainOnly();
        if (target.Root is not { IsCurrent: true } original
            || target.Modal is not null
            || _shownModal is not null
            || !ReferenceEquals(_window.RootViewController, original.Controller)
            || !ReferenceEquals(_nativeRoot, original.Controller)
            || !RootHierarchyMounted(original.Controller))
            throw new InvalidOperationException("The original accepted native root no longer owns this window.");
        // Full-screen SDK children can temporarily cover/remove the root's view while its controller still owns it.
        RequireOwnWindow(PresentedChildren(original.Controller));
        if (target.Top is { } top)
            RequireOwnWindow(PresentedChildren(top.Controller));
        if (!PoppedSinceSet(target))
            VerifyStack(target);
    }

    private void Preflight(Surface target)
    {
        MainOnly();
        UIViewController? actualRoot = _window.RootViewController;
        if (actualRoot is not null && !ReferenceEquals(actualRoot, _nativeRoot))
            throw new InvalidOperationException("The window was reassigned outside its native presenter.");
        if (_surface.Top is { } previousTop && !ReferenceEquals(previousTop, target.Top))
        {
            UIViewController[] children = PresentedChildren(previousTop.Controller);
            RequireOwnWindow(children);
            if (children.Length != 0 && previousTop.IsCurrent && Volatile.Read(ref _disposed) == 0)
                throw new InvalidOperationException(
                    "A still-current foreign presentation over a pushed page owns this surface.");
        }

        if (actualRoot?.PresentedViewController is { } foreign && !ReferenceEquals(foreign, _shownModal))
        {
            bool retiringOriginal = _surface.Root is { IsCurrent: false } previous
                                    && ReferenceEquals(previous.Controller, actualRoot)
                                    && !ReferenceEquals(target.Root?.Controller, actualRoot);
            bool retiringTop = _surface.Top is { IsCurrent: false } oldTop
                               && !ReferenceEquals(oldTop, target.Top)
                               && PresentedChildren(oldTop.Controller).Any(child => ReferenceEquals(child, foreign));
            if (!retiringOriginal && !retiringTop && Volatile.Read(ref _disposed) == 0)
                throw new InvalidOperationException("A still-current foreign native presentation owns this surface.");
            RequireOwnWindow(PresentedChildren(actualRoot));
        }
    }

    private async Task MutateAsync(Surface target, CancellationToken token)
    {
        MainOnly();
        UIViewController? actualRoot = _window.RootViewController;
        UIViewController? desiredRoot = target.Root?.Controller;
        UIViewController? desiredModal = target.Modal?.Controller;
        if (actualRoot?.PresentedViewController is { } presented
            && (!ReferenceEquals(actualRoot, desiredRoot) || !ReferenceEquals(presented, desiredModal)))
        {
            UIViewController[] originalChildren = PresentedChildren(actualRoot);
            await DismissAsync(actualRoot, token);
            RequireDetached(originalChildren);
            _shownModal = null;
        }

        if (_surface.Top is { } previousTop && !ReferenceEquals(previousTop, target.Top))
        {
            UIViewController[] topChildren = PresentedChildren(previousTop.Controller);
            if (topChildren.Length != 0)
            {
                if (previousTop.IsCurrent && Volatile.Read(ref _disposed) == 0)
                    throw new InvalidOperationException(
                        "A still-current pushed page's presentation remains owned by its SDK.");
                RequireOwnWindow(topChildren);
                await DismissAsync(previousTop.Controller, token);
                RequireDetached(topChildren);
            }
        }

        token.ThrowIfCancellationRequested();
        // Remove the old pushed page from its original stack before that native artifact can be retired.
        if (_nativeStack is { } oldNavigation
            && ReferenceEquals(actualRoot, oldNavigation)
            && !ReferenceEquals(actualRoot, desiredRoot))
            CollapseStack(oldNavigation);
        // Opening or leaving a pushed page on the visible stack animates like any push or pop; anything else is
        // instant.
        bool animated = _windowShown
                        && desiredModal is null
                        && actualRoot?.PresentedViewController is null
                        && ReferenceEquals(actualRoot, desiredRoot);
        if (SetStack(target, animated) is { } moving)
            await SettleTransitionAsync(moving, token);
        _window.RootViewController = desiredRoot;
        _nativeRoot = desiredRoot;
        _nativeStack = target.Root?.Stack;
        if (!_windowShown && desiredRoot is not null)
        {
            // A visible frame is needed for presentation acknowledgment; never reveal an empty window first.
            _window.MakeKeyAndVisible();
            _windowShown = true;
        }

        if (desiredModal is not null
            && desiredRoot is not null
            && !ReferenceEquals(desiredRoot.PresentedViewController, desiredModal))
        {
            desiredModal.ModalInPresentation = true;
            _shownModal = desiredModal;
            await NativeCompletionAsync(
                completion => desiredRoot.PresentViewController(desiredModal, false, completion),
                token);
        }

        _modalDelegate?.Stop();
        _modalDelegate = null;
        if (desiredModal?.PresentationController is { } presentation)
        {
            desiredModal.ModalInPresentation = true;
            _modalDelegate = new ModalDelegate(
                this,
                target.Modal
                ?? throw new InvalidOperationException("Missing modal page ownership."));
            presentation.Delegate = _modalDelegate;
        }
    }

    // Returns the stack when it started an animated push or pop of a page over the root, for the caller to await.
    private UINavigationController? SetStack(Surface target, bool animated = false)
    {
        if (target.Root?.Stack is not { } navigation)
            return null;
        UIViewController originalRoot = target.Root.OriginalContent
                                        ?? throw new InvalidOperationException(
                                            "The root page's navigation stack lost its original controller.");
        UIViewController[] desired = target.Top is { } top
            ? [originalRoot, top.Controller]
            : [originalRoot];
        UIViewController[] current = navigation.ViewControllers ?? [];
        bool changed = !current.SequenceEqual(desired);
        // Only the pushed page itself may animate: the same original root page stays at the bottom.
        animated &= changed
                    && current.Length >= 1
                    && ReferenceEquals(current[0], originalRoot)
                    && Math.Abs(current.Length - desired.Length) == 1;
        if (changed
            && target is { Top: { } pushed, TopEntry: { } entry }
            && !current.Any(shown => ReferenceEquals(shown, pushed.Controller)))
            _options.ConfiguringPush?.Invoke(new UIKitPushContext(originalRoot, pushed.Controller, entry, animated));
        if (changed)
            navigation.SetViewControllers(desired, animated);
        _lastSet = (navigation, desired);
        _reportedPop = null;
        // A pushed page sits on the root page's stack, and its own navigation item carries its chrome under that bar.
        navigation.SetNavigationBarHidden(target.Root.OriginalNavigationBarHidden, false);
        _stackDelegate ??= new StackDelegate(new WeakReference<UIKitNavigationPresenter>(this));
        if (!ReferenceEquals(navigation.Delegate, _stackDelegate))
            navigation.Delegate = _stackDelegate;
        return animated ? navigation : null;
    }

    // UIKit owns the stack's back button and back-swipes, their gesture delegates and Enabled: the presenter touches none
    // of them. It waits for a transition only at the two moments it reads the stack, and only for that one.
    private static async Task SettleTransitionAsync(UINavigationController navigation, CancellationToken token)
    {
        if (navigation.GetTransitionCoordinator() is not { } coordinator)
            return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!coordinator.AnimateAlongsideTransition(static _ => { }, _ => done.TrySetResult()))
            return;
        await done.Task.WaitAsync(token);
    }

    private Task SettleStackAsync(CancellationToken token) =>
        _nativeStack is { } navigation
            ? SettleTransitionAsync(navigation, token)
            : Task.CompletedTask;

    // Whether the stack differs from what the presenter last set: a pop the person committed. A swipe still being
    // dragged ends first; a cancelled one leaves the stack as it was.
    private async Task<bool> PersonPoppedAsync(CancellationToken token)
    {
        if (_lastSet is not { } last || !ReferenceEquals(_nativeRoot, last.Navigation))
            return false;
        await SettleTransitionAsync(last.Navigation, token);
        return !(last.Navigation.ViewControllers ?? []).SequenceEqual(last.Stack);
    }

    // The person popped the page this operation just pushed: the operation stands, and the pop is reported when it
    // ends.
    private bool PoppedSinceSet(Surface target) =>
        target is { Top: { } top, Root: { Stack: { } navigation, OriginalContent: { } home } }
        && _lastSet is { Stack: [_, var shown] } last
        && ReferenceEquals(last.Navigation, navigation)
        && ReferenceEquals(shown, top.Controller)
        // A swipe still being dragged counts too: if it is cancelled, UIKit restores the page and reports nothing.
        && navigation.ViewControllers is [var only]
        && ReferenceEquals(only, home);

    // Reports, once, a pop the person committed: the pushed page the presenter last set on the stack has left it.
    // UIKit has already removed it, so this is a fact for AppNav to record, never a Back to refuse. Runs
    // when UIKit has shown a controller (its transition is ending, so its coordinator may still exist) and when an
    // operation ends; during an operation it waits for that end. At an operation's end a swipe still being dragged may
    // yet be cancelled, so it is left to UIKit's own callback.
    private void ReportCommittedPop(bool shownByUiKit = false)
    {
        if (_applying
            || Volatile.Read(ref _disposed) != 0
            || _lastSet is not { Stack: [_, var shown] } last
            || !ReferenceEquals(_nativeRoot, last.Navigation)
            || (!shownByUiKit && last.Navigation.GetTransitionCoordinator() is not null)
            || (last.Navigation.ViewControllers ?? []).Any(child => ReferenceEquals(child, shown))
            || _surface is not { Top: { } top, TopEntry: { } entry }
            || !ReferenceEquals(top.Controller, shown)
            || ReferenceEquals(_reportedPop, top))
            return;
        _reportedPop = top;
        _ = PoppedAsync(top, entry.Id);
    }

    private async Task PoppedAsync(PresentedPage top, string entryId)
    {
        try
        {
            await DetachedAsync(() => _host.RecordNativePopAsync(entryId, _shutdown.Token));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || !top.IsCurrent) { }
        catch (Exception failure)
        {
            _host.ReportRecoveredFailure(failure);
        }
    }

    private static void CollapseStack(UINavigationController navigation)
    {
        if (navigation.ViewControllers is { Length: > 1 } children)
            navigation.SetViewControllers([children[0]], false);
    }

    private static bool SameScreens(Surface previous, Surface target) =>
        ReferenceEquals(previous.Root, target.Root)
        && ReferenceEquals(previous.Top, target.Top)
        && previous.Modal is null
        && target.Modal is null;

    private static bool StackMatches(Surface target) =>
        target.Root?.Stack is not { } navigation
        || target.Root.OriginalContent is not { } originalRoot
        || (navigation.ViewControllers ?? []).SequenceEqual(
            target.Top is { } top ? [originalRoot, top.Controller] : [originalRoot]);

    private void VerifyStack(Surface target)
    {
        if (target.Root?.Stack is not { } navigation)
        {
            if (target.Top is not null)
                throw new InvalidOperationException("A pushed page has no root navigation stack.");
            return;
        }

        UIViewController originalRoot = target.Root.OriginalContent
                                        ?? throw new InvalidOperationException(
                                            "The root page's navigation stack lost its original controller.");
        UIViewController[] expected = target.Top is { } top
            ? [originalRoot, top.Controller]
            : [originalRoot];
        if (!(navigation.ViewControllers ?? []).SequenceEqual(expected)
            || (target.Top is { } mounted
                && !RootHierarchyMounted(mounted.Controller)
                && !ModalCoversStack(target)))
            throw new InvalidOperationException("The native stack differs from AppNav's accepted target.");
    }

    // A modal presented by the root may take the pages under it out of the window: a full-screen one, or one presented
    // over the entry that owns it.
    private bool ModalCoversStack(Surface target) =>
        target is { Modal: { } modal }
        && (modal.Controller.ModalPresentationStyle is UIModalPresentationStyle.FullScreen
            || (modal.OwnerEntryId is { } owner && owner == (target.TopEntry ?? target.RootEntry)?.Id))
        && ReferenceEquals(target.Root?.Controller.PresentedViewController, modal.Controller)
        && modal.Controller.IsViewLoaded
        && ReferenceEquals(modal.Controller.View?.Window, _window);

    private static Task DismissAsync(UIViewController controller, CancellationToken token) =>
        NativeCompletionAsync(completion => controller.DismissViewController(false, completion), token);

    private static async Task NativeCompletionAsync(Action<Action> begin, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new NativeCompletion(done);
        try
        {
            token.ThrowIfCancellationRequested();
            begin(completion.Complete);
            await done.Task.WaitAsync(token);
        }
        finally
        {
            completion.Stop();
        }
    }

    private void Verify(Surface target)
    {
        MainOnly();
        if (!ReferenceEquals(_window.RootViewController, target.Root?.Controller)
            || !ReferenceEquals(target.Root?.Controller.PresentedViewController, target.Modal?.Controller)
            || (target.Modal?.Controller.PresentingViewController is { } presenting
                && !ReferenceEquals(presenting, target.Root?.Controller)))
            throw new InvalidOperationException("The native iOS hierarchy does not match the accepted target.");
        if ((target.Root is not null
             && !ReferenceEquals(target.Root.Controller.View?.Window, _window)
             && !ModalCoversStack(target))
            || (target.Modal is not null && !ReferenceEquals(target.Modal.Controller.View?.Window, _window)))
            throw new InvalidOperationException("The accepted native iOS surface is not mounted in its window.");
        if (!PoppedSinceSet(target))
            VerifyStack(target);
    }

    private static bool Current(Surface target) =>
        (target.Root?.IsCurrent ?? true) && (target.Modal?.IsCurrent ?? true) && (target.Top?.IsCurrent ?? true);

    private static void RequireCurrent(Surface target)
    {
        if (!Current(target))
            throw new OperationCanceledException("The originally resolved page retired during presentation.");
    }

    private async Task DisplayedFrameAsync(CancellationToken token)
    {
        MainOnly();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgment = new DisplayedFrameAcknowledgement(_window, done);
        var link = CADisplayLink.Create(acknowledgment.Tick);
        acknowledgment.Attach(link);
        Exception? pendingFailure = null;
        try
        {
            link.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
            await done.Task.WaitAsync(token);
        }
        catch (Exception failure)
        {
            pendingFailure = failure;
            throw;
        }
        finally
        {
            acknowledgment.Stop();
            try
            {
                link.Invalidate();
            }
            catch (Exception cleanup)
            {
                RememberUnsafeCleanup(cleanup);
                if (pendingFailure is not null)
                    throw new AggregateException(
                        "Native frame and callback retirement failed.",
                        pendingFailure,
                        cleanup);
                throw;
            }
        }
    }

    private void BackIntent(PresentedPage original)
    {
        if (!_applying
            && Volatile.Read(ref _disposed) == 0
            && (ReferenceEquals(_surface.Modal, original) || ReferenceEquals(_surface.Top, original))
            && original.IsCurrent)
            _ = BackAsync(original);
    }

    private async Task BackAsync(PresentedPage original)
    {
        try
        {
            await DetachedAsync(() => _host.RequestBackAsync(_shutdown.Token));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || !original.IsCurrent) { }
        catch (Exception failure)
        {
            _host.ReportRecoveredFailure(failure);
        }
    }

    private void UnexpectedDismissal(PresentedPage original)
    {
        if (!_applying
            && Volatile.Read(ref _disposed) == 0
            && ReferenceEquals(_surface.Modal, original)
            && original.IsCurrent)
            _ = RefreshAsync(original, "Unexpected native dismissal requires accepted-state refresh.");
    }

    private async Task RefreshAsync(PresentedPage original, string reason)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _host.ReportRecoveredFailure(new InvalidOperationException(reason));
            await DetachedAsync(() => _host.RequestRefreshAsync(_shutdown.Token));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || !original.IsCurrent) { }
        catch (Exception failure)
        {
            _host.ReportRecoveredFailure(failure);
        }
    }

    private async Task RepairAsync(PresentedPage original)
    {
        try
        {
            // The callback detaches AppNav execution context and serializes behind the failed operation.
            if (original.IsCurrent && Volatile.Read(ref _disposed) == 0)
                await DetachedAsync(() => _host.RequestRefreshAsync(_shutdown.Token));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || !original.IsCurrent) { }
        catch (Exception failure)
        {
            _host.ReportRecoveredFailure(failure);
        }
    }

    // A host callback runs on the thread pool with no execution context from the operation that reported it: the
    // router's operation context flows through every await inside ApplyAsync, and a host that calls the router from it
    // would be refused as a reentrant operation, losing a pop or a repair.
    private static Task DetachedAsync(Func<Task> callback)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(callback);
    }

    private void ClearNative()
    {
        try
        {
            ClearNativeCore();
        }
        catch (Exception failure)
        {
            RememberUnsafeCleanup(failure);
            throw;
        }
    }

    private void ClearNativeCore()
    {
        MainOnly();
        _modalDelegate?.Stop();
        _modalDelegate = null;
        if (_nativeStack is { } stack && ReferenceEquals(stack.Delegate, _stackDelegate))
            stack.WeakDelegate = null;
        _lastSet = null;

        if (ReferenceEquals(_window.RootViewController, _nativeRoot) && _nativeRoot is { } original)
        {
            UIViewController[] topChildren = _surface.Top is { } top
                ? PresentedChildren(top.Controller)
                : [];
            RequireOwnWindow(topChildren);
            if (topChildren.Length != 0 && _surface.Top?.IsCurrent == true && Volatile.Read(ref _disposed) == 0)
                throw new InvalidOperationException(
                    "A still-current presentation over a pushed page cannot be cleared.");
            UIViewController[] children = PresentedChildren(original);
            bool terminalOrRetired = Volatile.Read(ref _disposed) != 0 || _surface.Root?.IsCurrent != true;
            if (children.Length != 0 && !ReferenceEquals(children[0], _shownModal) && !terminalOrRetired)
                throw new InvalidOperationException("A still-current foreign presentation remains owned by its SDK.");
            RequireOwnWindow(children);
            if (children.Length != 0)
                original.DismissViewController(false, null);
            // Terminal/canceled cleanup cannot wait forever for an SDK completion callback. Detach only this
            // original hierarchy's views; SDK-owned child controller objects are never disposed by this host.
            foreach (UIViewController child in children.Concat(topChildren).Distinct())
                if (child is { IsViewLoaded: true, View: { } view } && ReferenceEquals(view.Window, _window))
                    view.RemoveFromSuperview();
            _window.RootViewController = null;
            if (ReferenceEquals(original, _nativeStack) && _nativeStack is { } navigation)
                CollapseStack(navigation);
            if (original is { IsViewLoaded: true, View: { } rootView } && ReferenceEquals(rootView.Window, _window))
                rootView.RemoveFromSuperview();
            RequireDetached(children);
            RequireDetached(topChildren);
            if (original.IsViewLoaded && ReferenceEquals(original.View?.Window, _window))
                throw new InvalidOperationException("The original native root did not detach.");
        }

        _nativeRoot = null;
        _nativeStack = null;
        _shownModal = null;
    }

    private static UIViewController[] PresentedChildren(UIViewController original)
    {
        var children = new List<UIViewController>();
        for (UIViewController? child = original.PresentedViewController;
             child is not null;
             child = child.PresentedViewController)
            children.Add(child);
        return [.. children];
    }

    private void RequireOwnWindow(IEnumerable<UIViewController> children)
    {
        if (children.Any(child =>
                child is { IsViewLoaded: true, View.Window: { } attached } && !ReferenceEquals(attached, _window)))
            throw new InvalidOperationException("The original hierarchy contains UI owned by another window.");
    }

    private void RequireDetached(IEnumerable<UIViewController> children)
    {
        if (children.Any(child => child.IsViewLoaded && ReferenceEquals(child.View?.Window, _window)))
            throw new InvalidOperationException("An original presented child remains attached to the window.");
    }

    /// <summary>Clears the window and retires every page, then completes. Throws if native retirement failed, now
    /// or earlier; leases and loans are then kept.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_disposalGate)
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
            // Synchronous cancellation revokes admitted callbacks before waiting for native teardown.
            // ReSharper disable once MethodHasAsyncOverload
            _shutdown.Cancel();
            await _serial.WaitAsync().ConfigureAwait(false);
            try
            {
                await OnMainAsync(() =>
                    {
                        ThrowIfRetirementFailed();
                        ClearNative();
                        ReleaseDifference(_surface, new Surface(null, null, null, null));
                        _surface = new Surface(null, null, null, null);
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

    private void ReleaseDifference(Surface retired, Surface retained)
    {
        try
        {
            ReleaseDifferenceCore(retired, retained);
        }
        catch (Exception failure)
        {
            RememberUnsafeCleanup(failure);
            throw;
        }
    }

    private static void ReleaseDifferenceCore(Surface retired, Surface retained)
    {
        var failures = new List<Exception>();
        if (retired.Top is { } top && !ReferenceEquals(top, retained.Top))
            try
            {
                if (top.Controller.ParentViewController is not null
                    || top.Controller is { IsViewLoaded: true, View.Window: not null }
                    || PresentedChildren(top.Controller)
                        .Any(static child => child is { IsViewLoaded: true, View.Window: not null }))
                    throw new InvalidOperationException("The borrowed pushed page did not physically detach.");
                RetireCore(top);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

        if (retired.Modal is { } modal && !ReferenceEquals(modal, retained.Modal))
            try
            {
                RetireCore(modal);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

        if (retired.Root is { } root && !ReferenceEquals(root, retained.Root))
            try
            {
                RetireCore(root);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

        if (failures.Count != 0)
            throw new AggregateException("Native artifact retirement failed.", failures);
    }

    private void Retire(PresentedPage page)
    {
        try
        {
            RetireCore(page);
        }
        catch (Exception failure)
        {
            RememberUnsafeCleanup(failure);
            throw;
        }
    }

    private static void RetireCore(PresentedPage page)
    {
        if (page.HasLoan
            && (page.Controller.ParentViewController is not null
                || page.Controller is { IsViewLoaded: true, View.Window: not null }
                || PresentedChildren(page.Controller)
                    .Any(static child => child is { IsViewLoaded: true, View.Window: not null })))
            throw new InvalidOperationException("The borrowed native page did not physically detach.");
        var failures = new List<Exception>();
        if (page.Controller is UINavigationController navigation)
        {
            UIViewController[] children = navigation.ViewControllers ?? [];
            try
            {
                navigation.SetViewControllers([], false);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }

            foreach (UIViewController child in children)
                try
                {
                    child.Dispose();
                }
                catch (Exception failure)
                {
                    failures.Add(failure);
                }
        }

        try
        {
            page.Controller.Dispose();
        }
        catch (Exception failure)
        {
            failures.Add(failure);
        }

        // Native retirement must finish before releasing the optional business loan. Keep it on any failure.
        if (failures.Count == 0)
            try { page.ReleaseLoan(); }
            catch (Exception failure) { failures.Add(failure); }

        if (failures.Count != 0)
            throw new AggregateException("Native controller retirement failed.", failures);
    }

    private static Task OnMainAsync(Func<Task> action)
    {
        if (NSThread.IsMain)
            return action();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // UIKit accepts Action; the entire awaited body reports failure through done, never async-void escape.
        // ReSharper disable once AsyncVoidLambda
        UIApplication.SharedApplication.BeginInvokeOnMainThread(async () =>
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
        if (!NSThread.IsMain)
            throw new InvalidOperationException("Native presentation requires the main thread.");
    }

    // A queued native display tick may outlive invalidation; revoke every scope/task capture first.
    private sealed class DisplayedFrameAcknowledgement(UIWindow window, TaskCompletionSource done)
    {
        private UIWindow? _window = window;
        private TaskCompletionSource? _done = done;
        private CADisplayLink? _link;
        private double _target;

        public void Attach(CADisplayLink link) => _link = link;

        public void Tick()
        {
            if (_link is not { } current
                || _window?.IsKeyWindow != true
                || UIApplication.SharedApplication.ApplicationState != UIApplicationState.Active)
                return;
            if (_target == 0)
                _target = current.TargetTimestamp;
            else if (current.Timestamp >= _target)
                _ = _done?.TrySetResult();
        }

        public void Stop()
        {
            _window = null;
            _done = null;
            _link = null;
        }
    }

    private sealed class NativeCompletion(TaskCompletionSource done)
    {
        private TaskCompletionSource? _done = done;
        public void Complete() => Volatile.Read(ref _done)?.TrySetResult();
        public void Stop() => Interlocked.Exchange(ref _done, null);
    }

    private sealed record Surface(
        RouteEntry? RootEntry,
        PresentedPage? Root,
        RouteEntry? ModalEntry,
        PresentedPage? Modal,
        RouteEntry? TopEntry = null,
        PresentedPage? Top = null);

    private sealed class StackDelegate(WeakReference<UIKitNavigationPresenter> owner) : UINavigationControllerDelegate
    {
        public override void DidShowViewController(
            UINavigationController navigationController,
            UIViewController viewController,
            bool animated)
        {
            if (owner.TryGetTarget(out UIKitNavigationPresenter? presenter)
                && ReferenceEquals(presenter._nativeRoot, navigationController))
                presenter.ReportCommittedPop(true);
        }
    }

    private sealed class ModalDelegate(UIKitNavigationPresenter owner, PresentedPage original)
        : UIAdaptivePresentationControllerDelegate
    {
        private UIKitNavigationPresenter? _owner = owner;
        private PresentedPage? _original = original;
        public override bool ShouldDismiss(UIPresentationController presentationController) => false;

        public override void DidAttemptToDismiss(UIPresentationController presentationController)
        {
            if (_original is { } captured)
                _owner?.BackIntent(captured);
        }

        public override void DidDismiss(UIPresentationController presentationController)
        {
            if (_original is { } captured)
                _owner?.UnexpectedDismissal(captured);
        }

        public void Stop()
        {
            _owner = null;
            _original = null;
        } // A queued UIKit callback may still hold the native peer.
    }
}

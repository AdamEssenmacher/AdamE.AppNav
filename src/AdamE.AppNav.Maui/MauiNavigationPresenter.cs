using System.Runtime.CompilerServices;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;
using AdamE.AppNav.Maui.AppLinks;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

internal sealed class MauiNavigationPresenter :
    INavigationPresenter,
    IMauiPresentationState,
    IMauiRoutePresentationNavigator
{
    private readonly IMauiRoutePageFactory _pageFactory;
    private readonly IMauiBranchHostFactory _defaultBranchHostFactory = new MauiTabbedBranchHostFactory();
    private readonly IServiceProvider _services;
    private readonly MauiRoutePresentationOptions _presentationOptions;
    private readonly IMauiPresentationVerifier _presentationVerifier;
    private readonly IMauiNativeNavigationOperations _nativeOperations;
    private readonly IMauiPresentationOperationPolicy _presentationOperationPolicy;
    private readonly MauiExternalNavigationDispatcher? _externalNavigationDispatcher;
    private readonly NavigationDiagnostics _diagnostics;
    private readonly Dictionary<Page, IMauiBranchHost> _branchHostPages =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Page, IMauiBranchHostFactory> _branchHostFactories =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IMauiBranchHost> _trackedBranchHosts =
        new(ReferenceEqualityComparer.Instance);
    private readonly IMauiMainThreadDispatcher _mainThreadDispatcher;
    private readonly ConditionalWeakTable<Page, MauiNativeTreeEpoch> _pageEpochs = new();
    private readonly ConditionalWeakTable<Window, MauiNativeTreeEpoch> _windowEpochs = new();
    private readonly ConditionalWeakTable<Window, DestroyedWindowMarker> _destroyedWindows = new();
    private readonly ConditionalWeakTable<Page, ReleasedPageMarker> _releasedPages = new();
    private readonly ConditionalWeakTable<IMauiBranchHost, ReleasedBranchHostMarker> _releasedBranchHosts = new();
    private readonly MauiAbandonmentCleanupCoordinator _abandonmentCleanup;
    private readonly Lock _releaseGate = new();
    private readonly SemaphoreSlim _presentationOperationLock = new(1, 1);
    private readonly Lock _lifetimeGate = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly TaskCompletionSource<bool> _shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NavigationState _lastState = NavigationState.Empty;
    private Window? _attachedWindow;
    private string? _attachedWindowId;
    private Window? _pendingWindow;
    private MauiPresenterHostState _hostState;
    private string _lifecycleOperationId = CreateOperationId();
    private string? _activeOperationId;
    private NavigationPresentationContext? _activeNavigationPresentationContext;
    private NavigationPresentationContext? _lastNavigationPresentationContext;
    private bool _suppressReconciliation;
    private MauiNativeTreeEpoch _nativeTreeEpoch = new();
    private bool _disposed;
    private bool _shutdownSignalIssued;
    private bool _shutdownCancellationCompleted;
    private bool _finalCleanupStarted;
    private int _activeOperations;
    private MauiPresentationConsistencyException? _consistencyFailure;
    private MauiPresentationTransaction? _activeTransaction;
    private MauiPresentationOperationScope? _activePresentationOperation;

    public MauiNavigationPresenter(
        IMauiRoutePageFactory pageFactory,
        MauiExternalNavigationDispatcher? externalNavigationDispatcher = null,
        NavigationDiagnostics? diagnostics = null,
        MauiRoutePresentationOptions? presentationOptions = null,
        IMauiPresentationVerifier? presentationVerifier = null,
        IMauiNativeNavigationOperations? nativeOperations = null,
        IMauiPresentationOperationPolicy? presentationOperationPolicy = null,
        IServiceProvider? services = null,
        IMauiMainThreadDispatcher? mainThreadDispatcher = null)
    {
        _pageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
        _services = services ?? EmptyServiceProvider.Instance;
        _presentationOptions = presentationOptions ?? new MauiRoutePresentationOptions();
        _presentationVerifier = presentationVerifier ?? MauiPresentationVerifier.Instance;
        _nativeOperations = new GuardedMauiNativeNavigationOperations(
            nativeOperations ?? MauiNativeNavigationOperations.Instance,
            CanMutatePage,
            CanMutateWindow);
        _presentationOperationPolicy = presentationOperationPolicy ?? new DefaultMauiPresentationOperationPolicy();
        _externalNavigationDispatcher = externalNavigationDispatcher;
        _diagnostics = diagnostics ?? NavigationDiagnostics.None;
        _mainThreadDispatcher = mainThreadDispatcher ?? MauiMainThreadDispatcher.Instance;
        _abandonmentCleanup = new MauiAbandonmentCleanupCoordinator(
            (_, exception) => WritePageReleaseFailure(null, exception));
    }

    private Dictionary<NavigationPage, string> _navigationPageStackIds =>
        _nativeTreeEpoch.NavigationPageStackIds;

    private Dictionary<NavigationPage, IReadOnlyList<Page>> _navigationPageKnownPages =>
        _nativeTreeEpoch.NavigationPageKnownPages;

    private Dictionary<NavigationPage, SuppressedNavigationPop> _suppressedNavigationPops =>
        _nativeTreeEpoch.SuppressedNavigationPops;

    private HashSet<Page> _trackedModalPages => _nativeTreeEpoch.TrackedModalPages;

    private Dictionary<IMauiBranchHost, string> _pendingBranchHostSelections =>
        _nativeTreeEpoch.PendingBranchHostSelections;

    private bool _suppressedNavigationPopDrainQueued
    {
        get => _nativeTreeEpoch.SuppressedNavigationPopDrainQueued;
        set => _nativeTreeEpoch.SuppressedNavigationPopDrainQueued = value;
    }

    private bool _hostBackReconciliationPending
    {
        get => _nativeTreeEpoch.HostBackReconciliationPending;
        set => _nativeTreeEpoch.HostBackReconciliationPending = value;
    }

    private bool _branchHostSelectionDrainQueued
    {
        get => _nativeTreeEpoch.BranchHostSelectionDrainQueued;
        set => _nativeTreeEpoch.BranchHostSelectionDrainQueued = value;
    }

    private AppRoute? _pendingHostBackRoute
    {
        get => _nativeTreeEpoch.PendingHostBackRoute;
        set => _nativeTreeEpoch.PendingHostBackRoute = value;
    }

    public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested;

    public event EventHandler<Page?>? RootPageChanged;

    public Page? CurrentPage { get; private set; }

    public Window? AttachedWindow => _attachedWindow;

    public string? AttachedWindowId => _attachedWindowId;

    public Page? RootPage => CurrentPage;

    public Page? GetTopPresentedPage()
    {
        return ResolveTopPresentedPage(CurrentPage);
    }

    public bool IsModalPresented(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return CurrentPage?.Navigation.ModalStack.Any(candidate => ReferenceEquals(candidate, page)) == true ||
               _attachedWindow?.Page?.Navigation.ModalStack.Any(candidate => ReferenceEquals(candidate, page)) == true;
    }

    public async ValueTask PushAsync<TPage>(
        string key,
        MauiRoutePresentationPageOptions? options = null,
        CancellationToken cancellationToken = default)
        where TPage : Page
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        BeginOperation();

        options ??= new MauiRoutePresentationPageOptions();
        var lockTaken = false;
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            CancellationToken operationCancellation = CreateOperationCancellation(
                cancellationToken,
                out linkedCancellation);
            await _presentationOperationLock.WaitAsync(operationCancellation).ConfigureAwait(false);
            lockTaken = true;
            if (_mainThreadDispatcher.IsMainThread)
            {
                await PushPresentationPageOnMainThreadAsync(typeof(TPage), key, options, operationCancellation);
                return;
            }

            await _mainThreadDispatcher.InvokeAsync(
                () => PushPresentationPageOnMainThreadAsync(typeof(TPage), key, options, operationCancellation));
        }
        finally
        {
            if (lockTaken)
                _presentationOperationLock.Release();

            linkedCancellation?.Dispose();
            EndOperation();
        }
    }

    public async ValueTask<bool> PopAsync(
        bool animated = true,
        CancellationToken cancellationToken = default)
    {
        BeginOperation();
        var lockTaken = false;
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            CancellationToken operationCancellation = CreateOperationCancellation(
                cancellationToken,
                out linkedCancellation);
            await _presentationOperationLock.WaitAsync(operationCancellation).ConfigureAwait(false);
            lockTaken = true;
            if (_mainThreadDispatcher.IsMainThread)
            {
                return await PopPresentationPageOnMainThreadAsync(animated, operationCancellation);
            }

            return await _mainThreadDispatcher.InvokeAsync(
                () => PopPresentationPageOnMainThreadAsync(animated, operationCancellation));
        }
        finally
        {
            if (lockTaken)
                _presentationOperationLock.Release();

            linkedCancellation?.Dispose();
            EndOperation();
        }
    }

    internal Task StartShutdown()
    {
        var signalShutdown = false;
        lock (_lifetimeGate)
        {
            if (!_disposed)
            {
                _disposed = true;
                signalShutdown = true;
            }
        }

        if (signalShutdown)
        {
            try
            {
                _ = ObserveShutdownCancellationAsync(_shutdownCancellation.CancelAsync());
            }
            catch (Exception)
            {
                _ = ObserveShutdownCancellationAsync(Task.CompletedTask);
            }
        }

        var startCleanup = false;
        lock (_lifetimeGate)
        {
            if (signalShutdown)
                _shutdownSignalIssued = true;

            if (_disposed && _shutdownSignalIssued && _shutdownCancellationCompleted &&
                _activeOperations == 0 && !_finalCleanupStarted)
            {
                _finalCleanupStarted = true;
                startCleanup = true;
            }
        }

        if (startCleanup)
            QueueFinalCleanup();

        return _shutdownCompletion.Task;
    }

    private async Task ObserveShutdownCancellationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancellation callback ownership remains with the callback registrant.
        }

        var startCleanup = false;
        lock (_lifetimeGate)
        {
            _shutdownCancellationCompleted = true;
            if (_disposed && _shutdownSignalIssued && _activeOperations == 0 && !_finalCleanupStarted)
            {
                _finalCleanupStarted = true;
                startCleanup = true;
            }
        }

        if (startCleanup)
            QueueFinalCleanup();
    }

    private void QueueFinalCleanup()
    {
        _ = Task.Run(FinalizeShutdownAsync, CancellationToken.None);
    }

    private async Task FinalizeShutdownAsync()
    {
        MauiNativeTreeEpochClosure? epochClosure = null;
        try
        {
            if (_mainThreadDispatcher.IsMainThread)
                await FinalizeShutdownOnMainThreadAsync();
            else
                await _mainThreadDispatcher.InvokeAsync(FinalizeShutdownOnMainThreadAsync);

            epochClosure = _nativeTreeEpoch.Close();
            await _abandonmentCleanup.SealAndDrainAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WritePageReleaseFailure(null, ex);
        }
        finally
        {
            epochClosure ??= _nativeTreeEpoch.Close();
            await epochClosure.CompleteAsync().ConfigureAwait(false);
            await _abandonmentCleanup.SealAndDrainAsync().ConfigureAwait(false);
            _presentationOperationLock.Dispose();
            _shutdownCancellation.Dispose();
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationPresenterDisposed,
                LifecycleOperationId(),
                "MAUI navigation presenter was disposed.");
            _shutdownCompletion.TrySetResult(true);
        }
    }

    private async Task FinalizeShutdownOnMainThreadAsync()
    {
        _suppressReconciliation = true;
        _activeOperationId = null;

        // Destruction is a synchronous ownership handoff. Stop observing it before the first
        // asynchronous release below so shutdown finalization remains the sole owner of these pages.
        if (_attachedWindow is not null)
        {
            UnsubscribeWindowLifecycle(_attachedWindow);
            _externalNavigationDispatcher?.SetForegrounded(false);
        }

        if (_pendingWindow is not null)
            _pendingWindow.Destroying -= HandleWindowDestroying;

        Page? currentPage = CurrentPage;
        var detachedCandidates = new HashSet<Page>(ReferenceEqualityComparer.Instance);
        detachedCandidates.UnionWith(_trackedModalPages);
        foreach (IReadOnlyList<Page> knownPages in _navigationPageKnownPages.Values)
            detachedCandidates.UnionWith(knownPages);
        foreach (SuppressedNavigationPop pendingPop in _suppressedNavigationPops.Values)
            detachedCandidates.UnionWith(pendingPop.KnownPages);
        CurrentPage = null;
        try
        {
            SetAttachedWindowPage(null);
        }
        catch (Exception ex)
        {
            WritePageReleaseFailure(null, ex);
        }

        _attachedWindow = null;
        _attachedWindowId = null;
        _pendingWindow = null;
        _hostState = MauiPresenterHostState.Disposed;
        InvokeRootPageChanged(null);
        if (currentPage is not null)
            await ReleaseAndDiagnoseAsync(currentPage);
        foreach (Page detachedPage in detachedCandidates)
            await ReleaseAndDiagnoseAsync(detachedPage);

        foreach (NavigationPage navigationPage in _navigationPageStackIds.Keys.ToArray())
            UntrackNavigationPage(navigationPage);
        foreach (IMauiBranchHost host in new HashSet<IMauiBranchHost>(
                     _branchHostPages.Values,
                     ReferenceEqualityComparer.Instance).ToArray())
        {
            UntrackBranchHost(host);
            Page hostPage = host.Page;
            try
            {
                await DisposeBranchHostAsync(host);
            }
            catch (Exception ex)
            {
                WritePageReleaseFailure(hostPage, ex);
            }
        }
        foreach (Page modalPage in _trackedModalPages.ToArray())
            UntrackModalPage(modalPage);

        _navigationPageStackIds.Clear();
        _navigationPageKnownPages.Clear();
        _suppressedNavigationPops.Clear();
        _pendingBranchHostSelections.Clear();
        _branchHostSelectionDrainQueued = false;
        _hostBackReconciliationPending = false;
        _pendingHostBackRoute = null;
        _branchHostPages.Clear();
        _branchHostFactories.Clear();
        _trackedBranchHosts.Clear();
        _trackedModalPages.Clear();
        _lastState = NavigationState.Empty;
    }

    public async ValueTask AttachWindowAsync(
        Window window,
        string windowId = "main",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowId);

        await RunSerializedWindowMutationAsync(
            cancellationToken => AttachWindowOnMainThreadAsync(window, windowId, cancellationToken),
            cancellationToken);
    }

    public async ValueTask DetachWindowAsync(
        Window window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);

        await RunSerializedWindowMutationAsync(
            _ =>
            {
                DetachWindowOnMainThread(window);
                return Task.CompletedTask;
            },
            cancellationToken);
    }

    private async Task AttachWindowOnMainThreadAsync(
        Window window,
        string windowId,
        CancellationToken cancellationToken)
    {
        if (_destroyedWindows.TryGetValue(window, out _))
        {
            throw new InvalidOperationException(
                "A destroyed MAUI window cannot be attached to the navigation presenter again.");
        }

        WindowNode? presentedWindow = _lastState.ActiveWindow;
        if (presentedWindow is not null &&
            !StringComparer.Ordinal.Equals(presentedWindow.Id, windowId))
        {
            throw new AppNavigationConfigurationException(
                $"Presented navigation state window id '{presentedWindow.Id}' does not match the MAUI window id '{windowId}'.");
        }

        if (_hostState == MauiPresenterHostState.AwaitingReplacement)
        {
            await AttachReplacementWindowAsync(window, windowId, cancellationToken);
            return;
        }

        Window? previousWindow = _attachedWindow;
        RegisterWindow(window);
        try
        {
            TransferCurrentPage(previousWindow, window);
        }
        catch
        {
            if (!ReferenceEquals(previousWindow, window))
                ForgetWindow(window);
            throw;
        }

        if (previousWindow is not null && !ReferenceEquals(previousWindow, window))
        {
            UnsubscribeWindowLifecycle(previousWindow);
            ForgetWindow(previousWindow);
        }

        bool alreadyAttached = ReferenceEquals(previousWindow, window);
        _attachedWindow = window;
        _attachedWindowId = windowId;
        _hostState = MauiPresenterHostState.Attached;
        if (!alreadyAttached)
            SubscribeWindowLifecycle(window);

        _externalNavigationDispatcher?.SetForegrounded(true);
        _externalNavigationDispatcher?.MarkReady();
    }

    private void DetachWindowOnMainThread(Window window)
    {
        if (ReferenceEquals(_attachedWindow, window))
        {
            TransferCurrentPage(window, null);
            UnsubscribeWindowLifecycle(window);
            _externalNavigationDispatcher?.SetForegrounded(false);
            _attachedWindow = null;
            _attachedWindowId = null;
            _hostState = MauiPresenterHostState.Detached;
            ForgetWindow(window);
        }
    }

    private async Task AttachReplacementWindowAsync(
        Window window,
        string windowId,
        CancellationToken cancellationToken)
    {
        RegisterWindow(window);
        _pendingWindow = window;
        window.Destroying += HandleWindowDestroying;
        Page? previousWindowPage = window.Page;
        MauiNativeTreeEpoch candidateEpoch = _nativeTreeEpoch;
        using var candidateCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            candidateEpoch.CancellationToken);
        CancellationToken buildCancellation = candidateCancellation.Token;
        await using var transaction = new MauiPresentationTransaction(this, cancellationToken);
        bool previousSuppression = _suppressReconciliation;
        NavigationPresentationContext? previousPresentationContext = _activeNavigationPresentationContext;
        string operationId = LifecycleOperationId();
        _activeNavigationPresentationContext = _lastNavigationPresentationContext is { } lastContext
            ? lastContext with { CurrentState = _lastState, OperationId = operationId }
            : null;
        _suppressReconciliation = true;
        _activeTransaction = transaction;
        Page? candidateRoot = null;
        try
        {
            WindowNode? logicalWindow = _lastState.ActiveWindow;
            if (logicalWindow is not null && (logicalWindow.Root is not null || logicalWindow.Modals.Count > 0))
            {
                candidateRoot = logicalWindow.Root is null
                    ? CreateEmptyPage()
                    : await MaterializeNodeAsync(
                        logicalWindow.Root,
                        null,
                        operationId,
                        isNavigationTarget: logicalWindow.Modals.Count == 0,
                        buildCancellation);
                await ApplyModalsAsync(
                    candidateRoot,
                    logicalWindow.Modals,
                    operationId,
                    buildCancellation);
            }

            buildCancellation.ThrowIfCancellationRequested();
            if (!EpochRemainsCurrent(candidateEpoch) || !ReferenceEquals(_pendingWindow, window))
                throw new MauiNativeTreeInvalidatedException();

            VerifyPresentation(_lastState, operationId, candidateRoot, attachedWindow: null);
            _nativeOperations.SetWindowPage(window, candidateRoot);
            buildCancellation.ThrowIfCancellationRequested();
            VerifyPresentation(_lastState, operationId, candidateRoot, attachedWindow: window);

            CurrentPage = candidateRoot;
            bool epochRemainedCurrent = await transaction.CommitAsync();
            if (!epochRemainedCurrent)
                throw new MauiNativeTreeInvalidatedException();

            window.Destroying -= HandleWindowDestroying;
            _pendingWindow = null;
            _attachedWindow = window;
            _attachedWindowId = windowId;
            _hostState = MauiPresenterHostState.Attached;
            SubscribeWindowLifecycle(window);
            _activeTransaction = null;
            InvokeRootPageChanged(candidateRoot);
            if (!EpochRemainsCurrent(candidateEpoch) ||
                !ReferenceEquals(_attachedWindow, window) || !ReferenceEquals(CurrentPage, candidateRoot) ||
                !ReferenceEquals(window.Page, candidateRoot))
            {
                throw new MauiNativeTreeInvalidatedException();
            }

            _externalNavigationDispatcher?.SetForegrounded(true);
            _externalNavigationDispatcher?.MarkReady();
        }
        catch (Exception attachmentException)
        {
            _activeTransaction = null;
            if (EpochRemainsCurrent(candidateEpoch))
            {
                if (ReferenceEquals(CurrentPage, candidateRoot))
                    CurrentPage = null;

                if (window.Page is Page installedCandidatePage &&
                    candidateEpoch.Owns(installedCandidatePage) &&
                    !ReferenceEquals(installedCandidatePage, previousWindowPage))
                {
                    try
                    {
                        _nativeOperations.SetWindowPage(window, previousWindowPage);
                        if (!ReferenceEquals(window.Page, previousWindowPage))
                        {
                            throw new InvalidOperationException(
                                "The native window did not restore its previous page after replacement attachment failed.");
                        }
                    }
                    catch (Exception restoreException)
                    {
                        var consistencyException = new MauiPresentationConsistencyException(
                            "The MAUI presenter could not restore replacement-window page ownership after attachment failed.",
                            new AggregateException(
                                "Replacement-window attachment and rollback failed.",
                                attachmentException,
                                restoreException));
                        lock (_lifetimeGate)
                        {
                            _consistencyFailure ??= consistencyException;
                            consistencyException = _consistencyFailure;
                        }

                        if (window.Page is Page retainedCandidatePage &&
                            candidateEpoch.Owns(retainedCandidatePage))
                        {
                            window.Destroying -= HandleWindowDestroying;
                            _pendingWindow = null;
                            _attachedWindow = window;
                            _attachedWindowId = windowId;
                            CurrentPage = retainedCandidatePage;
                            _hostState = MauiPresenterHostState.Attached;
                            SubscribeWindowLifecycle(window);
                            InvokeRootPageChanged(retainedCandidatePage);
                        }

                        throw consistencyException;
                    }
                }

                if (ReferenceEquals(_attachedWindow, window))
                {
                    UnsubscribeWindowLifecycle(window);
                    _attachedWindow = null;
                    _attachedWindowId = null;
                }
                else
                {
                    window.Destroying -= HandleWindowDestroying;
                }

                _pendingWindow = null;

                // Finalize branch-host updates before releasing the pages they reference: a pending update may
                // still hold provisional state that has to be reversed while its host and pages are intact.
                await transaction.DisposeAsync();
                await transaction.ReleaseCreatedPagesAsync();
                ClearNativeTreeTracking();
                ForgetWindow(window);
            }

            _hostState = MauiPresenterHostState.AwaitingReplacement;
            throw;
        }
        finally
        {
            _activeTransaction = null;
            _activeNavigationPresentationContext = previousPresentationContext;
            _suppressReconciliation = previousSuppression;
        }
    }

    private async ValueTask RunSerializedWindowMutationAsync(
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken)
    {
        BeginOperation();
        var lockTaken = false;
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            CancellationToken operationCancellation = CreateHostOperationCancellation(
                cancellationToken,
                out linkedCancellation);
            await _presentationOperationLock.WaitAsync(operationCancellation).ConfigureAwait(false);
            lockTaken = true;
            await InvokeOnMainThreadPreservingExecutionContextAsync(() =>
            {
                operationCancellation.ThrowIfCancellationRequested();
                return mutation(operationCancellation);
            });
        }
        finally
        {
            if (lockTaken)
                _presentationOperationLock.Release();

            linkedCancellation?.Dispose();
            EndOperation();
        }
    }

    private void TransferCurrentPage(Window? sourceWindow, Window? destinationWindow)
    {
        if (ReferenceEquals(sourceWindow, destinationWindow))
            sourceWindow = null;

        Page? currentPage = CurrentPage;
        if (currentPage is null)
            return;

        Page? sourcePage = sourceWindow?.Page;
        Page? destinationPage = destinationWindow?.Page;
        bool clearSource = sourceWindow is not null && ReferenceEquals(sourcePage, currentPage);
        bool assignDestination = destinationWindow is not null && !ReferenceEquals(destinationPage, currentPage);
        if (!clearSource && !assignDestination)
            return;

        try
        {
            if (clearSource)
                _nativeOperations.SetWindowPage(sourceWindow!, null);
            if (assignDestination)
                _nativeOperations.SetWindowPage(destinationWindow!, currentPage);
        }
        catch (Exception transferException)
        {
            var rollbackFailures = new List<Exception>();
            if (destinationWindow is not null && !ReferenceEquals(destinationWindow.Page, destinationPage))
            {
                try
                {
                    _nativeOperations.SetWindowPage(destinationWindow, destinationPage);
                }
                catch (Exception rollbackException)
                {
                    rollbackFailures.Add(rollbackException);
                }
            }

            if (sourceWindow is not null && !ReferenceEquals(sourceWindow.Page, sourcePage))
            {
                try
                {
                    _nativeOperations.SetWindowPage(sourceWindow, sourcePage);
                }
                catch (Exception rollbackException)
                {
                    rollbackFailures.Add(rollbackException);
                }
            }

            if (rollbackFailures.Count == 0)
                throw;

            var failures = new List<Exception>(rollbackFailures.Count + 1) { transferException };
            failures.AddRange(rollbackFailures);
            var consistencyException = new MauiPresentationConsistencyException(
                "The MAUI presenter could not restore window page ownership after an attachment failure.",
                new AggregateException("Window page transfer and rollback failed.", failures));
            lock (_lifetimeGate)
            {
                _consistencyFailure ??= consistencyException;
                consistencyException = _consistencyFailure;
            }

            throw consistencyException;
        }
    }

    private void SubscribeWindowLifecycle(Window window)
    {
        window.Activated += HandleWindowActivated;
        window.Deactivated += HandleWindowDeactivated;
        window.Stopped += HandleWindowStopped;
        window.Resumed += HandleWindowResumed;
        window.Destroying += HandleWindowDestroying;
    }

    private void UnsubscribeWindowLifecycle(Window window)
    {
        window.Activated -= HandleWindowActivated;
        window.Deactivated -= HandleWindowDeactivated;
        window.Stopped -= HandleWindowStopped;
        window.Resumed -= HandleWindowResumed;
        window.Destroying -= HandleWindowDestroying;
    }

    private void HandleWindowActivated(object? sender, EventArgs e)
    {
        _externalNavigationDispatcher?.SetForegrounded(true);
    }

    private void HandleWindowDeactivated(object? sender, EventArgs e)
    {
        _externalNavigationDispatcher?.SetForegrounded(false);
    }

    private void HandleWindowStopped(object? sender, EventArgs e)
    {
        _externalNavigationDispatcher?.SetForegrounded(false);
    }

    private void HandleWindowResumed(object? sender, EventArgs e)
    {
        _externalNavigationDispatcher?.SetForegrounded(true);
    }

    private void HandleWindowDestroying(object? sender, EventArgs e)
    {
        if (sender is not Window window ||
            (!ReferenceEquals(_attachedWindow, window) && !ReferenceEquals(_pendingWindow, window)))
        {
            return;
        }

        _externalNavigationDispatcher?.SetForegrounded(false);
        bool publishRootLoss = _hostState != MauiPresenterHostState.AwaitingReplacement;
        _destroyedWindows.GetValue(window, static _ => new DestroyedWindowMarker());

        MauiNativeTreeEpoch abandonedEpoch = _nativeTreeEpoch;
        MauiNativeTreeEpochClosure closure = abandonedEpoch.Close();
        var abandonments = new List<MauiPageAbandonment>();
        try
        {
            // Detach every presenter-owned handler and finish reading host metadata *before* any application
            // DisposeAsync runs. A host that marks itself disposed is entitled to reject later SelectionChanged
            // removal or Page access, and teardown must not be abortable once the epoch is closed.
            IMauiBranchHost[] destroyedHosts = _trackedBranchHosts.ToArray();
            foreach (IMauiBranchHost host in destroyedHosts)
                UntrackBranchHostAndDiagnose(host);

            ClearNativeTreeTracking();

            foreach (IMauiBranchHost host in destroyedHosts)
            {
                MauiPageAbandonment? abandonment = CaptureBranchHostAbandonmentOrDiagnose(host);
                if (abandonment is not null)
                    abandonments.Add(abandonment);
            }

            _activeTransaction?.CaptureBranchHostUpdateAbandonments(abandonments);
            foreach (Page page in closure.Pages)
            {
                MauiPageAbandonment? abandonment = CaptureAbandonmentOrDiagnose(page);
                if (abandonment is not null)
                    abandonments.Add(abandonment);
            }
        }
        catch (Exception captureException)
        {
            // Capturing leases is best-effort. Leaving the presenter half-torn-down -- epoch closed, no
            // replacement installed, window still attached -- is strictly worse than losing one lease.
            WritePageReleaseFailure(null, captureException);
        }
        finally
        {
            try
            {
                if (_attachedWindow is not null)
                    UnsubscribeWindowLifecycle(_attachedWindow);
                if (_pendingWindow is not null)
                    _pendingWindow.Destroying -= HandleWindowDestroying;
            }
            catch (Exception unsubscribeException)
            {
                WritePageReleaseFailure(null, unsubscribeException);
            }

            _attachedWindow = null;
            _attachedWindowId = null;
            _pendingWindow = null;
            _activeTransaction?.AbandonNativeReferences();
            _activeTransaction = null;
            _activePresentationOperation = null;
            CurrentPage = null;
            _hostState = MauiPresenterHostState.AwaitingReplacement;
            _nativeTreeEpoch = new MauiNativeTreeEpoch();

            // Enqueue before publishing root loss: observers are application code, and a lease that is never
            // drained would block shutdown forever.
            Task prerequisite = Task.WhenAll(
                closure.CompleteAsync(),
                WaitForPresentationIdleAsync());
            _abandonmentCleanup.EnqueueAfter(prerequisite, abandonments);

            if (publishRootLoss)
                InvokeRootPageChanged(null);
        }
    }

    private void UntrackBranchHostAndDiagnose(IMauiBranchHost host)
    {
        try
        {
            UntrackBranchHost(host);
        }
        catch (Exception exception)
        {
            WritePageReleaseFailure(null, exception);
            _trackedBranchHosts.Remove(host);
        }
    }

    private void ClearNativeTreeTracking()
    {
        foreach (NavigationPage navigationPage in _navigationPageStackIds.Keys.ToArray())
            UntrackNavigationPage(navigationPage);
        foreach (IMauiBranchHost host in _trackedBranchHosts.ToArray())
            UntrackBranchHost(host);
        foreach (Page modalPage in _trackedModalPages.ToArray())
            UntrackModalPage(modalPage);

        _navigationPageStackIds.Clear();
        _navigationPageKnownPages.Clear();
        _suppressedNavigationPops.Clear();
        _pendingBranchHostSelections.Clear();
        _branchHostSelectionDrainQueued = false;
        _hostBackReconciliationPending = false;
        _pendingHostBackRoute = null;
        _branchHostPages.Clear();
        _branchHostFactories.Clear();
        _trackedBranchHosts.Clear();
        _trackedModalPages.Clear();
    }

    private async Task WaitForPresentationIdleAsync()
    {
        await _presentationOperationLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _presentationOperationLock.Release();
    }

    public async ValueTask ApplyAsync(
        NavigationPlan plan,
        NavigationPresentationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);
        BeginOperation();

        var lockTaken = false;
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            CancellationToken operationCancellation = CreateOperationCancellation(
                cancellationToken,
                out linkedCancellation);
            await _presentationOperationLock.WaitAsync(operationCancellation).ConfigureAwait(false);
            lockTaken = true;
            await InvokeOnMainThreadPreservingExecutionContextAsync(
                () => ApplyOnMainThreadAsync(plan, context, operationCancellation));
        }
        finally
        {
            if (lockTaken)
                _presentationOperationLock.Release();

            linkedCancellation?.Dispose();
            EndOperation();
        }
    }

    private Task InvokeOnMainThreadPreservingExecutionContextAsync(Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (_mainThreadDispatcher.IsMainThread)
            return callback();

        ExecutionContext executionContext = ExecutionContext.Capture() ??
            throw new InvalidOperationException(
                "MAUI presentation cannot switch to the main thread while execution-context flow is suppressed.");

        return _mainThreadDispatcher.InvokeAsync(() =>
        {
            Task? callbackTask = null;
            ExecutionContext.Run(executionContext, _ => callbackTask = callback(), null);
            return callbackTask ?? throw new InvalidOperationException(
                "The main-thread presentation callback did not return a task.");
        });
    }

    private async Task ApplyOnMainThreadAsync(
        NavigationPlan plan,
        NavigationPresentationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePlanForMaui(plan);
        if (_hostState == MauiPresenterHostState.AwaitingReplacement)
        {
            _lastState = plan.TargetState;
            _lastNavigationPresentationContext = context;
            return;
        }

        bool previousSuppressReconciliation = _suppressReconciliation;
        _suppressReconciliation = true;
        _activeOperationId = context.OperationId;
        _activeNavigationPresentationContext = context;
        await using var transaction = new MauiPresentationTransaction(this, cancellationToken);
        _activeTransaction = transaction;
        MauiPresentationOperationCandidate? operationCandidate =
            MauiPresentationOperationSelector.Select(_lastState, plan);
        _activePresentationOperation = operationCandidate is null
            ? null
            : new MauiPresentationOperationScope(
                _presentationOperationPolicy,
                plan,
                context,
                operationCandidate);
        try
        {
            WindowNode? window = plan.TargetState.ActiveWindow;
            if (window is null || (window.Root is null && window.Modals.Count == 0))
            {
                await SetCurrentPageAsync(null);
            }
            else
            {
                Page nextRoot = window.Root is null
                    ? CreateOrReuseEmptyRootHost(CurrentPage)
                    : await MaterializeNodeAsync(
                        window.Root,
                        CurrentPage,
                        context.OperationId,
                        isNavigationTarget: window.Modals.Count == 0,
                        cancellationToken,
                        placement: MauiBranchHostPlacement.WindowRoot);

                await SetCurrentPageAsync(nextRoot);
                await ApplyModalsAsync(nextRoot, window.Modals, context.OperationId, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            SuppressedNavigationPopFold popFold = await FoldSuppressedNavigationPopsAsync(
                plan.TargetState,
                transaction,
                cancellationToken);
            VerifyPresentation(popFold.EffectiveState, context.OperationId);
            _lastState = popFold.EffectiveState;
            bool epochRemainedCurrent = await transaction.CommitAsync();
            _activeTransaction = null;
            _lastNavigationPresentationContext = context;
            PreserveHostBackReconciliation(popFold, epochRemainedCurrent);
        }
        catch (Exception presentationException)
        {
            _activeTransaction = null;
            _activePresentationOperation = null;
            try
            {
                await RollbackOrRecoverAsync(transaction, context.OperationId, presentationException);
            }
            finally
            {
                DiscardSuppressedNavigationPops();
            }

            throw;
        }
        finally
        {
            _activeTransaction = null;
            _activePresentationOperation = null;
            _activeOperationId = null;
            _activeNavigationPresentationContext = null;
            _suppressReconciliation = previousSuppressReconciliation;
            if (!previousSuppressReconciliation)
                ScheduleSuppressedNativeChangeDrain();
        }
    }

    private void ValidatePlanForMaui(NavigationPlan plan)
    {
        NavigationState state = plan.TargetState;
        if (state.Windows.Count > 1)
        {
            throw new NotSupportedException(
                "The MAUI presenter supports one window per navigation plan. Multi-window state remains available to other adapters.");
        }

        WindowNode? targetWindow = state.ActiveWindow;
        if (state.ActiveWindowId is not null && targetWindow is null)
        {
            throw new AppNavigationConfigurationException(
                $"Navigation plan active window id '{state.ActiveWindowId}' does not reference an existing window.");
        }

        if (_attachedWindowId is not null && targetWindow is null)
        {
            throw new AppNavigationConfigurationException(
                $"Navigation plan must contain the attached MAUI window id '{_attachedWindowId}'.");
        }

        if (_attachedWindowId is not null && targetWindow is not null &&
            !StringComparer.Ordinal.Equals(_attachedWindowId, targetWindow.Id))
        {
            throw new AppNavigationConfigurationException(
                $"Navigation plan window id '{targetWindow.Id}' does not match the attached MAUI window id '{_attachedWindowId}'.");
        }

        if (targetWindow?.Root is not null)
        {
            ValidateMauiNode(targetWindow.Root, "window root", MauiBranchHostPlacement.WindowRoot);
        }

        if (targetWindow is not null)
        {
            foreach (ModalNode modal in targetWindow.Modals)
            {
                ValidateMauiNode(modal, "window modal", MauiBranchHostPlacement.ModalContent);
            }
        }
    }

    private void ValidateMauiNode(
        NavigationNode node,
        string path,
        MauiBranchHostPlacement placement)
    {
        switch (node)
        {
            case StackNode:
                return;
            case BranchHostNode branchHost:
                if (_presentationOptions.TryGetBranchHost(branchHost.Id, out MauiBranchHostRegistration? registration) &&
                    (registration.Factory.SupportedPlacements & placement) == 0)
                {
                    throw new NotSupportedException(
                        $"MAUI branch host '{branchHost.Id}' does not support placement '{placement}'; found at {path}.");
                }

                foreach (NavigationBranch branch in branchHost.Branches)
                {
                    ValidateMauiNode(
                        branch.Content,
                        $"{path} branch '{branch.Id}'",
                        MauiBranchHostPlacement.Nested);
                }

                return;
            case ModalNode modal:
                if (modal.Content is not null)
                {
                    ValidateMauiNode(modal.Content, $"{path} content", MauiBranchHostPlacement.ModalContent);
                }

                return;
            default:
                throw new NotSupportedException(
                    $"The MAUI presenter does not support navigation node type '{node.GetType().FullName}' at {path}.");
        }
    }

    private async Task RollbackOrRecoverAsync(
        MauiPresentationTransaction transaction,
        string operationId,
        Exception presentationException)
    {
        if (transaction.IsInvalidated)
            return;

        _diagnostics.Write(
            NavigationDiagnosticEventKind.PresentationRollbackStarted,
            operationId,
            "Presentation rollback started.");

        try
        {
            await transaction.RollbackAsync();
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationRollbackCompleted,
                operationId,
                "Presentation rollback completed.");
        }
        catch (Exception rollbackException)
        {
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationRollbackFailed,
                operationId,
                "Presentation rollback failed.",
                new Dictionary<string, object?>
                {
                    [NavigationDiagnosticDataKeys.ExceptionType] = rollbackException.GetType().FullName,
                    [NavigationDiagnosticDataKeys.ExceptionMessage] = rollbackException.Message
                });

            if (transaction.IsInvalidated)
                return;

            if (_disposed)
                return;

            try
            {
                await RebuildStateFromScratchAsync(transaction.PreviousState, operationId);
                await transaction.ReleaseAllNonLivePagesAsync();
            }
            catch (Exception recoveryException)
            {
                if (transaction.IsInvalidated)
                    return;

                var aggregate = new AggregateException(
                    "Presentation, rollback, and full-state recovery all failed.",
                    presentationException,
                    rollbackException,
                    recoveryException);
                var consistencyException = new MauiPresentationConsistencyException(
                    "The MAUI presenter could not restore a consistent navigation state.",
                    aggregate);
                lock (_lifetimeGate)
                {
                    _consistencyFailure = consistencyException;
                }

                throw consistencyException;
            }
        }
    }

    private async Task PushPresentationPageOnMainThreadAsync(
        Type pageType,
        string key,
        MauiRoutePresentationPageOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfAwaitingReplacementForPresentationMutation();

        var navigationPage = ResolveTopNavigationPage(CurrentPage);
        if (navigationPage is null || !_navigationPageStackIds.ContainsKey(navigationPage))
        {
            throw new InvalidOperationException(
                "The current route is not hosted by a router-owned NavigationPage and cannot own presentation pages.");
        }

        var projection = RequireValidProjection(navigationPage.Navigation.NavigationStack);
        if (projection.Segments.Count == 0)
        {
            throw new InvalidOperationException("The current native navigation stack has no logical route page.");
        }

        var owner = projection.Segments[^1];
        var topPage = navigationPage.Navigation.NavigationStack[^1];
        var topKey = MauiPresentationMetadata.GetPresentationPageKey(topPage);
        if (StringComparer.Ordinal.Equals(topKey, key))
        {
            return;
        }

        if (owner.PresentationPages.Any(page =>
                StringComparer.Ordinal.Equals(MauiPresentationMetadata.GetPresentationPageKey(page), key)))
        {
            throw new InvalidOperationException(
                $"Presentation key '{key}' already exists below the top of route entry '{owner.RouteEntryId}'.");
        }

        Page[] previousStack = navigationPage.Navigation.NavigationStack.ToArray();
        await using var transaction = new MauiPresentationTransaction(this, cancellationToken);
        string operationId = CreateOperationId();
        bool previousSuppressReconciliation = _suppressReconciliation;
        string? previousOperationId = _activeOperationId;
        _suppressReconciliation = true;
        _activeOperationId = operationId;
        _activeTransaction = transaction;
        try
        {
            MauiNativeTreeEpoch creationEpoch = _nativeTreeEpoch;
            Page page = await InvokeExtensionPointAsync(
                creationEpoch,
                token => _pageFactory.CreatePresentationPageAsync(
                    pageType,
                    owner.RoutePage,
                    options.InheritBindingContext,
                    token),
                CaptureLateAbandonment,
                cancellationToken);

            if (page is NavigationPage or TabbedPage or FlyoutPage)
            {
                await InvokeReleaseAcrossBoundaryAsync(
                    creationEpoch,
                    token => _pageFactory.ReleasePresentationPageAsync(page, token),
                    cancellationToken);
                throw new InvalidOperationException(
                    $"Route-owned presentation page '{page.GetType().FullName}' cannot be a navigation container.");
            }

            if (page.Parent is not null)
            {
                await InvokeReleaseAcrossBoundaryAsync(
                    creationEpoch,
                    token => _pageFactory.ReleasePresentationPageAsync(page, token),
                    cancellationToken);
                throw new InvalidOperationException(
                    $"Route-owned presentation page '{page.GetType().FullName}' is already attached to a visual tree.");
            }

            RegisterPage(page);
            SetPresentationOwnerRouteEntryId(page, owner.RouteEntryId);
            SetPresentationPageKey(page, key);
            transaction.TrackCreated(page);
            WritePageLifecycle(
                NavigationDiagnosticEventKind.PresentationPageCreated,
                page,
                "Route-owned presentation page was created.");

            cancellationToken.ThrowIfCancellationRequested();
            await _nativeOperations.PushAsync(navigationPage, page, options.Animated);
            cancellationToken.ThrowIfCancellationRequested();
            SuppressedNavigationPopFold popFold = await FoldSuppressedNavigationPopsAsync(
                _lastState,
                transaction,
                cancellationToken);
            if (popFold.HadExternalPop)
                VerifyPresentation(popFold.EffectiveState, operationId);
            else
                VerifyPresentationPush(navigationPage, previousStack, page);
            UpdateKnownNavigationPages(navigationPage);
            _lastState = popFold.EffectiveState;
            bool epochRemainedCurrent = await transaction.CommitAsync();
            _activeTransaction = null;
            PreserveHostBackReconciliation(popFold, epochRemainedCurrent);
        }
        catch (Exception presentationException)
        {
            _activeTransaction = null;
            try
            {
                await RollbackOrRecoverAsync(transaction, operationId, presentationException);
            }
            finally
            {
                DiscardSuppressedNavigationPops();
            }
            throw;
        }
        finally
        {
            _activeTransaction = null;
            _activeOperationId = previousOperationId;
            _suppressReconciliation = previousSuppressReconciliation;
            if (!previousSuppressReconciliation)
                ScheduleSuppressedNativeChangeDrain();
        }
    }

    private async Task<bool> PopPresentationPageOnMainThreadAsync(
        bool animated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfAwaitingReplacementForPresentationMutation();

        var navigationPage = ResolveTopNavigationPage(CurrentPage);
        if (navigationPage is null || !_navigationPageStackIds.ContainsKey(navigationPage))
        {
            return false;
        }

        var projection = RequireValidProjection(navigationPage.Navigation.NavigationStack);
        if (projection.Segments.Count == 0 || projection.Segments[^1].PresentationPages.Count == 0)
        {
            return false;
        }

        Page[] previousStack = navigationPage.Navigation.NavigationStack.ToArray();
        Page expectedPage = projection.Segments[^1].PresentationPages[^1];
        await using var transaction = new MauiPresentationTransaction(this, cancellationToken);
        string operationId = CreateOperationId();
        bool previousSuppressReconciliation = _suppressReconciliation;
        string? previousOperationId = _activeOperationId;
        _suppressReconciliation = true;
        _activeOperationId = operationId;
        _activeTransaction = transaction;
        transaction.Retire(expectedPage);
        try
        {
            Page? removed = await _nativeOperations.PopAsync(navigationPage, animated);
            cancellationToken.ThrowIfCancellationRequested();
            SuppressedNavigationPopFold popFold = await FoldSuppressedNavigationPopsAsync(
                _lastState,
                transaction,
                cancellationToken);
            if (popFold.HadExternalPop)
                VerifyPresentation(popFold.EffectiveState, operationId);
            else
                VerifyPresentationPop(navigationPage, previousStack, expectedPage, removed);
            UpdateKnownNavigationPages(navigationPage);
            _lastState = popFold.EffectiveState;
            bool epochRemainedCurrent = await transaction.CommitAsync();
            _activeTransaction = null;
            PreserveHostBackReconciliation(popFold, epochRemainedCurrent);
            return true;
        }
        catch (Exception presentationException)
        {
            _activeTransaction = null;
            try
            {
                await RollbackOrRecoverAsync(transaction, operationId, presentationException);
            }
            finally
            {
                DiscardSuppressedNavigationPops();
            }
            throw;
        }
        finally
        {
            _activeTransaction = null;
            _activeOperationId = previousOperationId;
            _suppressReconciliation = previousSuppressReconciliation;
            if (!previousSuppressReconciliation)
                ScheduleSuppressedNativeChangeDrain();
        }
    }

    private void ThrowIfAwaitingReplacementForPresentationMutation()
    {
        if (_hostState == MauiPresenterHostState.AwaitingReplacement)
        {
            throw new InvalidOperationException(
                "Presentation-only navigation is unavailable while the MAUI presenter awaits a replacement window.");
        }
    }

    private static void VerifyPresentationPush(
        NavigationPage navigationPage,
        IReadOnlyList<Page> previousStack,
        Page expectedPage)
    {
        IReadOnlyList<Page> currentStack = navigationPage.Navigation.NavigationStack;
        if (currentStack.Count != previousStack.Count + 1 ||
            !StackPrefixMatches(currentStack, previousStack) ||
            !ReferenceEquals(currentStack[^1], expectedPage))
        {
            throw new InvalidOperationException("Native presentation push did not produce the expected stack.");
        }

        MauiNavigationStackProjection projection = RequireValidProjection(currentStack);
        if (projection.Segments.Count == 0 ||
            projection.Segments[^1].PresentationPages.Count == 0 ||
            !ReferenceEquals(projection.Segments[^1].PresentationPages[^1], expectedPage))
        {
            throw new InvalidOperationException("Native presentation push did not produce the expected projection.");
        }
    }

    private static void VerifyPresentationPop(
        NavigationPage navigationPage,
        IReadOnlyList<Page> previousStack,
        Page expectedPage,
        Page? removedPage)
    {
        IReadOnlyList<Page> currentStack = navigationPage.Navigation.NavigationStack;
        if (!ReferenceEquals(removedPage, expectedPage) ||
            currentStack.Count != previousStack.Count - 1 ||
            !StackPrefixMatches(previousStack, currentStack))
        {
            throw new InvalidOperationException("Native presentation pop did not produce the expected stack.");
        }

        RequireValidProjection(currentStack);
    }

    private static bool StackPrefixMatches(IReadOnlyList<Page> longer, IReadOnlyList<Page> prefix)
    {
        if (longer.Count < prefix.Count)
            return false;

        for (var index = 0; index < prefix.Count; index++)
            if (!ReferenceEquals(longer[index], prefix[index]))
                return false;

        return true;
    }

    private static MauiNavigationStackProjection RequireValidProjection(IReadOnlyList<Page> pages)
    {
        var projection = MauiNavigationStackProjection.Create(pages);
        if (projection.Error is not { } error)
        {
            return projection;
        }

        throw new InvalidOperationException(
            $"Native navigation stack is invalid at page index {error.PageIndex}: {error.Message}");
    }

    private NavigationPage? ResolveTopNavigationPage(Page? page)
    {
        if (page is null)
        {
            return null;
        }

        var topModal = page.Navigation.ModalStack.LastOrDefault();
        if (topModal is not null && !ReferenceEquals(topModal, page))
        {
            return ResolveTopNavigationPage(topModal);
        }

        if (_branchHostPages.TryGetValue(page, out IMauiBranchHost? host))
            return host.SelectedBranchPage is { } selectedPage
                ? ResolveTopNavigationPage(selectedPage)
                : null;

        return page as NavigationPage;
    }

    private async Task<Page> MaterializeNodeAsync(
        NavigationNode node,
        Page? existingPage,
        string operationId,
        bool isNavigationTarget,
        CancellationToken cancellationToken,
        bool wasResurfacedTarget = false,
        MauiBranchHostPlacement placement = MauiBranchHostPlacement.Nested)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return node switch
        {
            StackNode stack => await MaterializeStackAsync(
                stack,
                existingPage as NavigationPage,
                operationId,
                isNavigationTarget,
                cancellationToken,
                wasResurfacedTarget),
            BranchHostNode branchHost => await MaterializeBranchHostAsync(
                branchHost,
                existingPage,
                operationId,
                isNavigationTarget,
                cancellationToken,
                wasResurfacedTarget,
                placement),
            ModalNode modal when modal.Content is null &&
                                existingPage is not null &&
                                StringComparer.Ordinal.Equals(GetRouteEntryId(existingPage), modal.RouteEntry.Id)
                => await UpdateReusedRouteModalPageAsync(
                    modal,
                    existingPage,
                    isNavigationTarget,
                    wasResurfacedTarget,
                    cancellationToken),
            ModalNode modal when modal.Content is null
                => await CreateRoutePageAsync(modal.RouteEntry, cancellationToken),
            ModalNode modal => await MaterializeNodeAsync(
                modal.Content!,
                existingPage,
                operationId,
                isNavigationTarget,
                cancellationToken,
                wasResurfacedTarget,
                MauiBranchHostPlacement.ModalContent),
            _ => throw new NotSupportedException($"Navigation node '{node.GetType().Name}' is not supported by the MAUI presenter.")
        };
    }

    private async Task<Page> UpdateReusedRouteModalPageAsync(
        ModalNode modal,
        Page existingPage,
        bool isNavigationTarget,
        bool wasResurfacedTarget,
        CancellationToken cancellationToken)
    {
        await UpdateRoutePageAsync(
            existingPage,
            modal.RouteEntry,
            new MauiRoutePageUpdateContext(
                ClassifyReuseKind(isNavigationTarget, wasResurfacedTarget)),
            cancellationToken);
        return existingPage;
    }

    private async Task<Page> MaterializeStackAsync(
        StackNode stack,
        NavigationPage? existingPage,
        string operationId,
        bool isNavigationTarget,
        CancellationToken cancellationToken,
        bool wasResurfacedTarget = false)
    {
        if (stack.Entries.Count == 0)
        {
            return CreateEmptyPage();
        }

        if (existingPage is not null &&
            !_branchHostPages.ContainsKey(existingPage) &&
            StringComparer.Ordinal.Equals(GetHostId(existingPage), stack.Id) &&
            StackRootMatches(existingPage, stack))
        {
            TrackNavigationPage(existingPage, stack.Id);
            await ReconcileNavigationStackAsync(
                existingPage,
                stack,
                isNavigationTarget,
                cancellationToken,
                wasResurfacedTarget);
            UpdateKnownNavigationPages(existingPage);
            return existingPage;
        }

        var root = await CreateRoutePageAsync(stack.Entries[0], cancellationToken);
        var navigationPage = new NavigationPage(root);
        RegisterPage(navigationPage);
        _activeTransaction?.TrackCreated(navigationPage);
        SetHostId(navigationPage, stack.Id);
        SetRouteEntryId(root, stack.Entries[0].Id);
        WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageCreated, navigationPage, "NavigationPage was created.");
        TrackNavigationPage(navigationPage, stack.Id);

        for (var i = 1; i < stack.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await CreateRoutePageAsync(stack.Entries[i], cancellationToken);
            await _nativeOperations.PushAsync(navigationPage, page, animated: false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        UpdateKnownNavigationPages(navigationPage);
        return navigationPage;
    }

    private async Task ReconcileNavigationStackAsync(
        NavigationPage navigationPage,
        StackNode stack,
        bool isNavigationTarget,
        CancellationToken cancellationToken,
        bool wasResurfacedTarget = false)
    {
        var currentStack = navigationPage.Navigation.NavigationStack;
        var currentProjection = RequireValidProjection(currentStack);
        var previousStackCount = currentProjection.Segments.Count;
        var commonCount = CommonRoutePrefix(currentProjection.Segments, stack.Entries);
        var retainedNativePageCount = currentProjection.NativePageCountForSegmentPrefix(commonCount);
        var nativePopCount = currentStack.Count - retainedNativePageCount;
        var replacementPages = new List<Page>(Math.Max(0, stack.Entries.Count - commonCount));
        for (var i = commonCount; i < stack.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            replacementPages.Add(await CreateRoutePageAsync(stack.Entries[i], cancellationToken));
        }

        bool animatePop = _activePresentationOperation?.ResolveAnimated(
            MauiPresentationOperationKind.StackPop,
            stack.Id,
            isNavigationTarget && nativePopCount == 1 && replacementPages.Count == 0) == true;
        while (navigationPage.Navigation.NavigationStack.Count > retainedNativePageCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var removed = await _nativeOperations.PopAsync(navigationPage, animatePop);
            cancellationToken.ThrowIfCancellationRequested();
            animatePop = false;
            if (removed is not null)
            {
                await DetachPageTreeAsync(removed);
            }
        }

        bool animatePush = _activePresentationOperation?.ResolveAnimated(
            MauiPresentationOperationKind.StackPush,
            stack.Id,
            isNavigationTarget && nativePopCount == 0 && replacementPages.Count == 1) == true;
        foreach (Page page in replacementPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _nativeOperations.PushAsync(navigationPage, page, animatePush);
            cancellationToken.ThrowIfCancellationRequested();
            animatePush = false;
        }

        var updatedProjection = RequireValidProjection(navigationPage.Navigation.NavigationStack);
        await UpdateReusedStackPagesAsync(
            updatedProjection.Segments,
            stack.Entries,
            commonCount,
            isNavigationTarget,
            previousStackCount,
            wasResurfacedTarget,
            cancellationToken);
        UpdateKnownNavigationPages(navigationPage);
    }

    private static bool StackRootMatches(NavigationPage navigationPage, StackNode stack)
    {
        var projection = MauiNavigationStackProjection.Create(navigationPage.Navigation.NavigationStack);
        return projection.IsValid &&
               projection.Segments.Count > 0 &&
               stack.Entries.Count > 0 &&
               StringComparer.Ordinal.Equals(projection.Segments[0].RouteEntryId, stack.Entries[0].Id);
    }

    private static int CommonRoutePrefix(
        IReadOnlyList<MauiNavigationStackSegment> segments,
        IReadOnlyList<RouteEntry> entries)
    {
        var count = Math.Min(segments.Count, entries.Count);
        var common = 0;
        for (var i = 0; i < count; i++)
        {
            if (!StringComparer.Ordinal.Equals(segments[i].RouteEntryId, entries[i].Id))
            {
                break;
            }

            common++;
        }

        return common;
    }

    private async Task<Page> MaterializeBranchHostAsync(
        BranchHostNode branchHost,
        Page? existingPage,
        string operationId,
        bool isNavigationTarget,
        CancellationToken cancellationToken,
        bool wasResurfacedTarget,
        MauiBranchHostPlacement placement)
    {
        MauiBranchHostFactorySelection selection = ResolveBranchHostFactory(branchHost.Id);
        IMauiBranchHost? host = null;
        if (existingPage is not null &&
            _branchHostPages.TryGetValue(existingPage, out IMauiBranchHost? existingHost) &&
            _branchHostFactories.TryGetValue(existingPage, out IMauiBranchHostFactory? existingFactory) &&
            StringComparer.Ordinal.Equals(GetHostId(existingPage), branchHost.Id) &&
            ReferenceEquals(existingFactory, selection.Factory))
        {
            host = existingHost;
        }

        if (host is null)
        {
            if (existingPage is not null && _branchHostPages.ContainsKey(existingPage))
                _activeTransaction?.Retire(existingPage);

            MauiNativeTreeEpoch creationEpoch = _nativeTreeEpoch;
            var creationContext = new MauiBranchHostCreationContext(
                branchHost,
                placement,
                _activeNavigationPresentationContext ?? throw new InvalidOperationException(
                    "A branch-host was materialized without presentation context."),
                _services);
            host = await InvokeExtensionPointAsync(
                creationEpoch,
                async token =>
                {
                    IMauiBranchHost created = await selection.Factory.CreateAsync(creationContext, token);
                    ArgumentNullException.ThrowIfNull(created);
                    return created;
                },
                CaptureLateBranchHostAbandonment,
                cancellationToken);

            if (host.Page is null)
                throw new InvalidOperationException("A MAUI branch-host factory returned a null page.");

            if (host is IMauiBranchHostNativeOperations nativeAware)
                nativeAware.SetNativeOperations(_nativeOperations);

            RegisterPage(host.Page);
            _branchHostPages[host.Page] = host;
            _branchHostFactories[host.Page] = selection.Factory;
            if (!ReferenceEquals(host.Page, existingPage))
            {
                _activeTransaction?.TrackCreated(host.Page);
                WritePageLifecycle(
                    NavigationDiagnosticEventKind.PresentationPageCreated,
                    host.Page,
                    "Branch-host page was created.");
            }
        }

        SetHostId(host.Page, branchHost.Id);
        TrackBranchHost(host);
        MauiBranchHostBranch[] hostBranches = host.Branches.ToArray();
        var stagedBranches = new List<MauiBranchHostBranch>(branchHost.Branches.Count);
        const MauiBranchHostPlacement childPlacement = MauiBranchHostPlacement.Nested;
        foreach (NavigationBranch branch in branchHost.Branches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Page? existingBranchPage = hostBranches.FirstOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.Id, branch.Id))?.Page;
            Page page = await MaterializeNodeAsync(
                branch.Content,
                existingBranchPage,
                operationId,
                isNavigationTarget && StringComparer.Ordinal.Equals(branch.Id, branchHost.SelectedBranchId),
                cancellationToken,
                wasResurfacedTarget,
                childPlacement);
            ApplyBranchChrome(page, branch);
            SetBranchId(page, branch.Id);
            stagedBranches.Add(new MauiBranchHostBranch(branch.Id, branch.Title, page));
        }

        if (_activeTransaction is { } activeTransaction)
        {
            foreach (MauiBranchHostBranch previousBranch in hostBranches)
            {
                if (!stagedBranches.Any(current => ReferenceEquals(current.Page, previousBranch.Page)))
                    activeTransaction.Retire(previousBranch.Page);
            }
        }

        MauiNativeTreeEpoch updateEpoch = EpochFor(host.Page);
        var updateContext = new MauiBranchHostUpdateContext(
            branchHost,
            placement,
            stagedBranches,
            branchHost.SelectedBranchId,
            _activeNavigationPresentationContext ?? throw new InvalidOperationException(
                "A branch-host was updated without presentation context."));
        IMauiBranchHostUpdate update = await InvokeExtensionPointAsync(
            updateEpoch,
            async token =>
            {
                IMauiBranchHostUpdate applied = await host.ApplyAsync(updateContext, token);
                ArgumentNullException.ThrowIfNull(applied);
                return applied;
            },
            CaptureLateBranchHostUpdateAbandonment,
            cancellationToken);

        if (_activeTransaction is { } transaction)
        {
            transaction.TrackBranchHostUpdate(update);
            VerifyAppliedBranchPages(host, stagedBranches);
        }
        else
        {
            try
            {
                VerifyAppliedBranchPages(host, stagedBranches);
                await InvokeExtensionPointAsync(
                    updateEpoch,
                    token => update.CommitAsync(token),
                    cancellationToken);
            }
            catch
            {
                // Rolling back into a destroyed tree is worse than abandoning the update with its epoch.
                if (EpochRemainsCurrent(updateEpoch))
                {
                    try
                    {
                        await InvokeExtensionPointAsync(
                            updateEpoch,
                            token => update.RollbackAsync(token),
                            cancellationToken);
                    }
                    catch (MauiNativeTreeInvalidatedException)
                    {
                    }
                }

                throw;
            }
            finally
            {
                await update.DisposeAsync();
            }
        }
        return host.Page;
    }

    private static void VerifyAppliedBranchPages(
        IMauiBranchHost host,
        IReadOnlyList<MauiBranchHostBranch> stagedBranches)
    {
        IReadOnlyList<MauiBranchHostBranch> presentedBranches = host.Branches;
        if (presentedBranches.Count != stagedBranches.Count)
        {
            throw new InvalidOperationException(
                $"Branch host '{GetHostId(host.Page) ?? host.Page.GetType().Name}' did not retain the supplied branch pages: " +
                $"expected {stagedBranches.Count} branches but observed {presentedBranches.Count}.");
        }

        for (var index = 0; index < stagedBranches.Count; index++)
        {
            MauiBranchHostBranch staged = stagedBranches[index];
            MauiBranchHostBranch presented = presentedBranches[index];
            if (!StringComparer.Ordinal.Equals(presented.Id, staged.Id) ||
                !ReferenceEquals(presented.Page, staged.Page))
            {
                throw new InvalidOperationException(
                    $"Branch host '{GetHostId(host.Page) ?? host.Page.GetType().Name}' did not retain the supplied page " +
                    $"for branch '{staged.Id}' at index {index}.");
            }
        }
    }

    private MauiBranchHostFactorySelection ResolveBranchHostFactory(string branchHostId)
    {
        if (_presentationOptions.TryGetBranchHost(branchHostId, out MauiBranchHostRegistration? registration))
            return new MauiBranchHostFactorySelection(registration.Factory);

        return new MauiBranchHostFactorySelection(_defaultBranchHostFactory);
    }

    private sealed record MauiBranchHostFactorySelection(IMauiBranchHostFactory Factory);

    private static void ApplyBranchChrome(Page page, NavigationBranch branch)
    {
        page.Title = branch.Title;

        if (page is NavigationPage navigationPage &&
            navigationPage.Navigation.NavigationStack.Count > 0)
        {
            navigationPage.IconImageSource = navigationPage.Navigation.NavigationStack[0].IconImageSource;
        }
    }

    private async Task ApplyModalsAsync(
        Page root,
        IReadOnlyList<ModalNode> modals,
        string operationId,
        CancellationToken cancellationToken)
    {
        var modalStack = root.Navigation.ModalStack;
        var previousModalCount = modalStack.Count;
        var commonCount = CommonModalPrefix(modalStack, modals);
        var modalPopCount = modalStack.Count - commonCount;
        var modalPushCount = modals.Count - commonCount;
        var replacementModals = new List<Page>(Math.Max(0, modals.Count - commonCount));
        for (var i = commonCount; i < modals.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Page modalPage = modals[i].Content is null
                ? await CreateRoutePageAsync(modals[i].RouteEntry, cancellationToken)
                : await MaterializeNodeAsync(
                    modals[i].Content!,
                    null,
                    operationId,
                    isNavigationTarget: i == modals.Count - 1,
                    cancellationToken,
                    placement: MauiBranchHostPlacement.ModalContent);
            SetModalId(modalPage, modals[i].Id);
            TrackModalPage(modalPage);
            replacementModals.Add(modalPage);
        }

        string? poppedModalId = modalPopCount == 1
            ? GetModalId(root.Navigation.ModalStack[^1])
            : null;
        bool animatePop = poppedModalId is not null &&
            _activePresentationOperation?.ResolveAnimated(
                MauiPresentationOperationKind.ModalPop,
                poppedModalId,
                modalPopCount == 1 && modalPushCount == 0) == true;
        while (root.Navigation.ModalStack.Count > commonCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var removed = await _nativeOperations.PopModalAsync(root, animatePop);
            cancellationToken.ThrowIfCancellationRequested();
            animatePop = false;
            if (removed is not null)
            {
                await DetachPageTreeAsync(removed);
            }
        }

        bool animatePush = replacementModals.Count == 1 &&
            _activePresentationOperation?.ResolveAnimated(
                MauiPresentationOperationKind.ModalPush,
                GetModalId(replacementModals[0])!,
                modalPopCount == 0 && modalPushCount == 1) == true;
        foreach (Page modalPage in replacementModals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _nativeOperations.PushModalAsync(root, modalPage, animatePush);
            cancellationToken.ThrowIfCancellationRequested();
            animatePush = false;
        }

        await UpdateReusedModalPagesAsync(
            root.Navigation.ModalStack,
            modals,
            commonCount,
            previousModalCount,
            operationId,
            cancellationToken);
    }

    private int CommonModalPrefix(IReadOnlyList<Page> pages, IReadOnlyList<ModalNode> modals)
    {
        var count = Math.Min(pages.Count, modals.Count);
        var common = 0;
        for (var i = 0; i < count; i++)
        {
            if (!ModalPageMatches(pages[i], modals[i]))
            {
                break;
            }

            common++;
        }

        return common;
    }

    private bool ModalPageMatches(Page page, ModalNode modal)
    {
        if (!StringComparer.Ordinal.Equals(GetModalId(page), modal.Id))
        {
            return false;
        }

        return modal.Content is null
            ? StringComparer.Ordinal.Equals(GetRouteEntryId(page), modal.RouteEntry.Id)
            : CanReuseNodePage(modal.Content, page);
    }

    private bool CanReuseNodePage(NavigationNode node, Page? existingPage)
    {
        return node switch
        {
            StackNode stack => existingPage is NavigationPage navigationPage &&
                               !_branchHostPages.ContainsKey(existingPage) &&
                               StringComparer.Ordinal.Equals(GetHostId(navigationPage), stack.Id) &&
                               StackRootMatches(navigationPage, stack),
            BranchHostNode branchHost => existingPage is not null &&
                                         _branchHostPages.TryGetValue(existingPage, out IMauiBranchHost? host) &&
                                         ReferenceEquals(host.Page, existingPage) &&
                                         StringComparer.Ordinal.Equals(GetHostId(existingPage), branchHost.Id),
            ModalNode modal => modal.Content is null
                ? existingPage is not null &&
                  StringComparer.Ordinal.Equals(GetRouteEntryId(existingPage), modal.RouteEntry.Id)
                : CanReuseNodePage(modal.Content, existingPage),
            _ => false
        };
    }

    private async ValueTask<Page> CreateRoutePageAsync(
        RouteEntry entry,
        CancellationToken cancellationToken)
    {
        MauiNativeTreeEpoch creationEpoch = _nativeTreeEpoch;
        Page page = await InvokeExtensionPointAsync(
            creationEpoch,
            token => _pageFactory.CreatePageAsync(entry, token),
            CaptureLateAbandonment,
            cancellationToken);

        RegisterPage(page);
        _activeTransaction?.TrackCreated(page);
        SetRouteEntryId(page, entry.Id);
        WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageCreated, page, "Route page was created.");
        return page;
    }

    private void CaptureLateAbandonment(Page page)
    {
        MauiPageAbandonment? abandonment = CaptureAbandonmentOrDiagnose(page);
        if (abandonment is not null)
            _abandonmentCleanup.EnqueueAfter(WaitForPresentationIdleAsync(), [abandonment]);
    }

    private void CaptureLateBranchHostAbandonment(IMauiBranchHost host)
    {
        MauiPageAbandonment? abandonment = CaptureBranchHostAbandonmentOrDiagnose(host);
        if (abandonment is not null)
            _abandonmentCleanup.EnqueueAfter(WaitForPresentationIdleAsync(), [abandonment]);
    }

    private MauiPageAbandonment? CaptureBranchHostAbandonmentOrDiagnose(IMauiBranchHost host)
    {
        string hostTypeName = host.GetType().FullName ?? host.GetType().Name;
        if (!TryClaimBranchHostRelease(host))
            return null;

        return CaptureAsyncDisposableAbandonmentOrDiagnose(host, hostTypeName);
    }

    private void CaptureLateBranchHostUpdateAbandonment(IMauiBranchHostUpdate update)
    {
        string updateTypeName = update.GetType().FullName ?? update.GetType().Name;
        MauiPageAbandonment? abandonment = CaptureAsyncDisposableAbandonmentOrDiagnose(update, updateTypeName);
        if (abandonment is not null)
            _abandonmentCleanup.EnqueueAfter(WaitForPresentationIdleAsync(), [abandonment]);
    }

    private MauiPageAbandonment? CaptureAsyncDisposableAbandonmentOrDiagnose(
        IAsyncDisposable resource,
        string resourceTypeName)
    {
        try
        {
            ValueTask disposal = resource.DisposeAsync();
            if (disposal.IsCompletedSuccessfully)
            {
                disposal.GetAwaiter().GetResult();
                return null;
            }

            return new MauiPageAbandonment(
                new StartedAsyncDisposal(disposal.AsTask()),
                resourceTypeName);
        }
        catch (Exception exception)
        {
            WritePageReleaseFailure(null, exception);
            return null;
        }
    }

    private ValueTask DisposeBranchHostAsync(IMauiBranchHost host) =>
        TryClaimBranchHostRelease(host)
            ? host.DisposeAsync()
            : ValueTask.CompletedTask;

    private bool TryClaimBranchHostRelease(IMauiBranchHost host)
    {
        lock (_releaseGate)
        {
            if (_releasedBranchHosts.TryGetValue(host, out _))
                return false;

            _releasedBranchHosts.Add(host, new ReleasedBranchHostMarker());
            return true;
        }
    }

    private MauiPageAbandonment? CaptureAbandonmentOrDiagnose(Page page)
    {
        try
        {
            return _pageFactory.CaptureAbandonment(page);
        }
        catch (Exception exception)
        {
            WritePageReleaseFailure(page, exception);
            return null;
        }
    }

    private async ValueTask UpdateReusedStackPagesAsync(
        IReadOnlyList<MauiNavigationStackSegment> segments,
        IReadOnlyList<RouteEntry> entries,
        int commonCount,
        bool isNavigationTarget,
        int previousStackCount,
        bool wasResurfacedTarget,
        CancellationToken cancellationToken)
    {
        var count = Math.Min(commonCount, Math.Min(segments.Count, entries.Count));
        for (var i = 0; i < count; i++)
        {
            await UpdateRoutePageAsync(
                segments[i].RoutePage,
                entries[i],
                new MauiRoutePageUpdateContext(
                    ClassifyReuseKind(
                        isNavigationTarget && i == entries.Count - 1,
                        wasResurfacedTarget || previousStackCount > entries.Count)),
                cancellationToken);
        }
    }

    private async Task UpdateReusedModalPagesAsync(
        IReadOnlyList<Page> pages,
        IReadOnlyList<ModalNode> modals,
        int commonCount,
        int previousModalCount,
        string operationId,
        CancellationToken cancellationToken)
    {
        var count = Math.Min(commonCount, Math.Min(pages.Count, modals.Count));
        for (var i = 0; i < count; i++)
        {
            if (modals[i].Content is null)
            {
                await UpdateRoutePageAsync(
                    pages[i],
                    modals[i].RouteEntry,
                    new MauiRoutePageUpdateContext(
                        ClassifyReuseKind(
                            i == modals.Count - 1,
                            previousModalCount > modals.Count)),
                    cancellationToken);
                continue;
            }

            await MaterializeNodeAsync(
                modals[i].Content!,
                pages[i],
                operationId,
                isNavigationTarget: i == modals.Count - 1,
                cancellationToken,
                wasResurfacedTarget: previousModalCount > modals.Count,
                placement: MauiBranchHostPlacement.ModalContent);
        }
    }

    private static MauiRoutePageReuseKind ClassifyReuseKind(bool isNavigationTarget, bool wasResurfaced)
    {
        if (!isNavigationTarget)
        {
            return MauiRoutePageReuseKind.NonTargetReuse;
        }

        return wasResurfaced
            ? MauiRoutePageReuseKind.ResurfacedTarget
            : MauiRoutePageReuseKind.ExplicitTarget;
    }

    private async ValueTask UpdateRoutePageAsync(
        Page page,
        RouteEntry entry,
        MauiRoutePageUpdateContext context,
        CancellationToken cancellationToken)
    {
        MauiNativeTreeEpoch updateEpoch = EpochFor(page);
        _activeTransaction?.RecordUpdate(page);
        SetRouteEntryId(page, entry.Id);
        await InvokeExtensionPointAsync(
            updateEpoch,
            token => _pageFactory.UpdatePageAsync(page, entry, context, token),
            cancellationToken);
    }

    private Page CreateOrReuseEmptyRootHost(Page? existingPage)
    {
        return existingPage is not null &&
               existingPage is not NavigationPage &&
               !_branchHostPages.ContainsKey(existingPage)
            ? existingPage
            : CreateEmptyPage();
    }

    private Page CreateEmptyPage()
    {
        var page = new ContentPage
        {
            Title = "Empty",
            Content = new Grid()
        };
        RegisterPage(page);
        _activeTransaction?.TrackCreated(page);
        WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageCreated, page, "Empty page was created.");
        return page;
    }

    private async ValueTask SetCurrentPageAsync(Page? page)
    {
        if (ReferenceEquals(CurrentPage, page))
        {
            SetAttachedWindowPage(page);

            return;
        }

        var previous = CurrentPage;
        CurrentPage = page;

        SetAttachedWindowPage(page);
        _activeTransaction?.RecordRootChange();
        if (_activeTransaction is null)
            InvokeRootPageChanged(page);

        if (previous is not null && !ReferenceEquals(previous, page))
        {
            if (_activeTransaction is not null)
                _activeTransaction.Retire(previous);
            else
                await DetachPageTreeAsync(previous);
        }
    }

    private void SetAttachedWindowPage(Page? page)
    {
        if (_attachedWindow is null || ReferenceEquals(_attachedWindow.Page, page))
        {
            return;
        }

        _nativeOperations.SetWindowPage(_attachedWindow, page);
    }

    private void InvokeRootPageChanged(Page? page)
    {
        EventHandler<Page?>? handlers = RootPageChanged;
        if (handlers is null)
            return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            if (handler is not EventHandler<Page?> typedHandler)
                continue;

            try
            {
                typedHandler(this, page);
            }
            catch (Exception ex)
            {
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.DiagnosticObserverFailed,
                    LifecycleOperationId(),
                    "A root-page observer failed.",
                    new Dictionary<string, object?>
                    {
                        [NavigationDiagnosticDataKeys.ExceptionType] = ex.GetType().FullName,
                        [NavigationDiagnosticDataKeys.ExceptionMessage] = ex.Message
                    },
                    phase: NavigationDiagnosticPhase.Diagnostics);
            }
        }
    }

    private Page? ResolveTopPresentedPage(Page? page)
    {
        var current = page;

        while (current is not null)
        {
            var topModal = current.Navigation.ModalStack.LastOrDefault();
            if (topModal is not null && !ReferenceEquals(topModal, current))
            {
                current = topModal;
                continue;
            }

            Page? next = _branchHostPages.TryGetValue(current, out IMauiBranchHost? host)
                ? host.SelectedBranchPage
                : current is NavigationPage navigationPage
                    ? navigationPage.CurrentPage
                    : null;

            if (next is null || ReferenceEquals(next, current))
            {
                return current;
            }

            current = next;
        }

        return null;
    }

    private void UpdateKnownNavigationPages(NavigationPage navigationPage)
    {
        _navigationPageKnownPages[navigationPage] = navigationPage.Navigation.NavigationStack
            .ToArray();
    }

    private async ValueTask ReleaseNavigationPagesRemovedFromNativeStackAsync(NavigationPage navigationPage)
    {
        var currentPages = navigationPage.Navigation.NavigationStack
            .ToHashSet(ReferenceEqualityComparer.Instance);

        if (_navigationPageKnownPages.TryGetValue(navigationPage, out var knownPages))
        {
            foreach (var removedPage in knownPages
                         .Where(page => !currentPages.Contains(page))
                         .Reverse()
                         .ToArray())
            {
                await DetachPageTreeAsync(removedPage);
            }
        }

        _navigationPageKnownPages[navigationPage] = navigationPage.Navigation.NavigationStack.ToArray();
    }

    private void TrackNavigationPage(NavigationPage navigationPage, string stackId)
    {
        var wasTracked = _navigationPageStackIds.ContainsKey(navigationPage);
        _navigationPageStackIds[navigationPage] = stackId;
        navigationPage.Popped -= OnNavigationPagePopped;
        navigationPage.Popped += OnNavigationPagePopped;
        navigationPage.PoppedToRoot -= OnNavigationPagePoppedToRoot;
        navigationPage.PoppedToRoot += OnNavigationPagePoppedToRoot;
        UpdateKnownNavigationPages(navigationPage);

        if (!wasTracked)
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerAttached,
                navigationPage,
                "NavigationPage.Popped/PoppedToRoot",
                "Navigation stack handlers were attached.");
        }
    }

    private void UntrackNavigationPage(NavigationPage navigationPage)
    {
        var wasTracked = _navigationPageStackIds.Remove(navigationPage);
        _navigationPageKnownPages.Remove(navigationPage);
        navigationPage.Popped -= OnNavigationPagePopped;
        navigationPage.PoppedToRoot -= OnNavigationPagePoppedToRoot;

        if (wasTracked)
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerDetached,
                navigationPage,
                "NavigationPage.Popped/PoppedToRoot",
                "Navigation stack handlers were detached.");
        }
    }

    private void TrackBranchHost(IMauiBranchHost host)
    {
        if (!_branchHostPages.ContainsKey(host.Page))
            _branchHostPages[host.Page] = host;

        host.SelectionChanged -= OnBranchHostSelectionChanged;
        host.SelectionChanged += OnBranchHostSelectionChanged;
        if (_trackedBranchHosts.Add(host))
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerAttached,
                host.Page,
                "IMauiBranchHost.SelectionChanged",
                "Branch-host selection handler was attached.");
        }
    }

    private void UntrackBranchHost(IMauiBranchHost host)
    {
        host.SelectionChanged -= OnBranchHostSelectionChanged;
        if (_trackedBranchHosts.Remove(host))
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerDetached,
                host.Page,
                "IMauiBranchHost.SelectionChanged",
                "Branch-host selection handler was detached.");
        }
    }

    private void OnBranchHostSelectionChanged(
        object? sender,
        MauiBranchHostSelectionChangedEventArgs e)
    {
        if (sender is not IMauiBranchHost host ||
            !StringComparer.Ordinal.Equals(host.SelectedBranchId, e.BranchId))
            return;

        if (!_pageEpochs.TryGetValue(host.Page, out MauiNativeTreeEpoch? epoch))
            return;

        epoch.PendingBranchHostSelections[host] = e.BranchId;
        if (!_suppressReconciliation)
            ScheduleSuppressedNativeChangeDrain();
    }

    private Task ReconcileBranchHostSelectionAsync(IMauiBranchHost host, string branchId)
    {
        if (_suppressReconciliation ||
            !_branchHostPages.TryGetValue(host.Page, out IMauiBranchHost? trackedHost) ||
            !ReferenceEquals(trackedHost, host) ||
            host.Branches.FirstOrDefault(branch =>
                StringComparer.Ordinal.Equals(branch.Id, branchId)) is not { } branch)
        {
            return Task.CompletedTask;
        }

        string? branchHostId = GetHostId(host.Page);
        if (string.IsNullOrWhiteSpace(branchHostId))
            return Task.CompletedTask;

        var updatedWindow = UpdateWindowForPresentedNode(
            host.Page,
            node => UpdateBranchHostSelection(node, branchHostId, branchId));
        if (updatedWindow is not null)
        {
            RequestReconciliation(
                _lastState.ReplaceWindow(updatedWindow),
                NavigationReconciliationSource.BranchChanged,
                "Native branch-host selection changed.");
        }

        return Task.CompletedTask;
    }

    private void TrackModalPage(Page modalPage)
    {
        modalPage.Disappearing -= OnModalPageDisappearing;
        modalPage.Disappearing += OnModalPageDisappearing;

        if (_trackedModalPages.Add(modalPage))
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerAttached,
                modalPage,
                "Page.Disappearing",
                "Modal dismissal handler was attached.");
        }
    }

    private void UntrackModalPage(Page modalPage)
    {
        modalPage.Disappearing -= OnModalPageDisappearing;

        if (_trackedModalPages.Remove(modalPage))
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerDetached,
                modalPage,
                "Page.Disappearing",
                "Modal dismissal handler was detached.");
        }
    }

    private ValueTask DetachPageTreeAsync(Page page)
    {
        if (_activeTransaction is not null)
        {
            _activeTransaction.Retire(page);
            return ValueTask.CompletedTask;
        }

        return DetachPageTreeWithFailuresAsync(page);
    }

    private ValueTask DetachPageTreeWithFailuresAsync(Page page) =>
        DetachPageTreeWithFailuresCoreAsync(page, requiredEpoch: null);

    private ValueTask DetachPageTreeWithFailuresForEpochAsync(
        Page page,
        MauiNativeTreeEpoch requiredEpoch) =>
        DetachPageTreeWithFailuresCoreAsync(page, requiredEpoch);

    private async ValueTask DetachPageTreeWithFailuresCoreAsync(
        Page page,
        MauiNativeTreeEpoch? requiredEpoch)
    {
        if (requiredEpoch is null)
            _pageEpochs.TryGetValue(page, out requiredEpoch);

        var failures = new List<Exception>();
        await DetachPageTreeAsync(
            page,
            new HashSet<Page>(ReferenceEqualityComparer.Instance),
            failures,
            requiredEpoch);
        if (failures.Count > 0)
            throw new AggregateException("One or more pages could not be fully released.", failures);
    }

    private async ValueTask DetachPageTreeAsync(
        Page page,
        HashSet<Page> visited,
        List<Exception> failures,
        MauiNativeTreeEpoch? requiredEpoch = null)
    {
        if (!CanContinuePageRelease(requiredEpoch))
            return;

        if (!visited.Add(page))
        {
            return;
        }

        var shouldRelease = MarkPageReleased(page);
        var pageReleaseSucceeded = shouldRelease;
        if (shouldRelease)
        {
            try
            {
                page.BindingContext = null;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                pageReleaseSucceeded = false;
            }
        }

        UntrackModalPage(page);

        foreach (var modalPage in page.Navigation.ModalStack.ToArray())
        {
            if (!ReferenceEquals(modalPage, page))
            {
                await DetachPageTreeAsync(modalPage, visited, failures, requiredEpoch);
                if (!CanContinuePageRelease(requiredEpoch))
                    return;
            }
        }

        if (_branchHostPages.Remove(page, out IMauiBranchHost? branchHost) && branchHost is not null)
        {
            _branchHostFactories.Remove(page);
            _pendingBranchHostSelections.Remove(branchHost);
            UntrackBranchHost(branchHost);
            Page[] branchPages = branchHost.Branches
                .Select(static branch => branch.Page)
                .ToArray();
            foreach (Page branchPage in branchPages)
            {
                await DetachPageTreeAsync(branchPage, visited, failures, requiredEpoch);
                if (!CanContinuePageRelease(requiredEpoch))
                    return;
            }

            try
            {
                await DisposeBranchHostAsync(branchHost);
                if (!CanContinuePageRelease(requiredEpoch))
                    return;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                pageReleaseSucceeded = false;
            }

            if (pageReleaseSucceeded)
                ForgetPage(page);
            return;
        }

        switch (page)
        {
            case NavigationPage navigationPage:
                UntrackNavigationPage(navigationPage);
                foreach (var child in navigationPage.Navigation.NavigationStack.Reverse().ToArray())
                {
                    await DetachPageTreeAsync(child, visited, failures, requiredEpoch);
                    if (!CanContinuePageRelease(requiredEpoch))
                        return;
                }

                break;
            default:
                if (shouldRelease)
                {
                    try
                    {
                        if (requiredEpoch is null)
                        {
                            if (IsPresentationPage(page))
                                await _pageFactory.ReleasePresentationPageAsync(page, CancellationToken.None);
                            else
                                await _pageFactory.ReleasePageAsync(page, CancellationToken.None);
                        }
                        else
                        {
                            await InvokeReleaseAcrossBoundaryAsync(
                                requiredEpoch,
                                token => IsPresentationPage(page)
                                    ? _pageFactory.ReleasePresentationPageAsync(page, token)
                                    : _pageFactory.ReleasePageAsync(page, token),
                                CancellationToken.None);
                        }

                        if (!CanContinuePageRelease(requiredEpoch))
                            return;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                        pageReleaseSucceeded = false;
                    }
                }

                break;
        }

        if (pageReleaseSucceeded)
            ForgetPage(page);
    }

    private bool CanContinuePageRelease(MauiNativeTreeEpoch? requiredEpoch) =>
        requiredEpoch is null || EpochRemainsCurrent(requiredEpoch);

    private bool MarkPageReleased(Page page)
    {
        lock (_releaseGate)
        {
            if (_releasedPages.TryGetValue(page, out _))
                return false;

            _releasedPages.Add(page, new ReleasedPageMarker());
        }

        WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageReleased, page, "Page was released.");
        return true;
    }

    private void OnNavigationPagePopped(object? sender, NavigationEventArgs e)
    {
        if (sender is not NavigationPage navigationPage)
        {
            return;
        }

        if (!_navigationPageStackIds.TryGetValue(navigationPage, out var stackId))
        {
            return;
        }

        if (_suppressReconciliation)
        {
            CaptureSuppressedNavigationPop(navigationPage, stackId, e.Page);
            return;
        }

        MauiNativeTreeEpoch epoch = EpochFor(navigationPage);
        QueueNativeCleanupForEpoch(epoch, async () =>
        {
            await ReleaseNavigationPagesRemovedFromNativeStackAsync(navigationPage);
            ReconcileStackFromNative(stackId, navigationPage);
        });
    }

    private void OnNavigationPagePoppedToRoot(object? sender, NavigationEventArgs e)
    {
        OnNavigationPagePopped(sender, e);
    }

    private void CaptureSuppressedNavigationPop(
        NavigationPage navigationPage,
        string stackId,
        Page poppedPage)
    {
        Page[] remainingPages = navigationPage.Navigation.NavigationStack.ToArray();
        if (_suppressedNavigationPops.TryGetValue(navigationPage, out SuppressedNavigationPop? existing) &&
            StringComparer.Ordinal.Equals(existing.StackId, stackId))
        {
            Page[] updatedKnownPages = existing.KnownPages.Any(page => ReferenceEquals(page, poppedPage))
                ? existing.KnownPages.ToArray()
                : [.. existing.KnownPages, poppedPage];
            _suppressedNavigationPops[navigationPage] = existing with
            {
                KnownPages = updatedKnownPages,
                RemainingPages = remainingPages
            };
            return;
        }

        Page[] knownPages;
        if (_navigationPageKnownPages.TryGetValue(navigationPage, out IReadOnlyList<Page>? known))
        {
            knownPages = known.ToArray();
        }
        else
        {
            knownPages = [.. remainingPages, poppedPage];
        }

        if (!knownPages.Any(page => ReferenceEquals(page, poppedPage)))
            knownPages = [.. knownPages, poppedPage];

        _suppressedNavigationPops[navigationPage] = new SuppressedNavigationPop(
            navigationPage,
            _attachedWindowId ?? _lastState.ActiveWindowId,
            stackId,
            FindOwningModalId(navigationPage),
            ReferenceEquals(ResolveTopNavigationPage(CurrentPage), navigationPage),
            knownPages,
            remainingPages);
    }

    private async Task<SuppressedNavigationPopFold> FoldSuppressedNavigationPopsAsync(
        NavigationState initialState,
        MauiPresentationTransaction? transaction,
        CancellationToken cancellationToken)
    {
        NavigationState effectiveState = initialState;
        var hadExternalPop = false;
        var logicalStateChanged = false;
        AppRoute? route = null;

        while (_suppressedNavigationPops.Count > 0)
        {
            SuppressedNavigationPop[] pendingPops = _suppressedNavigationPops.Values.ToArray();
            _suppressedNavigationPops.Clear();

            foreach (SuppressedNavigationPop pendingPop in pendingPops)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SuppressedNavigationPopFold popFold = await FoldSuppressedNavigationPopAsync(
                    effectiveState,
                    pendingPop,
                    transaction,
                    cancellationToken);
                effectiveState = popFold.EffectiveState;
                hadExternalPop |= popFold.HadExternalPop;
                if (popFold.LogicalStateChanged)
                {
                    logicalStateChanged = true;
                    route = popFold.Route;
                }
            }
        }

        return new SuppressedNavigationPopFold(
            effectiveState,
            hadExternalPop,
            logicalStateChanged,
            route);
    }

    private async Task<SuppressedNavigationPopFold> FoldSuppressedNavigationPopAsync(
        NavigationState effectiveState,
        SuppressedNavigationPop pendingPop,
        MauiPresentationTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (!await RetireOrReleaseSuppressedPopPagesAsync(pendingPop, transaction))
            return new SuppressedNavigationPopFold(effectiveState, false, false, null);

        if (!TryResolveCapturedNavigationHost(
                effectiveState,
                pendingPop,
                out WindowNode? window,
                out StackNode? targetStack))
        {
            return new SuppressedNavigationPopFold(effectiveState, true, false, null);
        }

        MauiNavigationStackProjection remainingProjection =
            MauiNavigationStackProjection.Create(pendingPop.RemainingPages);
        if (remainingProjection.Error is { } projectionError)
        {
            WriteSuppressedPopProjectionFailure(projectionError);
            return new SuppressedNavigationPopFold(effectiveState, true, false, null);
        }

        string[] remainingRouteEntryIds = remainingProjection.Segments
            .Select(static segment => segment.RouteEntryId)
            .ToArray();
        string[] targetRouteEntryIds = targetStack.Entries
            .Select(static entry => entry.Id)
            .ToArray();
        bool changesLogicalState = !targetRouteEntryIds.SequenceEqual(
            remainingRouteEntryIds,
            StringComparer.Ordinal);

        WindowNode effectiveWindow = window;
        if (changesLogicalState)
        {
            WindowNode? updatedWindow = UpdateWindowContent(
                window,
                pendingPop.OwnerModalId,
                node => UpdateStackFromNative(node, pendingPop.StackId, remainingRouteEntryIds));
            if (updatedWindow is null)
                return new SuppressedNavigationPopFold(effectiveState, true, false, null);

            effectiveWindow = updatedWindow;
        }

        StackNode? effectiveStack = FindStack(
            effectiveWindow,
            pendingPop.OwnerModalId,
            pendingPop.StackId);
        if (effectiveStack is null)
            return new SuppressedNavigationPopFold(effectiveState, true, false, null);

        await RestoreNavigationStackToPopSnapshotAsync(
            pendingPop,
            effectiveStack,
            cancellationToken);

        return !changesLogicalState
            ? new SuppressedNavigationPopFold(effectiveState, true, false, null)
            : new SuppressedNavigationPopFold(
                effectiveState.ReplaceWindow(effectiveWindow),
                true,
                true,
                FindTopRouteForPresentedNode(effectiveWindow, pendingPop.OwnerModalId));
    }

    private async ValueTask<bool> RetireOrReleaseSuppressedPopPagesAsync(
        SuppressedNavigationPop pendingPop,
        MauiPresentationTransaction? transaction)
    {
        var remainingPages = pendingPop.RemainingPages
            .ToHashSet(ReferenceEqualityComparer.Instance);
        Page[] removedPages = pendingPop.KnownPages
            .Where(page => !remainingPages.Contains(page))
            .Reverse()
            .ToArray();
        if (removedPages.Length == 0 ||
            transaction is not null && removedPages.All(transaction.IsRetired))
        {
            return false;
        }

        if (transaction is not null)
        {
            foreach (Page removedPage in removedPages)
                transaction.Retire(removedPage);
        }
        else
        {
            foreach (Page removedPage in removedPages)
                await ReleaseAndDiagnoseAsync(removedPage);
        }

        return true;
    }

    private bool TryResolveCapturedNavigationHost(
        NavigationState state,
        SuppressedNavigationPop pendingPop,
        out WindowNode window,
        out StackNode targetStack)
    {
        window = null!;
        targetStack = null!;

        if (!_navigationPageStackIds.TryGetValue(
                pendingPop.NavigationPage,
                out string? currentStackId) ||
            !StringComparer.Ordinal.Equals(currentStackId, pendingPop.StackId))
        {
            return false;
        }

        HashSet<Page> livePages = CollectLivePages(CurrentPage);
        if (!livePages.Contains(pendingPop.NavigationPage))
            return false;

        string? currentOwnerModalId = FindOwningModalId(pendingPop.NavigationPage);
        if (!StringComparer.Ordinal.Equals(currentOwnerModalId, pendingPop.OwnerModalId))
            return false;

        WindowNode? capturedWindow = string.IsNullOrWhiteSpace(pendingPop.WindowId)
            ? state.ActiveWindow
            : state.FindWindow(pendingPop.WindowId);
        if (capturedWindow is null)
            return false;

        StackNode? capturedStack = FindStack(
            capturedWindow,
            pendingPop.OwnerModalId,
            pendingPop.StackId);
        if (capturedStack is null)
            return false;

        window = capturedWindow;
        targetStack = capturedStack;
        return true;
    }

    private async Task RestoreNavigationStackToPopSnapshotAsync(
        SuppressedNavigationPop pendingPop,
        StackNode effectiveStack,
        CancellationToken cancellationToken)
    {
        NavigationPage navigationPage = pendingPop.NavigationPage;
        IReadOnlyList<Page> currentStack = navigationPage.Navigation.NavigationStack;
        bool capturedStackIsPrefix = currentStack.Count >= pendingPop.RemainingPages.Count &&
            StackPrefixMatches(currentStack, pendingPop.RemainingPages);

        if (capturedStackIsPrefix && pendingPop.RemainingPages.Count > 0)
        {
            while (navigationPage.Navigation.NavigationStack.Count > pendingPop.RemainingPages.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Page? removed = await _nativeOperations.PopAsync(navigationPage, animated: false);
                cancellationToken.ThrowIfCancellationRequested();
                if (removed is null)
                    throw new InvalidOperationException("Native stack pop did not return the removed page.");

                await DetachPageTreeAsync(removed);
            }

            UpdateKnownNavigationPages(navigationPage);
            return;
        }

        await ReconcileNavigationStackAsync(
            navigationPage,
            effectiveStack,
            pendingPop.IsNavigationTarget,
            cancellationToken);
    }

    private void WriteSuppressedPopProjectionFailure(MauiNavigationStackProjectionError error)
    {
        _diagnostics.Write(
            NavigationDiagnosticEventKind.PresentationVerificationFailed,
            LifecycleOperationId(),
            $"Suppressed native navigation pop produced an invalid stack at page index {error.PageIndex}: {error.Message}",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.PresentationPath] = $"$.nativeStack[{error.PageIndex}]",
                [NavigationDiagnosticDataKeys.PresentationExpected] = "valid route-owned page segment",
                [NavigationDiagnosticDataKeys.PresentationActual] = error.Message
            });
    }

    private void MarkHostBackReconciliationPending(AppRoute? route)
    {
        _hostBackReconciliationPending = true;
        _pendingHostBackRoute = route;
    }

    private void PreserveHostBackReconciliation(
        SuppressedNavigationPopFold popFold,
        bool epochRemainedCurrent)
    {
        if (!popFold.LogicalStateChanged)
            return;

        if (epochRemainedCurrent)
        {
            MarkHostBackReconciliationPending(popFold.Route);
            return;
        }

        if (!_disposed)
        {
            RequestReconciliation(
                _lastState,
                NavigationReconciliationSource.HostBack,
                "Native stack pop changed.",
                popFold.Route);
        }
    }

    private void DiscardSuppressedNavigationPops()
    {
        _suppressedNavigationPops.Clear();
    }

    private void ScheduleSuppressedNativeChangeDrain()
    {
        if (!_suppressedNavigationPopDrainQueued &&
            (_suppressedNavigationPops.Count != 0 || _hostBackReconciliationPending))
        {
            _suppressedNavigationPopDrainQueued = true;
            if (!QueueNativeCleanupForEpoch(_nativeTreeEpoch, DrainSuppressedNavigationPopsAsync))
                _suppressedNavigationPopDrainQueued = false;
        }

        if (!_branchHostSelectionDrainQueued && _pendingBranchHostSelections.Count != 0)
        {
            _branchHostSelectionDrainQueued = true;
            if (!QueueNativeCleanupForEpoch(_nativeTreeEpoch, DrainSuppressedBranchHostSelectionsAsync))
                _branchHostSelectionDrainQueued = false;
        }
    }

    private async Task DrainSuppressedNavigationPopsAsync()
    {
        bool publishHostBack = _hostBackReconciliationPending;
        AppRoute? route = _pendingHostBackRoute;
        _hostBackReconciliationPending = false;
        _pendingHostBackRoute = null;

        bool previousSuppressReconciliation = _suppressReconciliation;
        _suppressReconciliation = true;
        try
        {
            SuppressedNavigationPopFold popFold = await FoldSuppressedNavigationPopsAsync(
                _lastState,
                transaction: null,
                CancellationToken.None);
            if (popFold.LogicalStateChanged)
            {
                VerifyPresentation(popFold.EffectiveState, LifecycleOperationId());
                _lastState = popFold.EffectiveState;
                publishHostBack = true;
                route = popFold.Route;
            }
        }
        finally
        {
            _suppressReconciliation = previousSuppressReconciliation;
            _suppressedNavigationPopDrainQueued = false;
        }

        if (publishHostBack && !_disposed)
        {
            RequestReconciliation(
                _lastState,
                NavigationReconciliationSource.HostBack,
                "Native stack pop changed.",
                route);
        }

        ScheduleSuppressedNativeChangeDrain();
    }

    private async Task DrainSuppressedBranchHostSelectionsAsync()
    {
        try
        {
            if (_suppressReconciliation || _disposed)
                return;

            KeyValuePair<IMauiBranchHost, string>[] pendingSelections =
                _pendingBranchHostSelections.ToArray();
            foreach (KeyValuePair<IMauiBranchHost, string> pendingSelection in pendingSelections)
            {
                if (_pendingBranchHostSelections.TryGetValue(pendingSelection.Key, out string? branchId) &&
                    StringComparer.Ordinal.Equals(branchId, pendingSelection.Value))
                {
                    _pendingBranchHostSelections.Remove(pendingSelection.Key);
                    await ReconcileBranchHostSelectionAsync(pendingSelection.Key, branchId);
                }
            }
        }
        finally
        {
            _branchHostSelectionDrainQueued = false;
        }

        ScheduleSuppressedNativeChangeDrain();
    }

    private void OnModalPageDisappearing(object? sender, EventArgs e)
    {
        if (_suppressReconciliation || _disposed || sender is not Page page)
        {
            return;
        }

        ScheduleModalDismissalReconciliation(page);
    }

    internal void ScheduleModalDismissalReconciliation(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        MauiNativeTreeEpoch epoch = EpochFor(page);
        _mainThreadDispatcher.BeginInvoke(() => QueueNativeCleanupForEpoch(
            epoch,
            () => ReconcileModalDismissalIfRemovedAsync(page)));
    }

    private async Task ReconcileModalDismissalIfRemovedAsync(Page page)
    {
        if (_suppressReconciliation || _disposed || IsModalPresented(page))
        {
            return;
        }

        var modalId = GetModalId(page);
        var window = _lastState.ActiveWindow;
        if (string.IsNullOrWhiteSpace(modalId) || window is null)
        {
            return;
        }

        var remainingModals = window.Modals
            .Where(modal => !StringComparer.Ordinal.Equals(modal.Id, modalId))
            .ToArray();

        if (remainingModals.Length == window.Modals.Count)
        {
            return;
        }

        await ReleaseAndDiagnoseAsync(page);
        var updatedState = _lastState.ReplaceWindow(window with { Modals = remainingModals });
        RequestReconciliation(updatedState, NavigationReconciliationSource.ModalDismissed, "Native modal dismissal changed.");
    }

    private bool QueueNativeCleanup(Func<Task> cleanup)
    {
        return QueueNativeCleanupForEpoch(_nativeTreeEpoch, cleanup);
    }

    private bool QueueNativeCleanupForEpoch(MauiNativeTreeEpoch epoch, Func<Task> cleanup)
    {
        if (!EpochRemainsCurrent(epoch))
            return false;
        if (!TryBeginOperation())
            return false;

        _ = RunQueuedNativeCleanupAsync(epoch, cleanup);
        return true;
    }

    private async Task RunQueuedNativeCleanupAsync(MauiNativeTreeEpoch epoch, Func<Task> cleanup)
    {
        var lockTaken = false;
        try
        {
            await _presentationOperationLock.WaitAsync(_shutdownCancellation.Token).ConfigureAwait(false);
            lockTaken = true;
            _shutdownCancellation.Token.ThrowIfCancellationRequested();
            if (!EpochRemainsCurrent(epoch))
                return;

            // The dispatcher hop can queue behind Window.Destroying, so the epoch must be rechecked inside the
            // delegate: passing the check above only proves the tree was alive before the hop.
            Task GuardedCleanupAsync() => EpochRemainsCurrent(epoch) ? cleanup() : Task.CompletedTask;

            if (_mainThreadDispatcher.IsMainThread)
                await GuardedCleanupAsync();
            else
                await _mainThreadDispatcher.InvokeAsync(GuardedCleanupAsync);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Shutdown cancels cleanup that has not started mutating native state.
        }
        catch (Exception ex)
        {
            _diagnostics.Write(
                NavigationDiagnosticEventKind.ReconciliationFailed,
                LifecycleOperationId(),
                "Queued native presentation cleanup failed.",
                new Dictionary<string, object?>
                {
                    [NavigationDiagnosticDataKeys.ExceptionType] = ex.GetType().FullName,
                    [NavigationDiagnosticDataKeys.ExceptionMessage] = ex.Message
                },
                phase: NavigationDiagnosticPhase.Reconciliation);
        }
        finally
        {
            if (lockTaken)
                _presentationOperationLock.Release();

            EndOperation();
        }
    }

    private async ValueTask ReleaseAndDiagnoseAsync(Page page)
    {
        try
        {
            await DetachPageTreeAsync(page);
        }
        catch (Exception ex)
        {
            // Retired pages are released only after the target presentation has been verified. Cleanup is
            // irreversible, so report failures without throwing them into transaction rollback, which could
            // otherwise reattach a page whose handle or scope has already been released.
            WritePageReleaseFailure(page, ex);
        }
    }

    private async ValueTask ReleaseCommittedAndDiagnoseAsync(
        Page page,
        MauiNativeTreeEpoch epoch)
    {
        try
        {
            // The transaction remains discoverable while committed cleanup runs so destruction can abandon
            // its remaining native references. Bypass the transaction-aware staging entry point here: this
            // page has already been verified as non-live and is now being irreversibly retired.
            await DetachPageTreeWithFailuresForEpochAsync(page, epoch);
        }
        catch (Exception ex)
        {
            WritePageReleaseFailure(page, ex);
        }
    }

    private void WritePageReleaseFailure(Page? page, Exception exception)
    {
        var data = new Dictionary<string, object?>
        {
            [NavigationDiagnosticDataKeys.ExceptionType] = exception.GetType().FullName,
            [NavigationDiagnosticDataKeys.ExceptionMessage] = exception.Message
        };
        if (page is not null)
            data[NavigationDiagnosticDataKeys.PageType] = page.GetType().FullName;

        _diagnostics.Write(
            NavigationDiagnosticEventKind.PresentationPageReleaseFailed,
            LifecycleOperationId(),
            "A page could not be fully released.",
            data);
    }

    private void ReconcileStackFromNative(string stackId, NavigationPage navigationPage)
    {
        var projection = MauiNavigationStackProjection.Create(navigationPage.Navigation.NavigationStack);
        if (projection.Error is { } error)
        {
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationVerificationFailed,
                LifecycleOperationId(),
                $"Native navigation stack is invalid at page index {error.PageIndex}: {error.Message}",
                new Dictionary<string, object?>
                {
                    [NavigationDiagnosticDataKeys.PresentationPath] = $"$.nativeStack[{error.PageIndex}]",
                    [NavigationDiagnosticDataKeys.PresentationExpected] = "valid route-owned page segment",
                    [NavigationDiagnosticDataKeys.PresentationActual] = error.Message
                });
            return;
        }

        var remainingRouteEntryIds = projection.Segments
            .Select(static segment => segment.RouteEntryId)
            .ToArray();
        var ownerModalId = FindOwningModalId(navigationPage);
        var currentRouteEntryIds = FindStack(
                _lastState.ActiveWindow,
                ownerModalId,
                stackId)?
            .Entries
            .Select(static entry => entry.Id)
            .ToArray();
        if (currentRouteEntryIds is not null &&
            currentRouteEntryIds.SequenceEqual(remainingRouteEntryIds, StringComparer.Ordinal))
        {
            return;
        }

        var updatedWindow = UpdateWindowForPresentedNode(
            navigationPage,
            node => UpdateStackFromNative(node, stackId, remainingRouteEntryIds));
        if (updatedWindow is null)
        {
            return;
        }

        var updatedState = _lastState.ReplaceWindow(updatedWindow);
        var route = FindTopRouteForPresentedNode(updatedWindow, ownerModalId);
        RequestReconciliation(updatedState, NavigationReconciliationSource.HostBack, "Native stack pop changed.", route);
    }

    private static StackNode? FindStack(WindowNode? window, string? ownerModalId, string stackId)
    {
        if (window is null)
        {
            return null;
        }

        NavigationNode? content;
        if (string.IsNullOrWhiteSpace(ownerModalId))
        {
            content = window.Root;
        }
        else
        {
            content = window.Modals
                .FirstOrDefault(modal => StringComparer.Ordinal.Equals(modal.Id, ownerModalId))?
                .Content;
        }

        return content is null ? null : FindStack(content, stackId);
    }

    private static StackNode? FindStack(NavigationNode node, string stackId)
    {
        return node switch
        {
            StackNode stack when StringComparer.Ordinal.Equals(stack.Id, stackId) => stack,
            BranchHostNode branchHost => FindStack(branchHost, stackId),
            ModalNode modal when modal.Content is not null => FindStack(modal.Content, stackId),
            _ => null
        };
    }

    private static StackNode? FindStack(BranchHostNode branchHost, string stackId)
    {
        foreach (NavigationBranch branch in branchHost.Branches)
        {
            if (FindStack(branch.Content, stackId) is { } stack)
                return stack;
        }

        return null;
    }

    private WindowNode? UpdateWindowForPresentedNode(
        Page ownerPage,
        Func<NavigationNode, NavigationNode?> update)
    {
        var window = _lastState.ActiveWindow;
        if (window is null)
        {
            return null;
        }

        return UpdateWindowContent(window, FindOwningModalId(ownerPage), update);
    }

    private static WindowNode? UpdateWindowContent(
        WindowNode window,
        string? ownerModalId,
        Func<NavigationNode, NavigationNode?> update)
    {
        if (string.IsNullOrWhiteSpace(ownerModalId))
        {
            var updatedRoot = window.Root is null ? null : update(window.Root);
            return updatedRoot is null ? null : window with { Root = updatedRoot };
        }

        var updatedModals = window.Modals.ToArray();
        for (var i = 0; i < updatedModals.Length; i++)
        {
            if (!StringComparer.Ordinal.Equals(updatedModals[i].Id, ownerModalId))
            {
                continue;
            }

            if (updatedModals[i].Content is null)
            {
                return null;
            }

            var updatedContent = update(updatedModals[i].Content!);
            if (updatedContent is null)
            {
                return null;
            }

            updatedModals[i] = updatedModals[i] with { Content = updatedContent };
            return window with { Modals = updatedModals };
        }

        return null;
    }

    private string? FindOwningModalId(Page page)
    {
        var modalId = GetModalId(page);
        if (!string.IsNullOrWhiteSpace(modalId))
        {
            return modalId;
        }

        var root = CurrentPage ?? _attachedWindow?.Page;
        if (root is null)
        {
            return null;
        }

        foreach (var modalPage in root.Navigation.ModalStack.Reverse())
        {
            var candidateModalId = GetModalId(modalPage);
            if (string.IsNullOrWhiteSpace(candidateModalId))
            {
                continue;
            }

            if (ContainsPageInStructuralTree(modalPage, page))
            {
                return candidateModalId;
            }
        }

        return null;
    }

    private bool ContainsPageInStructuralTree(Page root, Page target)
    {
        if (ReferenceEquals(root, target))
        {
            return true;
        }

        return root switch
        {
            _ when _branchHostPages.TryGetValue(root, out IMauiBranchHost? host) => host.Branches.Any(branch =>
                ContainsPageInStructuralTree(branch.Page, target)),
            NavigationPage navigationPage => navigationPage.Navigation.NavigationStack.Any(page => ContainsPageInStructuralTree(page, target)),
            _ => false
        };
    }

    private static AppRoute? FindTopRouteForPresentedNode(WindowNode window, string? ownerModalId)
    {
        if (!string.IsNullOrWhiteSpace(ownerModalId))
        {
            var modal = window.Modals.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, ownerModalId));
            if (modal is not null)
            {
                return modal.Content is null
                    ? modal.RouteEntry.Route
                    : PresentedRouteTraversal.FindTopRoute(modal.Content) ?? modal.RouteEntry.Route;
            }
        }

        return window.Root is null ? null : PresentedRouteTraversal.FindTopRoute(window.Root);
    }

    private void RequestReconciliation(
        NavigationState state,
        NavigationReconciliationSource source,
        string reason,
        AppRoute? route = null)
    {
        _lastState = state;
        ReconciliationRequested?.Invoke(
            this,
            new NavigationReconciliationRequestedEventArgs(new NavigationReconciliation(state, source, route, reason)));
    }

    private static NavigationNode? UpdateBranchHostSelection(NavigationNode node, string branchHostId, string selectedBranchId)
    {
        return node switch
        {
            BranchHostNode branchHost when StringComparer.Ordinal.Equals(branchHost.Id, branchHostId) =>
                branchHost.Branches.Any(branch => StringComparer.Ordinal.Equals(branch.Id, selectedBranchId))
                    ? branchHost with { SelectedBranchId = selectedBranchId }
                    : null,
            BranchHostNode branchHost => UpdateSelectedBranch(branchHost, child => UpdateBranchHostSelection(child, branchHostId, selectedBranchId)),
            ModalNode modal when modal.Content is not null =>
                UpdateBranchHostSelection(modal.Content, branchHostId, selectedBranchId) is { } updated
                    ? modal with { Content = updated }
                    : null,
            _ => null
        };
    }

    private static NavigationNode? UpdateStackFromNative(
        NavigationNode node,
        string stackId,
        IReadOnlyList<string> remainingRouteEntryIds)
    {
        return node switch
        {
            StackNode stack when StringComparer.Ordinal.Equals(stack.Id, stackId) =>
                UpdateStackEntriesFromNative(stack, remainingRouteEntryIds),
            BranchHostNode branchHost => UpdateBranchContainingStack(
                branchHost,
                stackId,
                remainingRouteEntryIds),
            ModalNode modal when modal.Content is not null =>
                UpdateStackFromNative(modal.Content, stackId, remainingRouteEntryIds) is { } updated
                    ? modal with { Content = updated }
                    : null,
            _ => null
        };
    }

    private static BranchHostNode? UpdateBranchContainingStack(
        BranchHostNode branchHost,
        string stackId,
        IReadOnlyList<string> remainingRouteEntryIds)
    {
        foreach (NavigationBranch branch in branchHost.Branches)
        {
            NavigationNode? updatedContent = UpdateStackFromNative(
                branch.Content,
                stackId,
                remainingRouteEntryIds);
            if (updatedContent is not null)
                return branchHost.ReplaceBranch(branch with { Content = updatedContent });
        }

        return null;
    }

    private static StackNode? UpdateStackEntriesFromNative(
        StackNode stack,
        IReadOnlyList<string> remainingRouteEntryIds)
    {
        if (remainingRouteEntryIds.Count == 0)
        {
            return stack.Entries.Count == 0
                ? stack
                : stack with { Entries = Array.Empty<RouteEntry>() };
        }

        var updatedEntries = new List<RouteEntry>(remainingRouteEntryIds.Count);
        foreach (var entryId in remainingRouteEntryIds)
        {
            var entry = stack.Entries.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, entryId));
            if (entry is null)
            {
                return null;
            }

            updatedEntries.Add(entry);
        }

        return stack with { Entries = updatedEntries };
    }

    private static NavigationNode? UpdateSelectedBranch(BranchHostNode branchHost, Func<NavigationNode, NavigationNode?> update)
    {
        var selectedBranch = branchHost.SelectedBranch;
        if (selectedBranch is null)
        {
            return null;
        }

        var updatedContent = update(selectedBranch.Content);
        return updatedContent is null
            ? null
            : branchHost.ReplaceBranch(selectedBranch with { Content = updatedContent });
    }

    private async Task RebuildStateFromScratchAsync(NavigationState state, string operationId)
    {
        Page? failedRoot = CurrentPage;
        Page? rebuiltRoot = null;
        NavigationPresentationContext? previousPresentationContext = _activeNavigationPresentationContext;
        _activeNavigationPresentationContext ??= _lastNavigationPresentationContext is { } lastContext
            ? lastContext with { CurrentState = state, OperationId = operationId }
            : throw new InvalidOperationException(
                "Branch-host recovery requires a prior navigation presentation context.");
        MauiNativeTreeEpoch recoveryEpoch = _nativeTreeEpoch;
        await using var recoveryTransaction = new MauiPresentationTransaction(this);
        _activeTransaction = recoveryTransaction;
        try
        {
            // Recovery runs while an operation is already unwinding, so there is no operation token to honour --
            // but the rebuild still materializes application-supplied hosts and pages, and must stop the moment
            // this epoch is destroyed rather than populating a tree nobody will ever attach.
            CancellationToken recoveryCancellation = recoveryEpoch.CancellationToken;
            WindowNode? window = state.ActiveWindow;
            if (window is not null && (window.Root is not null || window.Modals.Count > 0))
            {
                rebuiltRoot = window.Root is null
                    ? CreateEmptyPage()
                    : await MaterializeNodeAsync(
                        window.Root,
                        null,
                        operationId,
                        isNavigationTarget: window.Modals.Count == 0,
                        recoveryCancellation,
                        placement: MauiBranchHostPlacement.WindowRoot);
                await ApplyModalsAsync(
                    rebuiltRoot,
                    window.Modals,
                    operationId,
                    recoveryCancellation);
            }

            CurrentPage = rebuiltRoot;
            SetAttachedWindowPage(rebuiltRoot);
            VerifyPresentation(state, operationId);
            bool epochRemainedCurrent = await recoveryTransaction.CommitAsync();
            if (!epochRemainedCurrent)
                throw new MauiNativeTreeInvalidatedException();

            _activeTransaction = null;
            _lastState = state;
            RebuildTrackingFromCurrentPage();

            if (!ReferenceEquals(failedRoot, rebuiltRoot) && failedRoot is not null)
                await ReleaseAndDiagnoseAsync(failedRoot);

            InvokeRootPageChanged(rebuiltRoot);
        }
        catch
        {
            _activeTransaction = null;
            try
            {
                await recoveryTransaction.RollbackAsync();
            }
            catch
            {
                // Preserve the original recovery failure; created pages are still released below.
            }

            await recoveryTransaction.ReleaseCreatedPagesAsync();

            throw;
        }
        finally
        {
            _activeNavigationPresentationContext = previousPresentationContext;
        }
    }

    private HashSet<Page> CollectLivePages(Page? root)
    {
        var pages = new HashSet<Page>(ReferenceEqualityComparer.Instance);
        if (root is null)
            return pages;

        CollectStructuralPages(root, pages);
        foreach (Page modal in root.Navigation.ModalStack)
            CollectStructuralPages(modal, pages);
        return pages;
    }

    private void CollectStructuralPages(Page page, HashSet<Page> pages)
    {
        if (!pages.Add(page))
            return;

        if (_branchHostPages.TryGetValue(page, out IMauiBranchHost? host))
        {
            foreach (MauiBranchHostBranch branch in host.Branches)
                CollectStructuralPages(branch.Page, pages);
            return;
        }

        switch (page)
        {
            case NavigationPage navigationPage:
                foreach (Page child in navigationPage.Navigation.NavigationStack)
                    CollectStructuralPages(child, pages);
                break;
        }
    }

    private void RebuildTrackingFromCurrentPage()
    {
        foreach (NavigationPage navigationPage in _navigationPageStackIds.Keys.ToArray())
            UntrackNavigationPage(navigationPage);
        foreach (IMauiBranchHost host in new HashSet<IMauiBranchHost>(
                     _branchHostPages.Values,
                     ReferenceEqualityComparer.Instance).ToArray())
            UntrackBranchHost(host);
        foreach (Page modalPage in _trackedModalPages.ToArray())
            UntrackModalPage(modalPage);

        var visited = new HashSet<Page>(ReferenceEqualityComparer.Instance);
        if (CurrentPage is null)
            return;

        TrackStructuralPage(CurrentPage, visited);
        foreach (Page modalPage in CurrentPage.Navigation.ModalStack)
        {
            TrackModalPage(modalPage);
            TrackStructuralPage(modalPage, visited);
        }
    }

    private void TrackStructuralPage(Page page, HashSet<Page> visited)
    {
        if (!visited.Add(page))
            return;

        if (_branchHostPages.TryGetValue(page, out IMauiBranchHost? host))
        {
            TrackBranchHost(host);
            foreach (MauiBranchHostBranch branch in host.Branches)
                TrackStructuralPage(branch.Page, visited);
            return;
        }

        switch (page)
        {
            case NavigationPage navigationPage:
                if (GetHostId(navigationPage) is { } stackId)
                    TrackNavigationPage(navigationPage, stackId);
                foreach (Page child in navigationPage.Navigation.NavigationStack)
                    TrackStructuralPage(child, visited);
                break;
        }
    }

    private static Dictionary<string, RouteEntry> CreateRouteEntryMap(NavigationState state)
    {
        var result = new Dictionary<string, RouteEntry>(StringComparer.Ordinal);
        foreach (WindowNode window in state.Windows)
        {
            if (window.Root is not null)
                AddRouteEntries(window.Root, result);
            foreach (ModalNode modal in window.Modals)
                AddRouteEntries(modal, result);
        }

        return result;
    }

    private static void AddRouteEntries(NavigationNode node, Dictionary<string, RouteEntry> result)
    {
        switch (node)
        {
            case StackNode stack:
                foreach (RouteEntry entry in stack.Entries)
                    result[entry.Id] = entry;
                break;
            case BranchHostNode branchHost:
                foreach (NavigationBranch branch in branchHost.Branches)
                    AddRouteEntries(branch.Content, result);
                break;
            case ModalNode modal:
                result[modal.RouteEntry.Id] = modal.RouteEntry;
                if (modal.Content is not null)
                    AddRouteEntries(modal.Content, result);
                break;
        }
    }

    private void VerifyPresentation(NavigationState targetState, string operationId)
    {
        VerifyPresentation(targetState, operationId, CurrentPage, _attachedWindow);
    }

    private void VerifyPresentation(
        NavigationState targetState,
        string operationId,
        Page? currentPage,
        Window? attachedWindow)
    {
        var mismatch = _presentationVerifier.Verify(new MauiPresentationVerificationContext(
            targetState,
            currentPage,
            attachedWindow,
            _presentationOptions,
            _branchHostPages));
        if (mismatch is null)
        {
            return;
        }

        var data = new Dictionary<string, object?>
        {
            [NavigationDiagnosticDataKeys.PresentationPath] = mismatch.Path,
            [NavigationDiagnosticDataKeys.PresentationExpected] = mismatch.Expected,
            [NavigationDiagnosticDataKeys.PresentationActual] = mismatch.Actual
        };
        AddIfPresent(data, NavigationDiagnosticDataKeys.WindowId, targetState.ActiveWindowId);
        _diagnostics.Write(
            NavigationDiagnosticEventKind.PresentationVerificationFailed,
            operationId,
            $"Presentation verification failed at '{mismatch.Path}'.",
            data);

        throw new InvalidOperationException(
            $"Presentation verification failed at '{mismatch.Path}'. Expected '{mismatch.Expected}', actual '{mismatch.Actual}'.");
    }

    private void WritePageLifecycle(
        NavigationDiagnosticEventKind kind,
        Page page,
        string message)
    {
        _diagnostics.Write(
            kind,
            LifecycleOperationId(),
            message,
            PageDiagnosticData(page));
    }

    private void WriteHandlerLifecycle(
        NavigationDiagnosticEventKind kind,
        Page page,
        string handlerName,
        string message)
    {
        var data = PageDiagnosticData(page);
        data[NavigationDiagnosticDataKeys.HandlerName] = handlerName;

        _diagnostics.Write(
            kind,
            LifecycleOperationId(),
            message,
            data);
    }

    private Dictionary<string, object?> PageDiagnosticData(Page page)
    {
        var data = new Dictionary<string, object?>
        {
            [NavigationDiagnosticDataKeys.PageType] = page.GetType().FullName
        };

        AddIfPresent(data, NavigationDiagnosticDataKeys.HostId, GetHostId(page));
        AddIfPresent(data, NavigationDiagnosticDataKeys.BranchId, GetBranchId(page));
        AddIfPresent(data, NavigationDiagnosticDataKeys.RouteEntryId, GetRouteEntryId(page));
        AddIfPresent(
            data,
            NavigationDiagnosticDataKeys.PresentationOwnerRouteEntryId,
            GetPresentationOwnerRouteEntryId(page));
        AddIfPresent(data, NavigationDiagnosticDataKeys.PresentationPageKey, GetPresentationPageKey(page));
        AddIfPresent(data, NavigationDiagnosticDataKeys.ModalId, GetModalId(page));

        return data;
    }

    private static void AddIfPresent(Dictionary<string, object?> data, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            data[key] = value;
        }
    }

    private string LifecycleOperationId()
    {
        if (!string.IsNullOrWhiteSpace(_activeOperationId))
        {
            return _activeOperationId;
        }

        if (string.IsNullOrWhiteSpace(_lifecycleOperationId))
        {
            _lifecycleOperationId = CreateOperationId();
        }

        return _lifecycleOperationId;
    }

    private static string CreateOperationId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static void SetHostId(BindableObject bindableObject, string id)
    {
        MauiPresentationMetadata.SetHostId(bindableObject, id);
    }

    private static string? GetHostId(BindableObject? bindableObject)
    {
        return MauiPresentationMetadata.GetHostId(bindableObject);
    }

    private static void SetBranchId(BindableObject bindableObject, string id)
    {
        MauiPresentationMetadata.SetBranchId(bindableObject, id);
    }

    private static string? GetBranchId(BindableObject? bindableObject)
    {
        return MauiPresentationMetadata.GetBranchId(bindableObject);
    }

    private static void SetRouteEntryId(BindableObject bindableObject, string id)
    {
        MauiPresentationMetadata.SetRouteEntryId(bindableObject, id);
    }

    private static string? GetRouteEntryId(BindableObject? bindableObject)
    {
        return MauiPresentationMetadata.GetRouteEntryId(bindableObject);
    }

    private static void SetModalId(BindableObject bindableObject, string id)
    {
        MauiPresentationMetadata.SetModalId(bindableObject, id);
    }

    private static string? GetModalId(BindableObject? bindableObject)
    {
        return MauiPresentationMetadata.GetModalId(bindableObject);
    }

    private static void SetPresentationOwnerRouteEntryId(BindableObject bindableObject, string? id)
    {
        MauiPresentationMetadata.SetPresentationOwnerRouteEntryId(bindableObject, id);
    }

    private static string? GetPresentationOwnerRouteEntryId(BindableObject? bindableObject)
    {
        return MauiPresentationMetadata.GetPresentationOwnerRouteEntryId(bindableObject);
    }

    private static void SetPresentationPageKey(BindableObject bindableObject, string? key)
    {
        MauiPresentationMetadata.SetPresentationPageKey(bindableObject, key);
    }

    private static string? GetPresentationPageKey(BindableObject? bindableObject)
    {
        return MauiPresentationMetadata.GetPresentationPageKey(bindableObject);
    }

    private static bool IsPresentationPage(BindableObject bindableObject)
    {
        return !string.IsNullOrWhiteSpace(GetPresentationOwnerRouteEntryId(bindableObject)) ||
               !string.IsNullOrWhiteSpace(GetPresentationPageKey(bindableObject));
    }

    private void ThrowIfUnavailable()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MauiNavigationPresenter));
            if (_consistencyFailure is not null)
                throw _consistencyFailure;
        }
    }

    private void BeginOperation()
    {
        lock (_lifetimeGate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MauiNavigationPresenter));
            if (_consistencyFailure is not null)
                throw _consistencyFailure;

            _activeOperations++;
        }
    }

    private bool TryBeginOperation()
    {
        lock (_lifetimeGate)
        {
            if (_disposed || _consistencyFailure is not null)
                return false;

            _activeOperations++;
            return true;
        }
    }

    private void EndOperation()
    {
        var startCleanup = false;
        lock (_lifetimeGate)
        {
            _activeOperations--;
            if (_activeOperations < 0)
                throw new InvalidOperationException("MAUI presenter operation admission count became negative.");

            if (_disposed && _shutdownSignalIssued && _shutdownCancellationCompleted &&
                _activeOperations == 0 && !_finalCleanupStarted)
            {
                _finalCleanupStarted = true;
                startCleanup = true;
            }
        }

        if (startCleanup)
            QueueFinalCleanup();
    }

    private CancellationToken CreateOperationCancellation(
        CancellationToken callerCancellation,
        out CancellationTokenSource? linkedCancellation)
    {
        linkedCancellation = callerCancellation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                _shutdownCancellation.Token,
                _nativeTreeEpoch.CancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(
                _shutdownCancellation.Token,
                _nativeTreeEpoch.CancellationToken);
        return linkedCancellation.Token;
    }

    private CancellationToken CreateHostOperationCancellation(
        CancellationToken callerCancellation,
        out CancellationTokenSource? linkedCancellation)
    {
        if (!callerCancellation.CanBeCanceled)
        {
            linkedCancellation = null;
            return _shutdownCancellation.Token;
        }

        linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation,
            _shutdownCancellation.Token);
        return linkedCancellation.Token;
    }

    private void RegisterPage(Page page)
    {
        if (_pageEpochs.TryGetValue(page, out MauiNativeTreeEpoch? existing))
        {
            if (ReferenceEquals(existing, _nativeTreeEpoch))
                return;

            throw new InvalidOperationException("A MAUI page cannot belong to more than one native-tree epoch.");
        }

        _nativeTreeEpoch.Register(page);
        try
        {
            _pageEpochs.Add(page, _nativeTreeEpoch);
        }
        catch
        {
            _nativeTreeEpoch.Forget(page);
            throw;
        }
    }

    private void ForgetPage(Page page)
    {
        if (_pageEpochs.TryGetValue(page, out MauiNativeTreeEpoch? epoch))
            epoch.Forget(page);
        _pageEpochs.Remove(page);
    }

    private void RegisterWindow(Window window)
    {
        if (_windowEpochs.TryGetValue(window, out MauiNativeTreeEpoch? existing))
        {
            if (ReferenceEquals(existing, _nativeTreeEpoch))
                return;

            throw new InvalidOperationException("A MAUI window cannot belong to more than one native-tree epoch.");
        }

        _nativeTreeEpoch.Register(window);
        try
        {
            _windowEpochs.Add(window, _nativeTreeEpoch);
        }
        catch
        {
            _nativeTreeEpoch.Forget(window);
            throw;
        }
    }

    private void ForgetWindow(Window window)
    {
        if (_windowEpochs.TryGetValue(window, out MauiNativeTreeEpoch? epoch))
            epoch.Forget(window);
        _windowEpochs.Remove(window);
    }

    private MauiNativeTreeEpoch EpochFor(Page page)
    {
        return _pageEpochs.TryGetValue(page, out MauiNativeTreeEpoch? epoch)
            ? epoch
            : throw new MauiNativeTreeInvalidatedException();
    }

    /// <summary>
    /// Whether <paramref name="epoch"/> is still the presenter's current, open native tree.
    /// </summary>
    private bool EpochRemainsCurrent(MauiNativeTreeEpoch epoch) =>
        ReferenceEquals(epoch, _nativeTreeEpoch) && epoch.IsOpen;

    /// <summary>
    /// Links an operation's cancellation with <paramref name="epoch"/>'s so that closing the native tree cancels
    /// application code that is cooperatively waiting on the token AppNav handed it.
    /// </summary>
    private static CancellationToken LinkEpochCancellation(
        MauiNativeTreeEpoch epoch,
        CancellationToken operationCancellation,
        out CancellationTokenSource? linked)
    {
        linked = null;
        CancellationToken epochCancellation = epoch.CancellationToken;
        if (!operationCancellation.CanBeCanceled)
            return epochCancellation;
        if (!epochCancellation.CanBeCanceled)
            return operationCancellation;

        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(operationCancellation, epochCancellation);
            return linked.Token;
        }
        catch (ObjectDisposedException)
        {
            // The epoch's source was disposed by its closure, which means the epoch is definitively closed.
            linked = null;
            return epochCancellation;
        }
    }

    /// <summary>
    /// Invokes an application-supplied extension point across the ownership boundary and returns its result only
    /// while <paramref name="epoch"/> is still the current native tree.
    /// </summary>
    /// <remarks>
    /// This is the application-facing mirror of <see cref="GuardedMauiNativeNavigationOperations"/>. It is the only
    /// supported way to call into <see cref="IMauiRoutePageLifecycleHook"/>, <see cref="IMauiBranchHostFactory"/>,
    /// <see cref="IMauiBranchHost"/>, or <see cref="IMauiBranchHostUpdate"/>, and it enforces both halves of the
    /// native-tree ownership boundary in one place:
    /// <list type="bullet">
    /// <item>the callee always receives a token that cancels when the epoch closes, even where the calling
    /// operation itself is uncancellable; and</item>
    /// <item>a result produced after the epoch closed is handed to <paramref name="abandonLateResult"/> for
    /// page-free disposal instead of being registered into the replacement tree.</item>
    /// </list>
    /// Application code that ignores its cancellation token may still run to completion; AppNav guarantees only
    /// that it will not act on the result or touch the destroyed tree afterwards.
    /// </remarks>
    private async ValueTask<T> InvokeExtensionPointAsync<T>(
        MauiNativeTreeEpoch epoch,
        Func<CancellationToken, ValueTask<T>> invoke,
        Action<T> abandonLateResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentNullException.ThrowIfNull(invoke);
        ArgumentNullException.ThrowIfNull(abandonLateResult);

        T result;
        CancellationTokenSource? linked = null;
        try
        {
            CancellationToken effective = LinkEpochCancellation(epoch, cancellationToken, out linked);
            effective.ThrowIfCancellationRequested();
            result = await invoke(effective);
        }
        finally
        {
            linked?.Dispose();
        }

        if (EpochRemainsCurrent(epoch))
            return result;

        if (result is not null)
            abandonLateResult(result);

        throw new MauiNativeTreeInvalidatedException();
    }

    /// <summary>
    /// Invokes an application-supplied release or unwind callback across the ownership boundary.
    /// </summary>
    /// <remarks>
    /// Identical to <see cref="InvokeExtensionPointAsync(MauiNativeTreeEpoch, Func{CancellationToken, ValueTask}, CancellationToken)"/>
    /// in how it links cancellation, but deliberately omits the post-await epoch check: these callbacks run on
    /// paths that are already unwinding, where throwing <see cref="MauiNativeTreeInvalidatedException"/> would mask
    /// the failure being cleaned up after. Epoch closure still reaches the callee through the linked token.
    /// </remarks>
    private static async ValueTask InvokeReleaseAcrossBoundaryAsync(
        MauiNativeTreeEpoch epoch,
        Func<CancellationToken, ValueTask> release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentNullException.ThrowIfNull(release);

        CancellationTokenSource? linked = null;
        try
        {
            CancellationToken effective = LinkEpochCancellation(epoch, cancellationToken, out linked);
            await release(effective);
        }
        finally
        {
            linked?.Dispose();
        }
    }

    /// <summary>
    /// Invokes an application-supplied extension point that produces no reference AppNav must own.
    /// </summary>
    /// <inheritdoc cref="InvokeExtensionPointAsync{T}" path="/remarks"/>
    private async ValueTask InvokeExtensionPointAsync(
        MauiNativeTreeEpoch epoch,
        Func<CancellationToken, ValueTask> invoke,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentNullException.ThrowIfNull(invoke);

        CancellationTokenSource? linked = null;
        try
        {
            CancellationToken effective = LinkEpochCancellation(epoch, cancellationToken, out linked);
            effective.ThrowIfCancellationRequested();
            await invoke(effective);
        }
        finally
        {
            linked?.Dispose();
        }

        if (!EpochRemainsCurrent(epoch))
            throw new MauiNativeTreeInvalidatedException();
    }

    private bool CanMutatePage(Page page)
    {
        return _pageEpochs.TryGetValue(page, out MauiNativeTreeEpoch? epoch) &&
               ReferenceEquals(epoch, _nativeTreeEpoch) &&
               epoch.Owns(page);
    }

    private bool CanMutateWindow(Window window)
    {
        return _windowEpochs.TryGetValue(window, out MauiNativeTreeEpoch? epoch) &&
               ReferenceEquals(epoch, _nativeTreeEpoch) &&
               epoch.Owns(window);
    }

    private sealed class MauiPresentationTransaction : IAsyncDisposable
    {
        private readonly MauiNavigationPresenter _presenter;
        private readonly MauiNativeTreeEpoch _epoch;
        private readonly CancellationToken _operationCancellation;
        private Page? _previousCurrentPage;
        private Window? _previousAttachedWindow;
        private Page? _previousWindowPage;
        private Page[] _previousModals;
        private readonly Dictionary<NavigationPage, Page[]> _navigationStacks =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Page, PageSnapshot> _pageSnapshots =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Page, RouteEntry> _updatedPages =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, RouteEntry> _previousEntries;
        private readonly List<IMauiBranchHostUpdate> _branchHostUpdates = [];
        private readonly HashSet<Page> _createdPages = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Page> _retiredPages = new(ReferenceEqualityComparer.Instance);
        private bool _rootChanged;
        private bool _branchHostUpdatesFinalized;

        public MauiPresentationTransaction(
            MauiNavigationPresenter presenter,
            CancellationToken operationCancellation = default)
        {
            _presenter = presenter;
            _operationCancellation = operationCancellation;
            _epoch = presenter._nativeTreeEpoch;
            PreviousState = presenter._lastState;
            _previousCurrentPage = presenter.CurrentPage;
            _previousAttachedWindow = presenter._attachedWindow;
            _previousWindowPage = _previousAttachedWindow?.Page;
            _previousModals = _previousCurrentPage?.Navigation.ModalStack.ToArray() ?? [];
            _previousEntries = CreateRouteEntryMap(PreviousState);

            var visited = new HashSet<Page>(ReferenceEqualityComparer.Instance);
            if (_previousCurrentPage is not null)
                CapturePage(_previousCurrentPage, visited);
            foreach (Page modal in _previousModals)
                CapturePage(modal, visited);
        }

        public NavigationState PreviousState { get; }

        public bool IsInvalidated =>
            !_presenter.EpochRemainsCurrent(_epoch);

        private void ThrowIfInvalidated()
        {
            if (IsInvalidated)
                throw new MauiNativeTreeInvalidatedException();
        }

        public void TrackCreated(Page page) => _createdPages.Add(page);

        public void TrackBranchHostUpdate(IMauiBranchHostUpdate update)
        {
            ArgumentNullException.ThrowIfNull(update);
            if (_branchHostUpdatesFinalized)
                throw new InvalidOperationException("Cannot track a branch-host update after transaction completion.");

            _branchHostUpdates.Add(update);
        }

        public void Retire(Page page) => _retiredPages.Add(page);

        public bool IsRetired(Page page) => _retiredPages.Contains(page);

        public void RecordRootChange() => _rootChanged = true;

        public void AbandonNativeReferences()
        {
            _previousCurrentPage = null;
            _previousAttachedWindow = null;
            _previousWindowPage = null;
            _previousModals = [];
            _navigationStacks.Clear();
            _pageSnapshots.Clear();
            _updatedPages.Clear();
            _branchHostUpdates.Clear();
            _branchHostUpdatesFinalized = true;
            _createdPages.Clear();
            _retiredPages.Clear();
        }

        public void CaptureBranchHostUpdateAbandonments(List<MauiPageAbandonment> abandonments)
        {
            ArgumentNullException.ThrowIfNull(abandonments);
            if (_branchHostUpdatesFinalized)
                return;

            _branchHostUpdatesFinalized = true;
            foreach (IMauiBranchHostUpdate update in _branchHostUpdates.ToArray())
            {
                string updateTypeName = update.GetType().FullName ?? update.GetType().Name;
                MauiPageAbandonment? abandonment =
                    _presenter.CaptureAsyncDisposableAbandonmentOrDiagnose(update, updateTypeName);
                if (abandonment is not null)
                    abandonments.Add(abandonment);
            }

            _branchHostUpdates.Clear();
        }

        public void RecordUpdate(Page page)
        {
            if (_updatedPages.ContainsKey(page))
                return;

            string? entryId = GetRouteEntryId(page);
            if (entryId is not null && _previousEntries.TryGetValue(entryId, out RouteEntry? entry))
                _updatedPages.Add(page, entry);
        }

        public async ValueTask<bool> CommitAsync()
        {
            ThrowIfInvalidated();
            if (!_branchHostUpdatesFinalized)
            {
                foreach (IMauiBranchHostUpdate update in _branchHostUpdates.ToArray())
                {
                    try
                    {
                        await InvokeReleaseAcrossBoundaryAsync(
                            _epoch,
                            token => update.CommitAsync(token),
                            _operationCancellation);
                    }
                    catch (OperationCanceledException) when (IsInvalidated)
                    {
                        // A cooperative commit observes epoch cancellation by throwing. That is the same
                        // condition the epoch-closed result below reports, and it must stay a result rather
                        // than an exception: _lastState has already advanced, so failing ApplyAsync here would
                        // leave RouterNavigator on the previous state while the replacement window is rebuilt
                        // from the new one. Caller-only cancellation still propagates.
                        return false;
                    }

                    if (IsInvalidated)
                        return false;
                }

                await DisposeBranchHostUpdatesAsync();
                if (IsInvalidated)
                    return false;
            }

            HashSet<Page> livePages = _presenter.CollectLivePages(_presenter.CurrentPage);
            var releaseCandidates = new HashSet<Page>(_retiredPages, ReferenceEqualityComparer.Instance);
            foreach (Page createdPage in _createdPages)
                if (!livePages.Contains(createdPage))
                    releaseCandidates.Add(createdPage);

            foreach (Page page in releaseCandidates)
            {
                if (!livePages.Contains(page))
                {
                    await _presenter.ReleaseCommittedAndDiagnoseAsync(page, _epoch);
                    if (IsInvalidated)
                        return false;
                }
            }

            if (IsInvalidated)
                return false;
            if (_rootChanged)
            {
                _presenter.InvokeRootPageChanged(_presenter.CurrentPage);
                if (IsInvalidated)
                    return false;
            }

            return true;
        }

        public async ValueTask RollbackAsync()
        {
            ThrowIfInvalidated();
            Exception? structuralRollbackFailure = null;
            try
            {
                if (!ReferenceEquals(_presenter._attachedWindow, _previousAttachedWindow))
                {
                    throw new InvalidOperationException(
                        "The attached MAUI window changed during a serialized presentation transaction.");
                }

                _presenter.CurrentPage = _previousCurrentPage;
                if (_previousAttachedWindow is not null)
                    _presenter._nativeOperations.SetWindowPage(
                        _previousAttachedWindow,
                        _previousWindowPage);

                foreach ((NavigationPage navigationPage, Page[] pages) in _navigationStacks)
                {
                    while (navigationPage.Navigation.NavigationStack.Count > 1)
                    {
                        int previousCount = navigationPage.Navigation.NavigationStack.Count;
                        await _presenter._nativeOperations.PopAsync(navigationPage, animated: false);
                        ThrowIfInvalidated();
                        if (navigationPage.Navigation.NavigationStack.Count >= previousCount)
                            throw new InvalidOperationException("Native stack rollback did not remove a page.");
                    }

                    if (pages.Length == 0)
                        continue;
                    if (navigationPage.Navigation.NavigationStack.Count != 1 ||
                        !ReferenceEquals(navigationPage.Navigation.NavigationStack[0], pages[0]))
                    {
                        throw new InvalidOperationException("Native stack rollback could not restore its original root page.");
                    }

                    for (var index = 1; index < pages.Length; index++)
                    {
                        await _presenter._nativeOperations.PushAsync(navigationPage, pages[index], animated: false);
                        ThrowIfInvalidated();
                    }
                }

                if (_previousCurrentPage is not null)
                {
                    while (_previousCurrentPage.Navigation.ModalStack.Count > 0)
                    {
                        int previousCount = _previousCurrentPage.Navigation.ModalStack.Count;
                        await _presenter._nativeOperations.PopModalAsync(_previousCurrentPage, animated: false);
                        ThrowIfInvalidated();
                        if (_previousCurrentPage.Navigation.ModalStack.Count >= previousCount)
                            throw new InvalidOperationException("Modal rollback did not remove a page.");
                    }

                    foreach (Page modal in _previousModals)
                    {
                        await _presenter._nativeOperations.PushModalAsync(
                            _previousCurrentPage,
                            modal,
                            animated: false);
                        ThrowIfInvalidated();
                    }
                }

                foreach ((Page page, RouteEntry entry) in _updatedPages)
                {
                    SetRouteEntryId(page, entry.Id);
                    await InvokeReleaseAcrossBoundaryAsync(
                        _epoch,
                        token => _presenter._pageFactory.UpdatePageAsync(
                            page,
                            entry,
                            new MauiRoutePageUpdateContext(MauiRoutePageReuseKind.NonTargetReuse),
                            token),
                        CancellationToken.None);
                    ThrowIfInvalidated();
                }
            }
            catch (MauiNativeTreeInvalidatedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                structuralRollbackFailure = ex;
            }

            Exception? branchHostRollbackFailure = null;
            if (!_branchHostUpdatesFinalized)
            {
                for (var index = _branchHostUpdates.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        IMauiBranchHostUpdate rollbackUpdate = _branchHostUpdates[index];
                        await InvokeReleaseAcrossBoundaryAsync(
                            _epoch,
                            token => rollbackUpdate.RollbackAsync(token),
                            CancellationToken.None);
                        ThrowIfInvalidated();
                    }
                    catch (MauiNativeTreeInvalidatedException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        branchHostRollbackFailure ??= ex;
                    }
                }

                try
                {
                    await DisposeBranchHostUpdatesAsync();
                    ThrowIfInvalidated();
                }
                catch (MauiNativeTreeInvalidatedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    branchHostRollbackFailure ??= ex;
                }
            }

            ThrowIfInvalidated();
            foreach ((Page page, PageSnapshot snapshot) in _pageSnapshots)
                snapshot.Restore(page);

            if (structuralRollbackFailure is not null && branchHostRollbackFailure is not null)
            {
                throw new AggregateException(
                    "Structural and branch-host rollback both failed.",
                    structuralRollbackFailure,
                    branchHostRollbackFailure);
            }

            if (structuralRollbackFailure is not null)
                throw structuralRollbackFailure;
            if (branchHostRollbackFailure is not null)
                throw branchHostRollbackFailure;

            _presenter._lastState = PreviousState;
            _presenter.RebuildTrackingFromCurrentPage();
            _presenter.VerifyPresentation(PreviousState, _presenter.LifecycleOperationId());

            HashSet<Page> livePages = _presenter.CollectLivePages(_presenter.CurrentPage);
            foreach (Page page in _createdPages)
                if (!livePages.Contains(page))
                {
                    await _presenter.ReleaseAndDiagnoseAsync(page);
                    ThrowIfInvalidated();
                }
        }

        public async ValueTask ReleaseCreatedPagesAsync()
        {
            if (IsInvalidated)
                return;

            foreach (Page page in _createdPages)
            {
                await _presenter.ReleaseAndDiagnoseAsync(page);
                if (IsInvalidated)
                    return;
            }
        }

        public async ValueTask ReleaseAllNonLivePagesAsync()
        {
            if (IsInvalidated)
                return;

            HashSet<Page> livePages = _presenter.CollectLivePages(_presenter.CurrentPage);
            var candidates = new HashSet<Page>(_pageSnapshots.Keys, ReferenceEqualityComparer.Instance);
            candidates.UnionWith(_retiredPages);
            candidates.UnionWith(_createdPages);
            foreach (Page page in candidates)
                if (!livePages.Contains(page))
                {
                    await _presenter.ReleaseAndDiagnoseAsync(page);
                    if (IsInvalidated)
                        return;
                }
        }

        /// <summary>
        /// Finalizes any branch-host update this transaction still owns.
        /// </summary>
        /// <remarks>
        /// Commit, rollback, and destruction-driven abandonment each finalize the pending updates themselves, so on
        /// those paths this is a no-op. It exists so that a transaction abandoned by any *other* exit -- a
        /// verification failure, a cancelled window attachment, an unexpected throw -- still cannot leak an
        /// <see cref="IMauiBranchHostUpdate"/> that holds application resources. Rollback is attempted only while the
        /// epoch is still current; once the native tree is destroyed the update is disposed and abandoned with it
        /// rather than being replayed into invalid controls.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            if (_branchHostUpdatesFinalized)
                return;

            if (!IsInvalidated)
            {
                for (var index = _branchHostUpdates.Count - 1; index >= 0; index--)
                {
                    IMauiBranchHostUpdate pending = _branchHostUpdates[index];
                    try
                    {
                        await InvokeReleaseAcrossBoundaryAsync(
                            _epoch,
                            token => pending.RollbackAsync(token),
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _presenter.WritePageReleaseFailure(null, ex);
                    }
                }
            }

            try
            {
                await DisposeBranchHostUpdatesAsync();
            }
            catch (Exception ex)
            {
                _presenter.WritePageReleaseFailure(null, ex);
            }
        }

        private async ValueTask DisposeBranchHostUpdatesAsync()
        {
            if (_branchHostUpdatesFinalized)
                return;

            _branchHostUpdatesFinalized = true;
            Exception? disposalFailure = null;
            foreach (IMauiBranchHostUpdate update in _branchHostUpdates)
            {
                try
                {
                    await update.DisposeAsync();
                }
                catch (Exception ex)
                {
                    disposalFailure ??= ex;
                }
            }

            if (disposalFailure is not null)
                throw disposalFailure;
        }

        private void CapturePage(Page page, HashSet<Page> visited)
        {
            if (!visited.Add(page))
                return;

            _pageSnapshots[page] = PageSnapshot.Capture(page);
            if (_presenter._branchHostPages.TryGetValue(page, out IMauiBranchHost? host))
            {
                foreach (MauiBranchHostBranch branch in host.Branches)
                    CapturePage(branch.Page, visited);
                return;
            }

            switch (page)
            {
                case NavigationPage navigationPage:
                    Page[] stack = navigationPage.Navigation.NavigationStack.ToArray();
                    _navigationStacks[navigationPage] = stack;
                    foreach (Page child in stack)
                        CapturePage(child, visited);
                    break;
            }
        }

        private sealed record PageSnapshot(
            string? HostId,
            string? BranchId,
            string? RouteEntryId,
            string? ModalId,
            string? PresentationOwnerRouteEntryId,
            string? PresentationPageKey,
            string? Title,
            ImageSource? IconImageSource,
            object? BindingContext)
        {
            public static PageSnapshot Capture(Page page) => new(
                GetHostId(page),
                GetBranchId(page),
                GetRouteEntryId(page),
                GetModalId(page),
                GetPresentationOwnerRouteEntryId(page),
                GetPresentationPageKey(page),
                page.Title,
                page.IconImageSource,
                page.BindingContext);

            public void Restore(Page page)
            {
                MauiPresentationMetadata.SetHostId(page, HostId);
                MauiPresentationMetadata.SetBranchId(page, BranchId);
                MauiPresentationMetadata.SetRouteEntryId(page, RouteEntryId);
                MauiPresentationMetadata.SetModalId(page, ModalId);
                MauiPresentationMetadata.SetPresentationOwnerRouteEntryId(page, PresentationOwnerRouteEntryId);
                MauiPresentationMetadata.SetPresentationPageKey(page, PresentationPageKey);
                page.Title = Title;
                page.IconImageSource = IconImageSource;
                page.BindingContext = BindingContext;
            }
        }
    }

    private sealed record SuppressedNavigationPopFold(
        NavigationState EffectiveState,
        bool HadExternalPop,
        bool LogicalStateChanged,
        AppRoute? Route);

    private sealed class ReleasedPageMarker
    {
    }

    private sealed class ReleasedBranchHostMarker
    {
    }

    private sealed class StartedAsyncDisposal(Task disposal) : IAsyncDisposable
    {
        private Task? _disposal = disposal;

        public ValueTask DisposeAsync()
        {
            Task? pending = Interlocked.Exchange(ref _disposal, null);
            return pending is null ? ValueTask.CompletedTask : new ValueTask(pending);
        }
    }

    private sealed class DestroyedWindowMarker
    {
    }

    private enum MauiPresenterHostState
    {
        Initial,
        Attached,
        Detached,
        AwaitingReplacement,
        Disposed
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }

}

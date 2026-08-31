using System.Runtime.CompilerServices;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Plans;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;
using AdamE.AppNav.Maui.AppLinks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace AdamE.AppNav.Maui;

internal sealed class MauiNavigationPresenter :
    INavigationPresenter,
    IMauiPresentationState,
    IMauiRoutePresentationNavigator
{
    private readonly IMauiRoutePageFactory _pageFactory;
    private readonly MauiRoutePresentationOptions _presentationOptions;
    private readonly IMauiPresentationVerifier _presentationVerifier;
    private readonly IMauiNativeNavigationOperations _nativeOperations;
    private readonly IMauiPresentationOperationPolicy _presentationOperationPolicy;
    private readonly MauiExternalNavigationDispatcher? _externalNavigationDispatcher;
    private readonly NavigationDiagnostics _diagnostics;
    private readonly Dictionary<NavigationPage, string> _navigationPageStackIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<NavigationPage, IReadOnlyList<Page>> _navigationPageKnownPages =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<NavigationPage, SuppressedNavigationPop> _suppressedNavigationPops =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TabbedPage> _trackedTabbedPages = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Page> _trackedModalPages = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<Page, ReleasedPageMarker> _releasedPages = new();
    private readonly Lock _releaseGate = new();
    private readonly SemaphoreSlim _presentationOperationLock = new(1, 1);
    private readonly Lock _lifetimeGate = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly TaskCompletionSource<bool> _shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NavigationState _lastState = NavigationState.Empty;
    private Window? _attachedWindow;
    private string? _attachedWindowId;
    private Window? _destroyingWindow;
    private Page? _destroyingPage;
    private string _lifecycleOperationId = CreateOperationId();
    private string? _activeOperationId;
    private bool _suppressReconciliation;
    private bool _suppressedNavigationPopDrainQueued;
    private bool _hostBackReconciliationPending;
    private AppRoute? _pendingHostBackRoute;
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
        IMauiPresentationOperationPolicy? presentationOperationPolicy = null)
    {
        _pageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
        _presentationOptions = presentationOptions ?? new MauiRoutePresentationOptions();
        _presentationVerifier = presentationVerifier ?? MauiPresentationVerifier.Instance;
        _nativeOperations = nativeOperations ?? MauiNativeNavigationOperations.Instance;
        _presentationOperationPolicy = presentationOperationPolicy ?? new DefaultMauiPresentationOperationPolicy();
        _externalNavigationDispatcher = externalNavigationDispatcher;
        _diagnostics = diagnostics ?? NavigationDiagnostics.None;
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
            if (MainThread.IsMainThread)
            {
                await PushPresentationPageOnMainThreadAsync(typeof(TPage), key, options, operationCancellation);
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(
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
            if (MainThread.IsMainThread)
            {
                return await PopPresentationPageOnMainThreadAsync(animated, operationCancellation);
            }

            return await MainThread.InvokeOnMainThreadAsync(
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
        try
        {
            if (MainThread.IsMainThread)
                await FinalizeShutdownOnMainThreadAsync();
            else
                await MainThread.InvokeOnMainThreadAsync(FinalizeShutdownOnMainThreadAsync);
        }
        catch (Exception ex)
        {
            WritePageReleaseFailure(null, ex);
        }
        finally
        {
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

        InvokeRootPageChanged(null);
        if (currentPage is not null)
            await ReleaseAndDiagnoseAsync(currentPage);
        foreach (Page detachedPage in detachedCandidates)
            await ReleaseAndDiagnoseAsync(detachedPage);

        foreach (NavigationPage navigationPage in _navigationPageStackIds.Keys.ToArray())
            UntrackNavigationPage(navigationPage);
        foreach (TabbedPage tabbedPage in _trackedTabbedPages.ToArray())
            UntrackTabbedPage(tabbedPage);
        foreach (Page modalPage in _trackedModalPages.ToArray())
            UntrackModalPage(modalPage);

        if (_attachedWindow is not null)
        {
            UnsubscribeWindowLifecycle(_attachedWindow);
            _externalNavigationDispatcher?.SetForegrounded(false);
        }

        _attachedWindow = null;
        _attachedWindowId = null;
        _destroyingWindow = null;
        _destroyingPage = null;
        _navigationPageStackIds.Clear();
        _navigationPageKnownPages.Clear();
        _suppressedNavigationPops.Clear();
        _hostBackReconciliationPending = false;
        _pendingHostBackRoute = null;
        _trackedTabbedPages.Clear();
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
            () => AttachWindowOnMainThread(window, windowId),
            cancellationToken);
    }

    public async ValueTask DetachWindowAsync(
        Window window,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);

        await RunSerializedWindowMutationAsync(
            () => DetachWindowOnMainThread(window),
            cancellationToken);
    }

    private void AttachWindowOnMainThread(Window window, string windowId)
    {
        WindowNode? presentedWindow = _lastState.ActiveWindow;
        if (presentedWindow is not null &&
            !StringComparer.Ordinal.Equals(presentedWindow.Id, windowId))
        {
            throw new AppNavigationConfigurationException(
                $"Presented navigation state window id '{presentedWindow.Id}' does not match the MAUI window id '{windowId}'.");
        }

        Window? previousWindow = _attachedWindow;
        TransferCurrentPage(previousWindow, window);

        if (previousWindow is not null && !ReferenceEquals(previousWindow, window))
        {
            UnsubscribeWindowLifecycle(previousWindow);
            if (ReferenceEquals(_destroyingWindow, previousWindow))
            {
                _destroyingWindow = null;
                _destroyingPage = null;
            }
        }

        bool alreadyAttached = ReferenceEquals(previousWindow, window);
        _attachedWindow = window;
        _attachedWindowId = windowId;
        if (!alreadyAttached)
            SubscribeWindowLifecycle(window);

        // A destroyed window cannot be cleared safely. Once its logical page tree has been transferred to a
        // replacement window, the tree is no longer owned by the dying window and native navigation may resume.
        if (_destroyingPage is not null && !ReferenceEquals(_destroyingWindow, window))
        {
            _destroyingWindow = null;
            _destroyingPage = null;
        }

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
            if (ReferenceEquals(_destroyingWindow, window))
            {
                _destroyingWindow = null;
                _destroyingPage = null;
            }
        }
    }

    private void DetachDestroyedWindowOnMainThread(Window window)
    {
        if (!ReferenceEquals(_attachedWindow, window))
        {
            if (ReferenceEquals(_destroyingWindow, window))
            {
                _destroyingWindow = null;
            }
            return;
        }

        // The native window is already being destroyed. Do not write Window.Page here; preserve the
        // logical page tree for a later replacement-window attachment instead.
        UnsubscribeWindowLifecycle(window);
        _externalNavigationDispatcher?.SetForegrounded(false);
        _attachedWindow = null;
        _attachedWindowId = null;
        _destroyingWindow = null;
        // Keep the page marker until a replacement window receives this logical tree. Direct navigation while
        // detached will first build a new tree, so the destroyed window's tree cannot be mutated.
    }

    private async ValueTask RunSerializedWindowMutationAsync(
        Action mutation,
        CancellationToken cancellationToken)
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
            await InvokeOnMainThreadPreservingExecutionContextAsync(() =>
            {
                operationCancellation.ThrowIfCancellationRequested();
                mutation();
                return Task.CompletedTask;
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

        bool sourceIsDestroying = ReferenceEquals(sourceWindow, _destroyingWindow);
        bool destinationIsDestroying = ReferenceEquals(destinationWindow, _destroyingWindow);
        Page? sourcePage = sourceWindow is not null && !sourceIsDestroying
            ? sourceWindow.Page
            : null;
        Page? destinationPage = destinationWindow is not null && !destinationIsDestroying
            ? destinationWindow.Page
            : null;
        bool clearSource = sourceWindow is not null && !sourceIsDestroying &&
                           ReferenceEquals(sourcePage, currentPage);
        bool assignDestination = destinationWindow is not null && !destinationIsDestroying &&
                                 !ReferenceEquals(destinationPage, currentPage);
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
            if (destinationWindow is not null && !destinationIsDestroying &&
                !ReferenceEquals(destinationWindow.Page, destinationPage))
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

            if (sourceWindow is not null && !sourceIsDestroying &&
                !ReferenceEquals(sourceWindow.Page, sourcePage))
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
        if (sender is not Window window || !ReferenceEquals(_attachedWindow, window))
            return;

        // Mark the window before waiting for the serialized cleanup. An active presentation may resume after
        // this callback and must not write Window.Page while the native window is being destroyed.
        _destroyingPage = window.Page ?? CurrentPage;
        _destroyingWindow = window;
        _ = ObserveDestroyedWindowAsync(window);
    }

    private async Task ObserveDestroyedWindowAsync(Window window)
    {
        try
        {
            // Stop accepting external navigation immediately while the serialized detach waits behind
            // any presentation operation already in progress.
            _externalNavigationDispatcher?.SetForegrounded(false);
            await RunSerializedWindowMutationAsync(
                () => DetachDestroyedWindowOnMainThread(window),
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Presenter shutdown cancels queued lifecycle cleanup.
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // Presenter shutdown may win the race with the window destruction callback.
        }
        catch (Exception exception)
        {
            _diagnostics.Write(
                NavigationDiagnosticEventKind.PresentationFailed,
                LifecycleOperationId(),
                "Window destruction cleanup failed.",
                new Dictionary<string, object?>
                {
                    [NavigationDiagnosticDataKeys.ExceptionType] = exception.GetType().FullName,
                    [NavigationDiagnosticDataKeys.ExceptionMessage] = exception.Message
                });
        }
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

    private static Task InvokeOnMainThreadPreservingExecutionContextAsync(Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (MainThread.IsMainThread)
            return callback();

        ExecutionContext executionContext = ExecutionContext.Capture() ??
            throw new InvalidOperationException(
                "MAUI presentation cannot switch to the main thread while execution-context flow is suppressed.");

        return MainThread.InvokeOnMainThreadAsync(() =>
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
        await EnsureDetachedLogicalPageTreeAsync(context.OperationId, cancellationToken);
        bool previousSuppressReconciliation = _suppressReconciliation;
        _suppressReconciliation = true;
        _activeOperationId = context.OperationId;
        var transaction = new MauiPresentationTransaction(this);
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
                        cancellationToken);

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
            _activeTransaction = null;
            await transaction.CommitAsync();
            if (popFold.LogicalStateChanged)
                MarkHostBackReconciliationPending(popFold.Route);
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
            _activePresentationOperation = null;
            _activeOperationId = null;
            _suppressReconciliation = previousSuppressReconciliation;
            if (!previousSuppressReconciliation)
                ScheduleSuppressedNavigationPopDrain();
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
            ValidateMauiNode(targetWindow.Root, "window root");
        }

        if (targetWindow is not null)
        {
            foreach (ModalNode modal in targetWindow.Modals)
            {
                ValidateMauiNode(modal, "window modal");
            }
        }
    }

    private static void ValidateMauiNode(NavigationNode node, string path)
    {
        switch (node)
        {
            case StackNode:
                return;
            case BranchHostNode branchHost:
                foreach (NavigationBranch branch in branchHost.Branches)
                {
                    ValidateMauiNode(branch.Content, $"{path} branch '{branch.Id}'");
                }

                return;
            case ModalNode modal:
                if (modal.Content is not null)
                {
                    ValidateMauiNode(modal.Content, $"{path} content");
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
        _diagnostics.Write(
            NavigationDiagnosticEventKind.PresentationRollbackStarted,
            operationId,
            "Presentation rollback started.");

        try
        {
            if (IsDestroyingAttachedWindow(transaction.PreviousAttachedWindow))
            {
                await RebuildStateFromScratchAsync(
                    transaction.PreviousState,
                    operationId,
                    transaction.PresentationPages);
                await transaction.ReleaseCreatedPagesAsync();
            }
            else
            {
                await transaction.RollbackAsync();
            }

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

            if (_disposed)
                return;

            try
            {
                await RebuildStateFromScratchAsync(
                    transaction.PreviousState,
                    operationId,
                    transaction.PresentationPages);
                await transaction.ReleaseAllNonLivePagesAsync();
            }
            catch (Exception recoveryException)
            {
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

        string operationId = CreateOperationId();
        await EnsureDetachedLogicalPageTreeAsync(operationId, cancellationToken);

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
        var transaction = new MauiPresentationTransaction(this);
        bool previousSuppressReconciliation = _suppressReconciliation;
        string? previousOperationId = _activeOperationId;
        _suppressReconciliation = true;
        _activeOperationId = operationId;
        _activeTransaction = transaction;
        try
        {
            var page = await _pageFactory.CreatePresentationPageAsync(
                pageType,
                owner.RoutePage,
                options.InheritBindingContext,
                cancellationToken);
            if (page is NavigationPage or TabbedPage)
            {
                await _pageFactory.ReleasePresentationPageAsync(page);
                throw new InvalidOperationException(
                    $"Route-owned presentation page '{page.GetType().FullName}' cannot be a navigation container.");
            }

            if (page.Parent is not null)
            {
                await _pageFactory.ReleasePresentationPageAsync(page);
                throw new InvalidOperationException(
                    $"Route-owned presentation page '{page.GetType().FullName}' is already attached to a visual tree.");
            }

            SetPresentationOwnerRouteEntryId(page, owner.RouteEntryId);
            SetPresentationPageKey(page, key);
            SetPresentationPageType(page, pageType);
            transaction.TrackCreated(page);
            WritePageLifecycle(
                NavigationDiagnosticEventKind.PresentationPageCreated,
                page,
                "Route-owned presentation page was created.");

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfNativeNavigationBlocked(navigationPage);
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
            _activeTransaction = null;
            await transaction.CommitAsync();
            if (popFold.LogicalStateChanged)
                MarkHostBackReconciliationPending(popFold.Route);
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
                ScheduleSuppressedNavigationPopDrain();
        }
    }

    private async Task<bool> PopPresentationPageOnMainThreadAsync(
        bool animated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string operationId = CreateOperationId();
        await EnsureDetachedLogicalPageTreeAsync(operationId, cancellationToken);

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
        var transaction = new MauiPresentationTransaction(this);
        bool previousSuppressReconciliation = _suppressReconciliation;
        string? previousOperationId = _activeOperationId;
        _suppressReconciliation = true;
        _activeOperationId = operationId;
        _activeTransaction = transaction;
        transaction.Retire(expectedPage);
        try
        {
            ThrowIfNativeNavigationBlocked(navigationPage);
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
            _activeTransaction = null;
            await transaction.CommitAsync();
            if (popFold.LogicalStateChanged)
                MarkHostBackReconciliationPending(popFold.Route);
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
                ScheduleSuppressedNavigationPopDrain();
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

    private static NavigationPage? ResolveTopNavigationPage(Page? page)
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

        return page switch
        {
            NavigationPage navigationPage => navigationPage,
            TabbedPage tabbedPage when tabbedPage.CurrentPage is not null =>
                ResolveTopNavigationPage(tabbedPage.CurrentPage),
            _ => null
        };
    }

    private async Task<Page> MaterializeNodeAsync(
        NavigationNode node,
        Page? existingPage,
        string operationId,
        bool isNavigationTarget,
        CancellationToken cancellationToken,
        bool wasResurfacedTarget = false,
        IReadOnlyList<PresentationPageRecovery>? presentationPages = null)
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
                wasResurfacedTarget,
                presentationPages),
            BranchHostNode branchHost => await MaterializeTabbedBranchHostAsync(
                branchHost,
                existingPage as TabbedPage,
                operationId,
                isNavigationTarget,
                cancellationToken,
                wasResurfacedTarget,
                presentationPages),
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
                presentationPages),
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
        bool wasResurfacedTarget = false,
        IReadOnlyList<PresentationPageRecovery>? presentationPages = null)
    {
        if (stack.Entries.Count == 0)
        {
            return CreateEmptyPage();
        }

        if (existingPage is not null &&
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
        _activeTransaction?.TrackCreated(navigationPage);
        SetHostId(navigationPage, stack.Id);
        SetRouteEntryId(root, stack.Entries[0].Id);
        WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageCreated, navigationPage, "NavigationPage was created.");
        TrackNavigationPage(navigationPage, stack.Id);

        await RestorePresentationPagesForRouteAsync(
            navigationPage,
            root,
            presentationPages,
            cancellationToken);

        for (var i = 1; i < stack.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfNativeNavigationBlocked(navigationPage);
            var page = await CreateRoutePageAsync(stack.Entries[i], cancellationToken);
            await _nativeOperations.PushAsync(navigationPage, page, animated: false);

            await RestorePresentationPagesForRouteAsync(
                navigationPage,
                page,
                presentationPages,
                cancellationToken);
        }

        UpdateKnownNavigationPages(navigationPage);
        return navigationPage;
    }

    private async Task RestorePresentationPagesForRouteAsync(
        NavigationPage navigationPage,
        Page routePage,
        IReadOnlyList<PresentationPageRecovery>? presentationPages,
        CancellationToken cancellationToken)
    {
        if (presentationPages is null)
        {
            return;
        }

        foreach (PresentationPageRecovery recoveryPage in presentationPages.Where(page =>
                     StringComparer.Ordinal.Equals(page.HostId, GetHostId(navigationPage)) &&
                     StringComparer.Ordinal.Equals(page.OwnerRouteEntryId, GetRouteEntryId(routePage))))
        {
            Page page = await CreateRecoveredPresentationPageAsync(
                recoveryPage,
                routePage,
                _activeTransaction,
                cancellationToken);
            ThrowIfNativeNavigationBlocked(navigationPage);
            await _nativeOperations.PushAsync(navigationPage, page, animated: false);
        }
    }

    private async Task<Page> CreateRecoveredPresentationPageAsync(
        PresentationPageRecovery recoveryPage,
        Page ownerRoutePage,
        MauiPresentationTransaction? transaction,
        CancellationToken cancellationToken)
    {
        Page page = await _pageFactory.CreatePresentationPageAsync(
            recoveryPage.ServiceType,
            ownerRoutePage,
            recoveryPage.InheritBindingContext,
            cancellationToken);
        if (page is NavigationPage or TabbedPage)
        {
            await _pageFactory.ReleasePresentationPageAsync(page);
            throw new InvalidOperationException(
                $"Route-owned presentation page '{page.GetType().FullName}' cannot be a navigation container.");
        }

        if (page.Parent is not null)
        {
            await _pageFactory.ReleasePresentationPageAsync(page);
            throw new InvalidOperationException(
                $"Route-owned presentation page '{page.GetType().FullName}' is already attached to a visual tree.");
        }

        SetPresentationOwnerRouteEntryId(page, recoveryPage.OwnerRouteEntryId);
        SetPresentationPageKey(page, recoveryPage.Key);
        SetPresentationPageType(page, recoveryPage.ServiceType);
        page.Title = recoveryPage.Title;
        page.IconImageSource = recoveryPage.IconImageSource;

        transaction?.TrackCreated(page);
        WritePageLifecycle(
            NavigationDiagnosticEventKind.PresentationPageCreated,
            page,
            "Recovered route-owned presentation page was created.");
        return page;
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
            ThrowIfNativeNavigationBlocked(navigationPage);
            var removed = await _nativeOperations.PopAsync(navigationPage, animatePop);
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
            ThrowIfNativeNavigationBlocked(navigationPage);
            await _nativeOperations.PushAsync(navigationPage, page, animatePush);
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

    private async Task<Page> MaterializeTabbedBranchHostAsync(
        BranchHostNode branchHost,
        TabbedPage? existingPage,
        string operationId,
        bool isNavigationTarget,
        CancellationToken cancellationToken,
        bool wasResurfacedTarget = false,
        IReadOnlyList<PresentationPageRecovery>? presentationPages = null)
    {
        var tabbedPage = existingPage is not null && StringComparer.Ordinal.Equals(GetHostId(existingPage), branchHost.Id)
            ? existingPage
            : new TabbedPage();
        var createdTabbedPage = !ReferenceEquals(tabbedPage, existingPage);

        SetHostId(tabbedPage, branchHost.Id);

        if (createdTabbedPage)
        {
            _activeTransaction?.TrackCreated(tabbedPage);
            WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageCreated, tabbedPage, "TabbedPage was created.");
        }

        TrackTabbedPage(tabbedPage);

        var stagedBranches = new List<(NavigationBranch Branch, Page? ExistingPage, Page Page)>(
            branchHost.Branches.Count);
        for (var i = 0; i < branchHost.Branches.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var branch = branchHost.Branches[i];
            var existingBranchPage = tabbedPage.Children.FirstOrDefault(child =>
                StringComparer.Ordinal.Equals(GetBranchId(child), branch.Id));
            var page = await MaterializeNodeAsync(
                branch.Content,
                existingBranchPage,
                operationId,
                isNavigationTarget && StringComparer.Ordinal.Equals(branch.Id, branchHost.SelectedBranchId),
                cancellationToken,
                wasResurfacedTarget,
                presentationPages);
            stagedBranches.Add((branch, existingBranchPage, page));
        }

        var desiredBranchIds = branchHost.Branches
            .Select(branch => branch.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (Page stalePage in tabbedPage.Children
                     .Where(child => GetBranchId(child) is not { } branchId || !desiredBranchIds.Contains(branchId))
                     .ToArray())
        {
            ThrowIfNativeNavigationBlocked(tabbedPage);
            _nativeOperations.RemoveTab(tabbedPage, stalePage);
            await DetachPageTreeAsync(stalePage);
        }

        Page? selectedPage = null;
        for (var i = 0; i < stagedBranches.Count; i++)
        {
            (NavigationBranch branch, Page? existingBranchPage, Page page) = stagedBranches[i];
            ApplyBranchChrome(page, branch);
            SetBranchId(page, branch.Id);

            if (existingBranchPage is not null && !ReferenceEquals(existingBranchPage, page))
            {
                ThrowIfNativeNavigationBlocked(tabbedPage);
                _nativeOperations.RemoveTab(tabbedPage, existingBranchPage);
                await DetachPageTreeAsync(existingBranchPage);
            }

            var currentIndex = tabbedPage.Children.IndexOf(page);
            if (currentIndex < 0)
            {
                ThrowIfNativeNavigationBlocked(tabbedPage);
                _nativeOperations.InsertTab(tabbedPage, Math.Min(i, tabbedPage.Children.Count), page);
            }
            else if (currentIndex != i)
            {
                ThrowIfNativeNavigationBlocked(tabbedPage);
                _nativeOperations.RemoveTab(tabbedPage, page);
                ThrowIfNativeNavigationBlocked(tabbedPage);
                _nativeOperations.InsertTab(tabbedPage, Math.Min(i, tabbedPage.Children.Count), page);
            }

            if (StringComparer.Ordinal.Equals(branch.Id, branchHost.SelectedBranchId))
            {
                selectedPage = page;
            }
        }

        ThrowIfNativeNavigationBlocked(tabbedPage);
        _nativeOperations.SetCurrentTab(tabbedPage, selectedPage ?? tabbedPage.Children.FirstOrDefault());
        return tabbedPage;
    }

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
        CancellationToken cancellationToken,
        IReadOnlyList<PresentationPageRecovery>? presentationPages = null)
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
                    presentationPages: presentationPages);
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
            ThrowIfNativeNavigationBlocked(root);
            var removed = await _nativeOperations.PopModalAsync(root, animatePop);
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
            ThrowIfNativeNavigationBlocked(root);
            await _nativeOperations.PushModalAsync(root, modalPage, animatePush);
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
                               StringComparer.Ordinal.Equals(GetHostId(navigationPage), stack.Id) &&
                               StackRootMatches(navigationPage, stack),
            BranchHostNode branchHost => existingPage is TabbedPage tabbedPage &&
                                         StringComparer.Ordinal.Equals(GetHostId(tabbedPage), branchHost.Id),
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
        var page = await _pageFactory.CreatePageAsync(entry, cancellationToken);
        _activeTransaction?.TrackCreated(page);
        SetRouteEntryId(page, entry.Id);
        WritePageLifecycle(NavigationDiagnosticEventKind.PresentationPageCreated, page, "Route page was created.");
        return page;
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
                wasResurfacedTarget: previousModalCount > modals.Count);
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
        _activeTransaction?.RecordUpdate(page);
        SetRouteEntryId(page, entry.Id);
        await _pageFactory.UpdatePageAsync(page, entry, context, cancellationToken);
    }

    private Page CreateOrReuseEmptyRootHost(Page? existingPage)
    {
        return existingPage is not null &&
               existingPage is not NavigationPage &&
               existingPage is not TabbedPage
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
        if (_attachedWindow is null || ReferenceEquals(_attachedWindow, _destroyingWindow) ||
            ReferenceEquals(_attachedWindow.Page, page))
        {
            return;
        }

        _nativeOperations.SetWindowPage(_attachedWindow, page);
    }

    private bool IsDestroyingAttachedWindow(Window? window)
    {
        return window is not null &&
               ReferenceEquals(_attachedWindow, window) &&
               ReferenceEquals(_destroyingWindow, window);
    }

    private void ThrowIfNativeNavigationBlocked(Page page)
    {
        if (_destroyingPage is null || !ContainsPageInStructuralTree(_destroyingPage, page))
        {
            return;
        }

        throw new OperationCanceledException(
            "Native MAUI navigation was canceled because the attached window is being destroyed or has been detached.");
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

    private static Page? ResolveTopPresentedPage(Page? page)
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

            var next = current switch
            {
                NavigationPage navigationPage when navigationPage.CurrentPage is not null => navigationPage.CurrentPage,
                TabbedPage tabbedPage when tabbedPage.CurrentPage is not null => tabbedPage.CurrentPage,
                _ => null
            };

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

    private void TrackTabbedPage(TabbedPage tabbedPage)
    {
        tabbedPage.CurrentPageChanged -= OnTabbedPageCurrentPageChanged;
        tabbedPage.CurrentPageChanged += OnTabbedPageCurrentPageChanged;

        if (_trackedTabbedPages.Add(tabbedPage))
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerAttached,
                tabbedPage,
                "TabbedPage.CurrentPageChanged",
                "TabbedPage selection handler was attached.");
        }
    }

    private void UntrackTabbedPage(TabbedPage tabbedPage)
    {
        tabbedPage.CurrentPageChanged -= OnTabbedPageCurrentPageChanged;

        if (_trackedTabbedPages.Remove(tabbedPage))
        {
            WriteHandlerLifecycle(
                NavigationDiagnosticEventKind.PresentationHandlerDetached,
                tabbedPage,
                "TabbedPage.CurrentPageChanged",
                "TabbedPage selection handler was detached.");
        }
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

    private async ValueTask DetachPageTreeWithFailuresAsync(Page page)
    {
        var failures = new List<Exception>();
        await DetachPageTreeAsync(
            page,
            new HashSet<Page>(ReferenceEqualityComparer.Instance),
            failures);
        if (failures.Count > 0)
            throw new AggregateException("One or more pages could not be fully released.", failures);
    }

    private async ValueTask DetachPageTreeAsync(
        Page page,
        HashSet<Page> visited,
        List<Exception> failures)
    {
        if (!visited.Add(page))
        {
            return;
        }

        var shouldRelease = MarkPageReleased(page);
        if (shouldRelease)
        {
            try
            {
                page.BindingContext = null;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        UntrackModalPage(page);

        foreach (var modalPage in page.Navigation.ModalStack.ToArray())
        {
            if (!ReferenceEquals(modalPage, page))
            {
                await DetachPageTreeAsync(modalPage, visited, failures);
            }
        }

        switch (page)
        {
            case NavigationPage navigationPage:
                UntrackNavigationPage(navigationPage);
                foreach (var child in navigationPage.Navigation.NavigationStack.Reverse().ToArray())
                {
                    await DetachPageTreeAsync(child, visited, failures);
                }

                break;
            case TabbedPage tabbedPage:
                UntrackTabbedPage(tabbedPage);
                foreach (var child in tabbedPage.Children.ToArray())
                {
                    await DetachPageTreeAsync(child, visited, failures);
                }

                break;
            default:
                if (shouldRelease)
                {
                    try
                    {
                        if (IsPresentationPage(page))
                            await _pageFactory.ReleasePresentationPageAsync(page);
                        else
                            await _pageFactory.ReleasePageAsync(page);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                }

                break;
        }
    }

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

        QueueNativeCleanup(async () =>
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
                ThrowIfNativeNavigationBlocked(navigationPage);
                Page? removed = await _nativeOperations.PopAsync(navigationPage, animated: false);
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

    private void DiscardSuppressedNavigationPops()
    {
        _suppressedNavigationPops.Clear();
    }

    private void ScheduleSuppressedNavigationPopDrain()
    {
        if (_suppressedNavigationPopDrainQueued ||
            (_suppressedNavigationPops.Count == 0 && !_hostBackReconciliationPending))
        {
            return;
        }

        _suppressedNavigationPopDrainQueued = true;
        if (!QueueNativeCleanup(DrainSuppressedNavigationPopsAsync))
            _suppressedNavigationPopDrainQueued = false;
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

        ScheduleSuppressedNavigationPopDrain();
    }

    private void OnTabbedPageCurrentPageChanged(object? sender, EventArgs e)
    {
        if (_suppressReconciliation || sender is not TabbedPage tabbedPage || tabbedPage.CurrentPage is null)
        {
            return;
        }

        QueueNativeCleanup(() =>
        {
            ReconcileTabSelection(tabbedPage);
            return Task.CompletedTask;
        });
    }

    private void ReconcileTabSelection(TabbedPage tabbedPage)
    {
        if (_suppressReconciliation || tabbedPage.CurrentPage is null)
            return;

        var selectedBranchId = GetBranchId(tabbedPage.CurrentPage);
        var branchHostId = GetHostId(tabbedPage);
        if (string.IsNullOrWhiteSpace(selectedBranchId) || string.IsNullOrWhiteSpace(branchHostId))
        {
            return;
        }

        var updatedWindow = UpdateWindowForPresentedNode(
            tabbedPage,
            node => UpdateBranchHostSelection(node, branchHostId, selectedBranchId));
        if (updatedWindow is null)
        {
            return;
        }

        var updatedState = _lastState.ReplaceWindow(updatedWindow);
        RequestReconciliation(updatedState, NavigationReconciliationSource.BranchChanged, "Native tab selection changed.");
    }

    private void OnModalPageDisappearing(object? sender, EventArgs e)
    {
        if (_suppressReconciliation || _disposed || sender is not Page page)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => QueueNativeCleanup(
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
        if (!TryBeginOperation())
            return false;

        _ = RunQueuedNativeCleanupAsync(cleanup);
        return true;
    }

    private async Task RunQueuedNativeCleanupAsync(Func<Task> cleanup)
    {
        var lockTaken = false;
        try
        {
            await _presentationOperationLock.WaitAsync(_shutdownCancellation.Token).ConfigureAwait(false);
            lockTaken = true;
            _shutdownCancellation.Token.ThrowIfCancellationRequested();
            if (MainThread.IsMainThread)
                await cleanup();
            else
                await MainThread.InvokeOnMainThreadAsync(cleanup);
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

    private static bool ContainsPageInStructuralTree(Page root, Page target)
    {
        return ContainsPageInStructuralTree(
            root,
            target,
            new HashSet<Page>(ReferenceEqualityComparer.Instance));
    }

    private static bool ContainsPageInStructuralTree(
        Page root,
        Page target,
        HashSet<Page> visited)
    {
        if (!visited.Add(root))
        {
            return false;
        }

        if (ReferenceEquals(root, target))
        {
            return true;
        }

        foreach (Page modalPage in root.Navigation.ModalStack)
        {
            if (!ReferenceEquals(modalPage, root) &&
                ContainsPageInStructuralTree(modalPage, target, visited))
            {
                return true;
            }
        }

        return root switch
        {
            NavigationPage navigationPage => navigationPage.Navigation.NavigationStack.Any(
                page => ContainsPageInStructuralTree(page, target, visited)),
            TabbedPage tabbedPage => tabbedPage.Children.Any(
                page => ContainsPageInStructuralTree(page, target, visited)),
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

    private async Task EnsureDetachedLogicalPageTreeAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        if (_attachedWindow is not null ||
            _destroyingPage is null ||
            CurrentPage is null ||
            !ReferenceEquals(_destroyingPage, CurrentPage))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new MauiPresentationTransaction(this);
        await RebuildStateFromScratchAsync(
            _lastState,
            operationId,
            snapshot.PresentationPages);
    }

    private async Task RebuildStateFromScratchAsync(
        NavigationState state,
        string operationId,
        IReadOnlyList<PresentationPageRecovery>? presentationPages = null)
    {
        Page? failedRoot = CurrentPage;
        Page? rebuiltRoot = null;
        var recoveryTransaction = new MauiPresentationTransaction(this);
        _activeTransaction = recoveryTransaction;
        try
        {
            IReadOnlyList<PresentationPageRecovery> recoveredPresentationPages =
                presentationPages ?? recoveryTransaction.PresentationPages;
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
                        CancellationToken.None,
                        presentationPages: recoveredPresentationPages);
                await ApplyModalsAsync(
                    rebuiltRoot,
                    window.Modals,
                    operationId,
                    CancellationToken.None,
                    recoveredPresentationPages);

                await RestorePresentationPagesAsync(
                    rebuiltRoot,
                    recoveredPresentationPages,
                    recoveryTransaction,
                    CancellationToken.None);
            }

            _activeTransaction = null;
            CurrentPage = rebuiltRoot;
            SetAttachedWindowPage(rebuiltRoot);
            VerifyPresentation(state, operationId);
            _lastState = state;
            RebuildTrackingFromCurrentPage();

            if (!ReferenceEquals(failedRoot, rebuiltRoot) && failedRoot is not null)
                await ReleaseAndDiagnoseAsync(failedRoot);

            InvokeRootPageChanged(rebuiltRoot);
        }
        catch
        {
            _activeTransaction = null;
            await recoveryTransaction.ReleaseCreatedPagesAsync();

            throw;
        }
    }

    private async Task RestorePresentationPagesAsync(
        Page root,
        IReadOnlyList<PresentationPageRecovery> presentationPages,
        MauiPresentationTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (PresentationPageRecovery recoveryPage in presentationPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NavigationPage? navigationPage = EnumerateStructuralPages(root)
                .OfType<NavigationPage>()
                .FirstOrDefault(candidate =>
                {
                    if (!StringComparer.Ordinal.Equals(GetHostId(candidate), recoveryPage.HostId))
                    {
                        return false;
                    }

                    MauiNavigationStackProjection projection =
                        MauiNavigationStackProjection.Create(candidate.Navigation.NavigationStack);
                    return projection.IsValid && projection.Segments.Any(segment =>
                        StringComparer.Ordinal.Equals(segment.RouteEntryId, recoveryPage.OwnerRouteEntryId));
                });

            if (navigationPage is null)
            {
                throw new InvalidOperationException(
                    $"The recovered navigation tree has no host for presentation page '{recoveryPage.Key}'.");
            }

            MauiNavigationStackProjection currentProjection =
                RequireValidProjection(navigationPage.Navigation.NavigationStack);
            MauiNavigationStackSegment owner = currentProjection.Segments.First(segment =>
                StringComparer.Ordinal.Equals(segment.RouteEntryId, recoveryPage.OwnerRouteEntryId));
            if (owner.PresentationPages.Any(page =>
                    StringComparer.Ordinal.Equals(GetPresentationPageKey(page), recoveryPage.Key)))
            {
                continue;
            }

            Page page = await CreateRecoveredPresentationPageAsync(
                recoveryPage,
                owner.RoutePage,
                transaction,
                cancellationToken);
            ThrowIfNativeNavigationBlocked(navigationPage);
            await _nativeOperations.PushAsync(navigationPage, page, animated: false);
            UpdateKnownNavigationPages(navigationPage);
        }
    }

    private static IEnumerable<Page> EnumerateStructuralPages(Page root)
    {
        var pending = new Stack<Page>();
        var visited = new HashSet<Page>(ReferenceEqualityComparer.Instance);
        pending.Push(root);

        while (pending.Count > 0)
        {
            Page page = pending.Pop();
            if (!visited.Add(page))
            {
                continue;
            }

            yield return page;

            foreach (Page modalPage in page.Navigation.ModalStack.Reverse())
            {
                if (!ReferenceEquals(modalPage, page))
                {
                    pending.Push(modalPage);
                }
            }

            switch (page)
            {
                case NavigationPage navigationPage:
                    foreach (Page child in navigationPage.Navigation.NavigationStack.Reverse())
                    {
                        pending.Push(child);
                    }

                    break;
                case TabbedPage tabbedPage:
                    foreach (Page child in tabbedPage.Children.Reverse())
                    {
                        pending.Push(child);
                    }

                    break;
            }
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

    private static void CollectStructuralPages(Page page, HashSet<Page> pages)
    {
        if (!pages.Add(page))
            return;

        foreach (Page modalPage in page.Navigation.ModalStack)
        {
            if (!ReferenceEquals(modalPage, page))
            {
                CollectStructuralPages(modalPage, pages);
            }
        }

        switch (page)
        {
            case NavigationPage navigationPage:
                foreach (Page child in navigationPage.Navigation.NavigationStack)
                    CollectStructuralPages(child, pages);
                break;
            case TabbedPage tabbedPage:
                foreach (Page child in tabbedPage.Children)
                    CollectStructuralPages(child, pages);
                break;
        }
    }

    private void RebuildTrackingFromCurrentPage()
    {
        foreach (NavigationPage navigationPage in _navigationPageStackIds.Keys.ToArray())
            UntrackNavigationPage(navigationPage);
        foreach (TabbedPage tabbedPage in _trackedTabbedPages.ToArray())
            UntrackTabbedPage(tabbedPage);
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

        switch (page)
        {
            case NavigationPage navigationPage:
                if (GetHostId(navigationPage) is { } stackId)
                    TrackNavigationPage(navigationPage, stackId);
                foreach (Page child in navigationPage.Navigation.NavigationStack)
                    TrackStructuralPage(child, visited);
                break;
            case TabbedPage tabbedPage:
                TrackTabbedPage(tabbedPage);
                foreach (Page child in tabbedPage.Children)
                    TrackStructuralPage(child, visited);
                break;
        }

        foreach (Page modalPage in page.Navigation.ModalStack)
        {
            if (!ReferenceEquals(modalPage, page))
            {
                TrackModalPage(modalPage);
                TrackStructuralPage(modalPage, visited);
            }
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
        Window? windowForVerification = ReferenceEquals(_attachedWindow, _destroyingWindow)
            ? null
            : _attachedWindow;
        var mismatch = _presentationVerifier.Verify(new MauiPresentationVerificationContext(
            targetState,
            CurrentPage,
            windowForVerification,
            _presentationOptions));
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

    private static void SetPresentationPageType(BindableObject bindableObject, Type type)
    {
        MauiPresentationMetadata.SetPresentationPageType(bindableObject, type);
    }

    private static Type? GetPresentationPageType(BindableObject? bindableObject)
    {
        return MauiPresentationMetadata.GetPresentationPageType(bindableObject);
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

    private sealed record PresentationPageRecovery(
        string HostId,
        string OwnerRouteEntryId,
        string Key,
        Type ServiceType,
        bool InheritBindingContext,
        string? Title,
        ImageSource? IconImageSource);

    private sealed class MauiPresentationTransaction
    {
        private readonly MauiNavigationPresenter _presenter;
        private readonly Page? _previousCurrentPage;
        private readonly Window? _previousAttachedWindow;
        private readonly Page? _previousWindowPage;
        private readonly Page[] _previousModals;
        private readonly Dictionary<NavigationPage, Page[]> _navigationStacks =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<TabbedPage, TabSnapshot> _tabs =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Page, PageSnapshot> _pageSnapshots =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Page, RouteEntry> _updatedPages =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, RouteEntry> _previousEntries;
        private readonly HashSet<Page> _createdPages = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Page> _retiredPages = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Page> _capturedPresentationPages =
            new(ReferenceEqualityComparer.Instance);
        private readonly List<PresentationPageRecovery> _presentationPages = [];
        private bool _rootChanged;

        public MauiPresentationTransaction(MauiNavigationPresenter presenter)
        {
            _presenter = presenter;
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

        public Window? PreviousAttachedWindow => _previousAttachedWindow;

        public IReadOnlyList<PresentationPageRecovery> PresentationPages => _presentationPages;

        public void TrackCreated(Page page) => _createdPages.Add(page);

        public void Retire(Page page) => _retiredPages.Add(page);

        public bool IsRetired(Page page) => _retiredPages.Contains(page);

        public void RecordRootChange() => _rootChanged = true;

        public void RecordUpdate(Page page)
        {
            if (_updatedPages.ContainsKey(page))
                return;

            string? entryId = GetRouteEntryId(page);
            if (entryId is not null && _previousEntries.TryGetValue(entryId, out RouteEntry? entry))
                _updatedPages.Add(page, entry);
        }

        public async ValueTask CommitAsync()
        {
            HashSet<Page> livePages = _presenter.CollectLivePages(_presenter.CurrentPage);
            var releaseCandidates = new HashSet<Page>(_retiredPages, ReferenceEqualityComparer.Instance);
            foreach (Page createdPage in _createdPages)
                if (!livePages.Contains(createdPage))
                    releaseCandidates.Add(createdPage);

            foreach (Page page in releaseCandidates)
                if (!livePages.Contains(page))
                    await _presenter.ReleaseAndDiagnoseAsync(page);

            if (_rootChanged)
                _presenter.InvokeRootPageChanged(_presenter.CurrentPage);
        }

        public async ValueTask RollbackAsync()
        {
            if (!ReferenceEquals(_presenter._attachedWindow, _previousAttachedWindow))
            {
                throw new InvalidOperationException(
                    "The attached MAUI window changed during a serialized presentation transaction.");
            }

            _presenter.CurrentPage = _previousCurrentPage;
            if (_previousAttachedWindow is not null &&
                !ReferenceEquals(_presenter._destroyingWindow, _previousAttachedWindow))
                _presenter._nativeOperations.SetWindowPage(
                    _previousAttachedWindow,
                    _previousWindowPage);

            foreach ((TabbedPage tabbedPage, TabSnapshot snapshot) in _tabs)
            {
                foreach (Page currentPage in tabbedPage.Children.ToArray())
                {
                    _presenter.ThrowIfNativeNavigationBlocked(tabbedPage);
                    _presenter._nativeOperations.RemoveTab(tabbedPage, currentPage);
                }
                for (var index = 0; index < snapshot.Children.Length; index++)
                {
                    _presenter.ThrowIfNativeNavigationBlocked(tabbedPage);
                    _presenter._nativeOperations.InsertTab(tabbedPage, index, snapshot.Children[index]);
                }
                _presenter.ThrowIfNativeNavigationBlocked(tabbedPage);
                _presenter._nativeOperations.SetCurrentTab(tabbedPage, snapshot.CurrentPage);
            }

            foreach ((NavigationPage navigationPage, Page[] pages) in _navigationStacks)
            {
                while (navigationPage.Navigation.NavigationStack.Count > 1)
                {
                    int previousCount = navigationPage.Navigation.NavigationStack.Count;
                    _presenter.ThrowIfNativeNavigationBlocked(navigationPage);
                    await _presenter._nativeOperations.PopAsync(navigationPage, animated: false);
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
                    _presenter.ThrowIfNativeNavigationBlocked(navigationPage);
                    await _presenter._nativeOperations.PushAsync(navigationPage, pages[index], animated: false);
                }
            }

            if (_previousCurrentPage is not null)
            {
                while (_previousCurrentPage.Navigation.ModalStack.Count > 0)
                {
                    int previousCount = _previousCurrentPage.Navigation.ModalStack.Count;
                    _presenter.ThrowIfNativeNavigationBlocked(_previousCurrentPage);
                    await _presenter._nativeOperations.PopModalAsync(_previousCurrentPage, animated: false);
                    if (_previousCurrentPage.Navigation.ModalStack.Count >= previousCount)
                        throw new InvalidOperationException("Modal rollback did not remove a page.");
                }

                foreach (Page modal in _previousModals)
                {
                    _presenter.ThrowIfNativeNavigationBlocked(_previousCurrentPage);
                    await _presenter._nativeOperations.PushModalAsync(
                        _previousCurrentPage,
                        modal,
                        animated: false);
                }
            }

            foreach ((Page page, RouteEntry entry) in _updatedPages)
            {
                SetRouteEntryId(page, entry.Id);
                await _presenter._pageFactory.UpdatePageAsync(
                    page,
                    entry,
                    new MauiRoutePageUpdateContext(MauiRoutePageReuseKind.NonTargetReuse),
                    CancellationToken.None);
            }

            foreach ((Page page, PageSnapshot snapshot) in _pageSnapshots)
                snapshot.Restore(page);

            _presenter._lastState = PreviousState;
            _presenter.RebuildTrackingFromCurrentPage();
            _presenter.VerifyPresentation(PreviousState, _presenter.LifecycleOperationId());

            HashSet<Page> livePages = _presenter.CollectLivePages(_presenter.CurrentPage);
            foreach (Page page in _createdPages)
                if (!livePages.Contains(page))
                    await _presenter.ReleaseAndDiagnoseAsync(page);
        }

        public async ValueTask ReleaseCreatedPagesAsync()
        {
            foreach (Page page in _createdPages)
                await _presenter.ReleaseAndDiagnoseAsync(page);
        }

        public async ValueTask ReleaseAllNonLivePagesAsync()
        {
            HashSet<Page> livePages = _presenter.CollectLivePages(_presenter.CurrentPage);
            var candidates = new HashSet<Page>(_pageSnapshots.Keys, ReferenceEqualityComparer.Instance);
            candidates.UnionWith(_retiredPages);
            candidates.UnionWith(_createdPages);
            foreach (Page page in candidates)
                if (!livePages.Contains(page))
                    await _presenter.ReleaseAndDiagnoseAsync(page);
        }

        private void CapturePage(Page page, HashSet<Page> visited)
        {
            if (!visited.Add(page))
                return;

            _pageSnapshots[page] = PageSnapshot.Capture(page);
            switch (page)
            {
                case NavigationPage navigationPage:
                    Page[] stack = navigationPage.Navigation.NavigationStack.ToArray();
                    _navigationStacks[navigationPage] = stack;
                    CapturePresentationPages(navigationPage, stack);
                    foreach (Page child in stack)
                        CapturePage(child, visited);
                    break;
                case TabbedPage tabbedPage:
                    Page[] children = tabbedPage.Children.ToArray();
                    _tabs[tabbedPage] = new TabSnapshot(children, tabbedPage.CurrentPage);
                    foreach (Page child in children)
                        CapturePage(child, visited);
                    break;
            }

            foreach (Page modalPage in page.Navigation.ModalStack)
            {
                if (!ReferenceEquals(modalPage, page))
                {
                    CapturePage(modalPage, visited);
                }
            }
        }

        private void CapturePresentationPages(NavigationPage navigationPage, IReadOnlyList<Page> stack)
        {
            string? hostId = GetHostId(navigationPage);
            if (string.IsNullOrWhiteSpace(hostId))
            {
                return;
            }

            MauiNavigationStackProjection projection = MauiNavigationStackProjection.Create(stack);
            if (!projection.IsValid)
            {
                return;
            }

            foreach (MauiNavigationStackSegment segment in projection.Segments)
            {
                foreach (Page page in segment.PresentationPages)
                {
                    if (!_capturedPresentationPages.Add(page))
                    {
                        continue;
                    }

                    _presentationPages.Add(new PresentationPageRecovery(
                        hostId,
                        segment.RouteEntryId,
                        GetPresentationPageKey(page)!,
                        GetPresentationPageType(page) ?? page.GetType(),
                        ReferenceEquals(page.BindingContext, segment.RoutePage.BindingContext),
                        page.Title,
                        page.IconImageSource));
                }
            }
        }

        private sealed record TabSnapshot(Page[] Children, Page? CurrentPage);

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

    private sealed record SuppressedNavigationPop(
        NavigationPage NavigationPage,
        string? WindowId,
        string StackId,
        string? OwnerModalId,
        bool IsNavigationTarget,
        IReadOnlyList<Page> KnownPages,
        IReadOnlyList<Page> RemainingPages);

    private sealed record SuppressedNavigationPopFold(
        NavigationState EffectiveState,
        bool HadExternalPop,
        bool LogicalStateChanged,
        AppRoute? Route);

    private sealed class ReleasedPageMarker
    {
    }

}

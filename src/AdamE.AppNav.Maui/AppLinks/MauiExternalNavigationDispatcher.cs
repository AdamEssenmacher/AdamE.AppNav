using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Requests;
using Microsoft.Extensions.DependencyInjection;

namespace AdamE.AppNav.Maui.AppLinks;

internal sealed class MauiExternalNavigationDispatcher : IMauiExternalNavigationDispatcher, IDisposable
{
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private readonly IServiceProvider _services;
    private readonly NavigationDiagnostics _diagnostics;
    private readonly MauiExternalNavigationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly LinkedList<PendingRequest> _pending = [];
    private readonly HashSet<RouterNavigationRequest> _deduped =
        new(MauiNavigationRequestEquivalenceComparer.Instance);
    private TaskCompletionSource<bool> _pendingRequestAvailable = NewSignal();
    private TaskCompletionSource<bool> _queueChanged = NewSignal();
    private CancellationTokenSource? _activeDrainCancellation;
    private PendingRequest? _inFlight;
    private bool _ready;
    private bool _foregrounded;
    private bool _drainScheduled;
    private bool _disposed;

    public MauiExternalNavigationDispatcher(
        IServiceProvider services,
        NavigationDiagnostics diagnostics,
        MauiExternalNavigationOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _diagnostics = diagnostics ?? NavigationDiagnostics.None;
        _options = options ?? new MauiExternalNavigationOptions();
        _options.ValidateLimits();
        _timeProvider = timeProvider ?? TimeProvider.System;

        MauiExternalNavigationBridgeRegistration bootstrap = MauiExternalNavigationBridge.Register(this);
        foreach (MauiExternalNavigationBootstrapDiagnostic diagnostic in bootstrap.Diagnostics)
            WriteBootstrapDiagnostic(diagnostic);
        foreach (RouterNavigationRequest request in bootstrap.Requests)
            TryDispatch(request);
    }

    public bool HasPendingRequests
    {
        get
        {
            PruneExpiredPending(_timeProvider.GetUtcNow());
            lock (_gate)
                return !_disposed && (_pending.Count > 0 || _inFlight is not null);
        }
    }

    public bool TryDispatch(RouterNavigationRequest? request)
    {
        if (request is null)
            return false;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        MauiExternalNavigationRejectionReason rejectionReason;
        try
        {
            if (!_options.TryAccept(request, now, out rejectionReason))
            {
                WriteRejected(request, rejectionReason);
                return false;
            }
        }
        catch (Exception ex)
        {
            WriteRejected(
                request,
                MauiExternalNavigationRejectionReason.ApplicationFilter,
                ex.GetType());
            return false;
        }

        return TryEnqueueAccepted(request, now);
    }

    public async ValueTask<bool> WaitForPendingRequestAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout cannot be negative.");

        PruneExpiredPending(_timeProvider.GetUtcNow());
        Task waitTask;
        lock (_gate)
        {
            if (_disposed)
                return false;
            if (_pending.Count > 0 || _inFlight is not null)
                return true;
            if (timeout == TimeSpan.Zero)
                return false;

            if (_pendingRequestAvailable.Task.IsCompleted)
                _pendingRequestAvailable = NewSignal();
            waitTask = _pendingRequestAvailable.Task;
        }

        try
        {
            await waitTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return HasPendingRequests;
        }

        return HasPendingRequests;
    }

    public void MarkReady()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _ready = true;
        }

        SignalQueueChanged();
        ScheduleDrainIfReady();
    }

    public void SetForegrounded(bool foregrounded)
    {
        CancellationTokenSource? activeDrainCancellation = null;
        lock (_gate)
        {
            if (_disposed)
                return;

            _foregrounded = foregrounded;
            if (!foregrounded)
                activeDrainCancellation = _activeDrainCancellation;
        }

        if (!foregrounded)
        {
            TryCancel(activeDrainCancellation);
            return;
        }

        SignalQueueChanged();
        ScheduleDrainIfReady();
    }

    internal MauiExternalNavigationBridgeDispatchResult TryDispatchFromBridge(
        RouterNavigationRequest request)
    {
        lock (_gate)
        {
            if (_disposed)
                return MauiExternalNavigationBridgeDispatchResult.Unavailable;
        }

        return TryDispatch(request)
            ? MauiExternalNavigationBridgeDispatchResult.Accepted
            : MauiExternalNavigationBridgeDispatchResult.Ignored;
    }

    internal MauiExternalNavigationBridgeDispatchResult RejectIngressFromBridge(
        MauiExternalNavigationRejectionReason reason)
    {
        lock (_gate)
        {
            if (_disposed)
                return MauiExternalNavigationBridgeDispatchResult.Unavailable;
        }

        WriteRejected(reason);
        return MauiExternalNavigationBridgeDispatchResult.Ignored;
    }

    public void Dispose()
    {
        CancellationTokenSource? activeDrainCancellation;
        TaskCompletionSource<bool> pendingRequestAvailable;
        TaskCompletionSource<bool> queueChanged;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _ready = false;
            _foregrounded = false;
            _drainScheduled = false;
            _pending.Clear();
            _deduped.Clear();
            _inFlight = null;
            activeDrainCancellation = _activeDrainCancellation;
            pendingRequestAvailable = _pendingRequestAvailable;
            queueChanged = _queueChanged;
        }

        MauiExternalNavigationBridge.Unregister(this);
        TryCancel(activeDrainCancellation);
        pendingRequestAvailable.TrySetResult(false);
        queueChanged.TrySetResult(false);
    }

    private bool TryEnqueueAccepted(RouterNavigationRequest request, DateTimeOffset now)
    {
        PruneExpiredPending(now);
        List<PendingRequest>? overflowed = null;
        int pendingCount;
        bool expired;
        bool duplicated = false;
        lock (_gate)
        {
            if (_disposed)
                return false;

            expired = IsExpired(request, now);
            if (expired)
            {
                pendingCount = _pending.Count;
            }
            else
            {
                if (!_deduped.Add(request))
                {
                    pendingCount = _pending.Count;
                    duplicated = true;
                }
                else
                {
                    while (_pending.Count >= _options.MaximumPendingRequests)
                    {
                        PendingRequest dropped = _pending.First!.Value;
                        _pending.RemoveFirst();
                        _deduped.Remove(dropped.Request);
                        (overflowed ??= []).Add(dropped);
                    }

                    _pending.AddLast(new PendingRequest(request, 0, now));
                    _pendingRequestAvailable.TrySetResult(true);
                    _queueChanged.TrySetResult(true);

                    pendingCount = _pending.Count;
                }
            }
        }

        if (duplicated)
        {
            WriteDeduplicated(request, pendingCount);
            return false;
        }

        if (expired)
        {
            WriteExpired(request, pendingCount);
            return false;
        }

        if (overflowed is not null)
        {
            foreach (PendingRequest dropped in overflowed)
                WriteOverflowed(dropped.Request, pendingCount);
        }

        WriteAccepted(request, pendingCount);
        ScheduleDrainIfReady();
        return true;
    }

    private void ScheduleDrainIfReady()
    {
        CancellationTokenSource drainCancellation;
        lock (_gate)
        {
            if (_disposed || _drainScheduled || !_ready || !_foregrounded || _pending.Count == 0)
                return;

            _drainScheduled = true;
            drainCancellation = new CancellationTokenSource();
            _activeDrainCancellation = drainCancellation;
        }

        _ = DrainObservedAsync(drainCancellation);
    }

    private async Task DrainObservedAsync(CancellationTokenSource drainCancellation)
    {
        try
        {
            while (true)
            {
                DrainSelection selection = SelectNext(_timeProvider.GetUtcNow());
                switch (selection.Kind)
                {
                    case DrainSelectionKind.Stop:
                        return;
                    case DrainSelectionKind.Expired:
                        WriteExpired(selection.Request!.Request, selection.PendingCount);
                        continue;
                    case DrainSelectionKind.Wait:
                        await WaitForQueueOrRetryAsync(
                                selection.QueueChanged!,
                                selection.Delay,
                                drainCancellation.Token)
                            .ConfigureAwait(false);
                        continue;
                    case DrainSelectionKind.Dispatch:
                        bool lifecycleCancelled = await DispatchOneAsync(
                                selection.Request!,
                                drainCancellation.Token)
                            .ConfigureAwait(false);
                        if (lifecycleCancelled)
                            return;
                        break;
                    default:
                        throw new InvalidOperationException("Unknown external navigation drain selection.");
                }
            }
        }
        catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested)
        {
            // Lifecycle cancellation is expected; an in-flight request is restored by DispatchOneAsync.
        }
        catch (Exception ex)
        {
            _diagnostics.Write(
                NavigationDiagnosticEventKind.AppLinkFailed,
                OperationId(),
                "The external navigation queue stopped unexpectedly.",
                new Dictionary<string, object?>
                {
                    [NavigationDiagnosticDataKeys.ExceptionType] = ex.GetType().FullName
                });
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeDrainCancellation, drainCancellation))
                {
                    _activeDrainCancellation = null;
                    _drainScheduled = false;
                }
            }

            drainCancellation.Dispose();
            ScheduleDrainIfReady();
        }
    }

    private DrainSelection SelectNext(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_disposed || !_ready || !_foregrounded || _pending.Count == 0)
            {
                _drainScheduled = false;
                return DrainSelection.Stop;
            }

            DateTimeOffset? earliestAttempt = null;
            LinkedListNode<PendingRequest>? node = _pending.First;
            while (node is not null)
            {
                LinkedListNode<PendingRequest>? next = node.Next;
                PendingRequest item = node.Value;
                if (IsExpired(item.Request, now))
                {
                    _pending.Remove(node);
                    _deduped.Remove(item.Request);
                    return DrainSelection.Expired(item, _pending.Count);
                }

                if (item.NextAttemptAt <= now)
                {
                    _pending.Remove(node);
                    _inFlight = item;
                    return DrainSelection.Dispatch(item, _pending.Count);
                }

                if (earliestAttempt is null || item.NextAttemptAt < earliestAttempt.Value)
                    earliestAttempt = item.NextAttemptAt;
                node = next;
            }

            if (_queueChanged.Task.IsCompleted)
                _queueChanged = NewSignal();

            TimeSpan delay = earliestAttempt!.Value - now;
            return DrainSelection.Wait(_queueChanged.Task, delay);
        }
    }

    private async Task<bool> DispatchOneAsync(PendingRequest item, CancellationToken cancellationToken)
    {
        int attempt = item.FailedAttempts + 1;
        WriteDispatched(item.Request, attempt);

        try
        {
            var navigator = _services.GetRequiredService<IRouterNavigator>();
            await navigator.NavigateAsync(item.Request, cancellationToken).ConfigureAwait(false);
            CompleteSuccessfulDispatch(item);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PreserveLifecycleCancelledRequest(item);
            return true;
        }
        catch (Exception ex)
        {
            WriteFailed(item.Request, attempt, ex.GetType());
            CompleteFailedDispatch(item, ex, attempt);
            return false;
        }
    }

    private void CompleteSuccessfulDispatch(PendingRequest item)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_inFlight, item))
                _inFlight = null;
            _deduped.Remove(item.Request);
        }
    }

    private void PreserveLifecycleCancelledRequest(PendingRequest item)
    {
        PendingRequest? overflowed = null;
        int pendingCount;
        lock (_gate)
        {
            if (ReferenceEquals(_inFlight, item))
                _inFlight = null;
            if (!_disposed && _deduped.Contains(item.Request))
            {
                if (_pending.Count >= _options.MaximumPendingRequests)
                {
                    overflowed = _pending.First!.Value;
                    _pending.RemoveFirst();
                    _deduped.Remove(overflowed.Request);
                }

                _pending.AddFirst(item);
            }
            _drainScheduled = false;
            pendingCount = _pending.Count;
        }

        if (overflowed is not null)
            WriteOverflowed(overflowed.Request, pendingCount);
    }

    private void CompleteFailedDispatch(PendingRequest item, Exception exception, int attempt)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        MauiExternalNavigationFailureDisposition disposition = _options.Classify(exception);
        string reason;
        bool retry;
        int pendingCount;

        lock (_gate)
        {
            if (ReferenceEquals(_inFlight, item))
                _inFlight = null;

            bool expired = IsExpired(item.Request, now);
            retry = !_disposed &&
                    !expired &&
                    disposition == MauiExternalNavigationFailureDisposition.Retry &&
                    attempt < _options.MaximumDispatchAttempts;
            if (retry && _pending.Count < _options.MaximumPendingRequests)
            {
                _pending.AddLast(item with
                {
                    FailedAttempts = attempt,
                    NextAttemptAt = NextRetryAt(item.Request, now)
                });
                _queueChanged.TrySetResult(true);
                reason = "RetryableFailure";
            }
            else
            {
                if (retry)
                {
                    // The failed request predates every request that arrived while it was in
                    // flight. Prefer those newer intents when the bounded pending queue is full.
                    retry = false;
                    reason = "PendingLimit";
                }
                else
                {
                    reason = expired
                        ? "Expired"
                        : disposition == MauiExternalNavigationFailureDisposition.Drop
                            ? "ClassifiedTerminal"
                            : "AttemptLimit";
                }

                _deduped.Remove(item.Request);
            }

            pendingCount = _pending.Count;
        }

        if (retry)
            WriteRetrying(item.Request, attempt, pendingCount);
        else if (reason == "Expired")
            WriteExpired(item.Request, pendingCount);
        else
        {
            if (reason == "PendingLimit")
                WriteOverflowed(item.Request, pendingCount);
            WriteTerminalDrop(item.Request, attempt, reason, pendingCount);
        }
    }

    private async Task WaitForQueueOrRetryAsync(
        Task queueChanged,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            return;

        TimeSpan boundedDelay = delay > MaximumTimerDelay ? MaximumTimerDelay : delay;
        Task retryDelay = Task.Delay(boundedDelay, _timeProvider, cancellationToken);
        Task completed = await Task.WhenAny(queueChanged, retryDelay).ConfigureAwait(false);
        if (ReferenceEquals(completed, retryDelay))
            await retryDelay.ConfigureAwait(false);
    }

    private bool IsExpired(RouterNavigationRequest request, DateTimeOffset now)
    {
        return request.Timestamp <= now && now - request.Timestamp >= _options.MaximumRequestAge;
    }

    private void PruneExpiredPending(DateTimeOffset now)
    {
        List<PendingRequest>? expired = null;
        int pendingCount;
        lock (_gate)
        {
            LinkedListNode<PendingRequest>? node = _pending.First;
            while (node is not null)
            {
                LinkedListNode<PendingRequest>? next = node.Next;
                if (IsExpired(node.Value.Request, now))
                {
                    PendingRequest item = node.Value;
                    _pending.Remove(node);
                    _deduped.Remove(item.Request);
                    (expired ??= []).Add(item);
                }

                node = next;
            }

            pendingCount = _pending.Count;
        }

        if (expired is null)
            return;

        foreach (PendingRequest item in expired)
            WriteExpired(item.Request, pendingCount);
    }

    private DateTimeOffset NextRetryAt(RouterNavigationRequest request, DateTimeOffset now)
    {
        DateTimeOffset requestedRetry = AddSaturated(now, _options.RetryDelay);
        DateTimeOffset expiresAt = AddSaturated(request.Timestamp, _options.MaximumRequestAge);
        return requestedRetry <= expiresAt ? requestedRetry : expiresAt;
    }

    private static DateTimeOffset AddSaturated(DateTimeOffset value, TimeSpan delay)
    {
        long availableTicks = (DateTimeOffset.MaxValue - value).Ticks;
        return delay.Ticks >= availableTicks
            ? DateTimeOffset.MaxValue
            : value + delay;
    }

    private void SignalQueueChanged()
    {
        lock (_gate)
            _queueChanged.TrySetResult(true);
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A completed drain may dispose its cancellation source concurrently.
        }
    }

    private void WriteAccepted(RouterNavigationRequest request, int pendingCount)
    {
        string operationId = OperationId();
        IReadOnlyDictionary<string, object?> data = RequestData(request, pendingCount);
        _diagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkReceived,
            operationId,
            "An external navigation request was accepted.",
            data);
        _diagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkBuffered,
            operationId,
            "An external navigation request was buffered.",
            data);
    }

    private void WriteBootstrapDiagnostic(MauiExternalNavigationBootstrapDiagnostic diagnostic)
    {
        _diagnostics.Write(
            diagnostic.Kind,
            OperationId(),
            "A structural external-navigation event occurred before the dispatcher was available.",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.ExternalNavigationReason] = diagnostic.Reason,
                [NavigationDiagnosticDataKeys.PendingRequestCount] = diagnostic.PendingCount
            });
    }

    private void WriteRejected(
        RouterNavigationRequest request,
        MauiExternalNavigationRejectionReason reason,
        Type? exceptionType = null)
    {
        var data = RequestData(request, PendingCount());
        data[NavigationDiagnosticDataKeys.ExternalNavigationReason] = reason.ToString();
        if (exceptionType is not null)
            data[NavigationDiagnosticDataKeys.ExceptionType] = exceptionType.FullName;

        _diagnostics.Write(
            reason == MauiExternalNavigationRejectionReason.Expired
                ? NavigationDiagnosticEventKind.ExternalNavigationExpired
                : NavigationDiagnosticEventKind.ExternalNavigationRejected,
            OperationId(),
            "An external navigation request was rejected.",
            data);
    }

    private void WriteRejected(MauiExternalNavigationRejectionReason reason)
    {
        _diagnostics.Write(
            NavigationDiagnosticEventKind.ExternalNavigationRejected,
            OperationId(),
            "An external navigation transport value was rejected before request construction.",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.ExternalNavigationReason] = reason.ToString(),
                [NavigationDiagnosticDataKeys.PendingRequestCount] = PendingCount()
            });
    }

    private void WriteDeduplicated(RouterNavigationRequest request, int pendingCount)
    {
        var data = RequestData(request, pendingCount);
        data[NavigationDiagnosticDataKeys.ExternalNavigationReason] = "EquivalentRequestPending";
        _diagnostics.Write(
            NavigationDiagnosticEventKind.ExternalNavigationDeduplicated,
            OperationId(),
            "An equivalent external navigation request was already pending.",
            data);
    }

    private void WriteOverflowed(RouterNavigationRequest request, int pendingCount)
    {
        var data = RequestData(request, pendingCount);
        data[NavigationDiagnosticDataKeys.ExternalNavigationReason] = "PendingLimit";
        _diagnostics.Write(
            NavigationDiagnosticEventKind.ExternalNavigationOverflowed,
            OperationId(),
            "The oldest external navigation request was dropped from a full queue.",
            data);
    }

    private void WriteExpired(RouterNavigationRequest request, int pendingCount)
    {
        var data = RequestData(request, pendingCount);
        data[NavigationDiagnosticDataKeys.ExternalNavigationReason] = "MaximumRequestAge";
        _diagnostics.Write(
            NavigationDiagnosticEventKind.ExternalNavigationExpired,
            OperationId(),
            "An external navigation request expired.",
            data);
    }

    private void WriteDispatched(RouterNavigationRequest request, int attempt)
    {
        var data = RequestData(request, PendingCount());
        data[NavigationDiagnosticDataKeys.DispatchAttempt] = attempt;
        data[NavigationDiagnosticDataKeys.MaximumDispatchAttempts] = _options.MaximumDispatchAttempts;
        _diagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkDispatched,
            OperationId(),
            "An external navigation request was dispatched.",
            data);
    }

    private void WriteFailed(RouterNavigationRequest request, int attempt, Type exceptionType)
    {
        var data = RequestData(request, PendingCount());
        data[NavigationDiagnosticDataKeys.DispatchAttempt] = attempt;
        data[NavigationDiagnosticDataKeys.ExceptionType] = exceptionType.FullName;
        _diagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkFailed,
            OperationId(),
            "An external navigation dispatch attempt failed.",
            data);
    }

    private void WriteRetrying(RouterNavigationRequest request, int attempt, int pendingCount)
    {
        var data = RequestData(request, pendingCount);
        data[NavigationDiagnosticDataKeys.ExternalNavigationReason] = "RetryableFailure";
        data[NavigationDiagnosticDataKeys.DispatchAttempt] = attempt;
        data[NavigationDiagnosticDataKeys.MaximumDispatchAttempts] = _options.MaximumDispatchAttempts;
        data[NavigationDiagnosticDataKeys.RetryDelayMs] = _options.RetryDelay.TotalMilliseconds;
        _diagnostics.Write(
            NavigationDiagnosticEventKind.ExternalNavigationRetrying,
            OperationId(),
            "An external navigation request was scheduled for retry.",
            data);
    }

    private void WriteTerminalDrop(
        RouterNavigationRequest request,
        int attempt,
        string reason,
        int pendingCount)
    {
        var data = RequestData(request, pendingCount);
        data[NavigationDiagnosticDataKeys.ExternalNavigationReason] = reason;
        data[NavigationDiagnosticDataKeys.DispatchAttempt] = attempt;
        data[NavigationDiagnosticDataKeys.MaximumDispatchAttempts] = _options.MaximumDispatchAttempts;
        _diagnostics.Write(
            NavigationDiagnosticEventKind.ExternalNavigationTerminalDrop,
            OperationId(),
            "An external navigation request was dropped.",
            data);
    }

    private int PendingCount()
    {
        lock (_gate)
            return _pending.Count;
    }

    private static Dictionary<string, object?> RequestData(
        RouterNavigationRequest request,
        int pendingCount)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [NavigationDiagnosticDataKeys.RequestSource] = request.Source.ToString(),
            [NavigationDiagnosticDataKeys.RequestDisposition] = request.Disposition.ToString(),
            [NavigationDiagnosticDataKeys.PendingRequestCount] = pendingCount
        };

        return data;
    }

    private static TaskCompletionSource<bool> NewSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static string OperationId() => Guid.NewGuid().ToString("N");

    private sealed record PendingRequest(
        RouterNavigationRequest Request,
        int FailedAttempts,
        DateTimeOffset NextAttemptAt);

    private enum DrainSelectionKind
    {
        Stop,
        Expired,
        Wait,
        Dispatch
    }

    private sealed record DrainSelection(
        DrainSelectionKind Kind,
        PendingRequest? Request = null,
        Task? QueueChanged = null,
        TimeSpan Delay = default,
        int PendingCount = 0)
    {
        public static DrainSelection Stop { get; } = new(DrainSelectionKind.Stop);

        public static DrainSelection Expired(PendingRequest request, int pendingCount) =>
            new(DrainSelectionKind.Expired, Request: request, PendingCount: pendingCount);

        public static DrainSelection Wait(Task queueChanged, TimeSpan delay) =>
            new(DrainSelectionKind.Wait, QueueChanged: queueChanged, Delay: delay);

        public static DrainSelection Dispatch(PendingRequest request, int pendingCount) =>
            new(DrainSelectionKind.Dispatch, Request: request, PendingCount: pendingCount);
    }
}

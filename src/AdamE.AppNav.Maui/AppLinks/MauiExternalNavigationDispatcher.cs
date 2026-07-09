using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Requests;
using Microsoft.Extensions.DependencyInjection;

namespace AdamE.AppNav.Maui.AppLinks;

internal sealed class MauiExternalNavigationDispatcher : IMauiExternalNavigationDispatcher, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly NavigationDiagnostics _diagnostics;
    private readonly Lock _gate = new();
    private readonly Queue<RouterNavigationRequest> _pending = new();
    private readonly HashSet<RouterNavigationRequest> _deduped = new(MauiNavigationRequestEquivalenceComparer.Instance);
    private TaskCompletionSource<bool> _pendingRequestAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _activeDrainCancellation;
    private bool _ready;
    private bool _foregrounded;
    private bool _drainScheduled;
    private bool _disposed;

    public MauiExternalNavigationDispatcher(IServiceProvider services, NavigationDiagnostics diagnostics)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _diagnostics = diagnostics ?? NavigationDiagnostics.None;

        foreach (RouterNavigationRequest request in MauiExternalNavigationBridge.Register(this))
            TryEnqueue(request);
    }

    public bool HasPendingRequests
    {
        get
        {
            lock (_gate)
            {
                return !_disposed && _pending.Count > 0;
            }
        }
    }

    public void Dispatch(RouterNavigationRequest? request)
    {
        if (request is null)
            return;

        if (!TryEnqueue(request))
            throw new ObjectDisposedException(nameof(MauiExternalNavigationDispatcher));
    }

    public async ValueTask<bool> WaitForPendingRequestAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout cannot be negative.");
        }

        Task waitTask;
        lock (_gate)
        {
            if (_disposed)
                return false;

            if (_pending.Count > 0)
            {
                return true;
            }

            if (timeout == TimeSpan.Zero)
            {
                return false;
            }

            if (_pendingRequestAvailable.Task.IsCompleted)
            {
                _pendingRequestAvailable = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

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

        ScheduleDrainIfReady();
    }

    public void SetForegrounded(bool foregrounded)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _foregrounded = foregrounded;
        }

        if (foregrounded)
        {
            ScheduleDrainIfReady();
        }
    }

    internal bool TryDispatchFromBridge(RouterNavigationRequest request)
    {
        return TryEnqueue(request);
    }

    public void Dispose()
    {
        CancellationTokenSource? activeDrainCancellation;
        TaskCompletionSource<bool> pendingRequestAvailable;
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
            activeDrainCancellation = _activeDrainCancellation;
            pendingRequestAvailable = _pendingRequestAvailable;
        }

        MauiExternalNavigationBridge.Unregister(this);
        try
        {
            activeDrainCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The drain completed after disposal captured its cancellation source.
        }

        pendingRequestAvailable.TrySetResult(false);
    }

    private bool TryEnqueue(RouterNavigationRequest request)
    {
        var queued = false;
        lock (_gate)
        {
            if (_disposed)
                return false;

            if (_deduped.Add(request))
            {
                _pending.Enqueue(request);
                _pendingRequestAvailable.TrySetResult(true);
                queued = true;
            }
        }

        var operationId = OperationId();
        var detail = request.Uri?.ToString() ?? request.Source.ToString();
        _diagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkReceived,
            operationId,
            detail,
            RequestData(request));

        if (queued)
        {
            _diagnostics.Write(
                NavigationDiagnosticEventKind.AppLinkBuffered,
                operationId,
                detail,
                RequestData(request));
        }

        ScheduleDrainIfReady();
        return true;
    }

    private void ScheduleDrainIfReady()
    {
        CancellationTokenSource drainCancellation;
        lock (_gate)
        {
            if (_disposed || _drainScheduled || !_ready || !_foregrounded || _pending.Count == 0)
            {
                return;
            }

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
                RouterNavigationRequest? request;
                lock (_gate)
                {
                    if (_disposed || !_ready || !_foregrounded || _pending.Count == 0)
                    {
                        _drainScheduled = false;
                        return;
                    }

                    request = _pending.Peek();
                }

                var operationId = OperationId();
                var detail = request.Uri?.ToString() ?? request.Source.ToString();
                _diagnostics.Write(
                    NavigationDiagnosticEventKind.AppLinkDispatched,
                    operationId,
                    detail,
                    RequestData(request));

                var shouldStop = false;
                var dispatchSucceeded = false;
                try
                {
                    var navigator = _services.GetRequiredService<IRouterNavigator>();
                    await navigator
                        .NavigateAsync(request, drainCancellation.Token)
                        .ConfigureAwait(false);
                    dispatchSucceeded = true;
                }
                catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested)
                {
                    shouldStop = true;
                }
                catch (Exception ex)
                {
                    _diagnostics.Write(
                        NavigationDiagnosticEventKind.AppLinkFailed,
                        operationId,
                        detail,
                        FailureData(request, ex));
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_disposed || drainCancellation.IsCancellationRequested)
                        {
                            _drainScheduled = false;
                            shouldStop = true;
                        }
                        else if (dispatchSucceeded)
                        {
                            if (_pending.Count > 0)
                                _pending.Dequeue();

                            _deduped.Remove(request);
                            if (!_ready || !_foregrounded || _pending.Count == 0)
                            {
                                _drainScheduled = false;
                                shouldStop = true;
                            }
                        }
                        else
                        {
                            _drainScheduled = false;
                            shouldStop = true;
                        }
                    }
                }

                if (shouldStop)
                    return;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeDrainCancellation, drainCancellation))
                    _activeDrainCancellation = null;
            }

            drainCancellation.Dispose();
        }
    }

    private static string OperationId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static Dictionary<string, object?> RequestData(RouterNavigationRequest request)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [NavigationDiagnosticDataKeys.RequestSource] = request.Source.ToString(),
            [NavigationDiagnosticDataKeys.RequestDisposition] = request.Disposition.ToString()
        };

        if (request.Uri is not null)
        {
            data[NavigationDiagnosticDataKeys.Uri] = request.Uri.ToString();
        }

        AddProvenanceData(data, request.Provenance);
        return data;
    }

    private static Dictionary<string, object?> FailureData(
        RouterNavigationRequest request,
        Exception exception)
    {
        var data = RequestData(request);
        data[NavigationDiagnosticDataKeys.ExceptionType] = exception.GetType().FullName;
        data[NavigationDiagnosticDataKeys.ExceptionMessage] = exception.Message;
        return data;
    }

    private static void AddProvenanceData(
        IDictionary<string, object?> data,
        NavigationRequestProvenance? provenance)
    {
        if (provenance is null)
        {
            return;
        }

        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceProvider, provenance.Provider);
        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceOriginalUri, provenance.OriginalUri?.ToString());
        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceReferrerUri, provenance.ReferrerUri?.ToString());
        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceCorrelationId, provenance.CorrelationId);
        if (provenance.IsColdStart.HasValue)
        {
            data[NavigationDiagnosticDataKeys.ProvenanceIsColdStart] = provenance.IsColdStart.Value;
        }

        if (provenance.Attributes.Count > 0)
        {
            data[NavigationDiagnosticDataKeys.ProvenanceAttributes] =
                new Dictionary<string, string?>(provenance.Attributes, StringComparer.Ordinal);
        }
    }

    private static void AddIfPresent(
        IDictionary<string, object?> data,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            data[key] = value;
        }
    }

}

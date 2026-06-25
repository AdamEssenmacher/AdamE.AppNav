using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Requests;
using Microsoft.Extensions.DependencyInjection;

namespace AdamE.MauiRouter.Maui.AppLinks;

internal sealed class MauiExternalNavigationDispatcher : IMauiExternalNavigationDispatcher
{
    private static readonly Lock BootstrapGate = new();
    private static readonly Queue<RouterNavigationRequest> BootstrapPending = new();
    private static readonly HashSet<RouterNavigationRequest> BootstrapDeduped = new(MauiNavigationRequestEquivalenceComparer.Instance);
    private static MauiExternalNavigationDispatcher? _current;

    private readonly IServiceProvider _services;
    private readonly NavigationDiagnostics _diagnostics;
    private readonly Lock _gate = new();
    private readonly Queue<RouterNavigationRequest> _pending = new();
    private readonly HashSet<RouterNavigationRequest> _deduped = new(MauiNavigationRequestEquivalenceComparer.Instance);
    private TaskCompletionSource<bool> _pendingRequestAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _ready;
    private bool _foregrounded;
    private bool _drainScheduled;

    public MauiExternalNavigationDispatcher(IServiceProvider services, NavigationDiagnostics diagnostics)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _diagnostics = diagnostics ?? NavigationDiagnostics.None;

        RouterNavigationRequest[] bootstrapRequests;
        lock (BootstrapGate)
        {
            _current = this;
            bootstrapRequests = BootstrapPending.ToArray();
            BootstrapPending.Clear();
            BootstrapDeduped.Clear();
        }

        foreach (var request in bootstrapRequests)
        {
            Enqueue(request);
        }
    }

    public bool HasPendingRequests
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0;
            }
        }
    }

    public static void Submit(RouterNavigationRequest? request)
    {
        if (request is null)
        {
            return;
        }

        MauiExternalNavigationDispatcher? current;
        lock (BootstrapGate)
        {
            current = _current;
            if (current is null)
            {
                if (!BootstrapDeduped.Add(request))
                {
                    return;
                }

                BootstrapPending.Enqueue(request);
                return;
            }
        }

        current.Dispatch(request);
    }

    public void Dispatch(RouterNavigationRequest? request)
    {
        if (request is null)
        {
            return;
        }

        Enqueue(request);
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
            _ready = true;
        }

        ScheduleDrainIfReady();
    }

    public void SetForegrounded(bool foregrounded)
    {
        lock (_gate)
        {
            _foregrounded = foregrounded;
        }

        if (foregrounded)
        {
            ScheduleDrainIfReady();
        }
    }

    private void Enqueue(RouterNavigationRequest request)
    {
        var operationId = OperationId();
        var detail = request.Uri?.ToString() ?? request.Source.ToString();
        _diagnostics.Write(
            NavigationDiagnosticEventKind.AppLinkReceived,
            operationId,
            detail,
            RequestData(request));

        var queued = false;
        lock (_gate)
        {
            if (!_deduped.Add(request))
            {
                return;
            }

            _pending.Enqueue(request);
            _pendingRequestAvailable.TrySetResult(true);
            queued = true;
        }

        if (queued)
        {
            _diagnostics.Write(
                NavigationDiagnosticEventKind.AppLinkBuffered,
                operationId,
                detail,
                RequestData(request));
        }

        ScheduleDrainIfReady();
    }

    private void ScheduleDrainIfReady()
    {
        lock (_gate)
        {
            if (_drainScheduled || !_ready || !_foregrounded || _pending.Count == 0)
            {
                return;
            }

            _drainScheduled = true;
        }

        _ = DrainObservedAsync();
    }

    private async Task DrainObservedAsync()
    {
        while (true)
        {
            RouterNavigationRequest? request;
            lock (_gate)
            {
                if (!_ready || !_foregrounded || _pending.Count == 0)
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
                await navigator.NavigateAsync(request).ConfigureAwait(false);
                dispatchSucceeded = true;
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
                    if (dispatchSucceeded)
                    {
                        if (_pending.Count > 0)
                        {
                            _pending.Dequeue();
                        }

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
            {
                return;
            }
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

    private sealed class MauiNavigationRequestEquivalenceComparer : IEqualityComparer<RouterNavigationRequest>
    {
        public static MauiNavigationRequestEquivalenceComparer Instance { get; } = new();

        public bool Equals(RouterNavigationRequest? x, RouterNavigationRequest? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            if (!Equals(x.Uri, y.Uri) ||
                !Equals(x.Route, y.Route) ||
                x.Source != y.Source ||
                !StringComparer.Ordinal.Equals(x.WindowId, y.WindowId) ||
                x.Disposition != y.Disposition ||
                !Equals(x.Provenance, y.Provenance) ||
                x.Metadata.Count != y.Metadata.Count)
            {
                return false;
            }

            foreach (var pair in x.Metadata)
            {
                if (!y.Metadata.TryGetValue(pair.Key, out var otherValue) ||
                    !Equals(pair.Value, otherValue))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(RouterNavigationRequest obj)
        {
            ArgumentNullException.ThrowIfNull(obj);

            var hash = new HashCode();
            hash.Add(obj.Uri);
            hash.Add(obj.Route);
            hash.Add(obj.Source);
            hash.Add(obj.WindowId, StringComparer.Ordinal);
            hash.Add(obj.Disposition);
            hash.Add(obj.Provenance);
            foreach (var pair in obj.Metadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                hash.Add(pair.Key, StringComparer.Ordinal);
                hash.Add(pair.Value?.GetType());
                hash.Add(pair.Value);
            }

            return hash.ToHashCode();
        }
    }
}

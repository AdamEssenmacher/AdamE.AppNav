using System.Text.Json;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Maui.Requests;

internal sealed class MauiFileDeferredNavigationRequestStore : IDeferredNavigationRequestStore
{
    public const string DefaultFileName = "appnav-deferred-requests.json";

    private readonly string _path;
    private readonly DeferredNavigationRequestSerializer _serializer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    private readonly List<StoredRequest> _requests = [];
    private readonly HashSet<RouterNavigationRequest> _deduped = new(MauiNavigationRequestEquivalenceComparer.Instance);
    private bool _loaded;

    public MauiFileDeferredNavigationRequestStore(
        RouteTable routes,
        MauiFileDeferredNavigationRequestStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        _path = options.Path;
        _serializer = new DeferredNavigationRequestSerializer(routes, new DeferredNavigationRequestPersistenceOptions
        {
            BaseUri = options.BaseUri,
            MetadataSerializer = options.MetadataSerializer,
            RouteStateRegistry = options.RouteStateRegistry
        });
    }

    public async ValueTask<bool> HasDeferredRequestsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return _requests.Count > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask EnqueueAsync(
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_deduped.Contains(request))
            {
                return;
            }

            var entry = new StoredRequest(Guid.NewGuid(), request);
            RouterNavigationRequest[] projected = _requests
                .Select(static stored => stored.Request)
                .Append(request)
                .ToArray();
            await PersistAsync(projected, cancellationToken).ConfigureAwait(false);
            _requests.Add(entry);
            _deduped.Add(request);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IDeferredNavigationRequestLease> AcquireReplayLeaseAsync(
        CancellationToken cancellationToken = default)
    {
        await _replayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
                return new ReplayLease(this, _requests.ToArray());
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            _replayGate.Release();
            throw;
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await _replayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // ClearAsync is the recovery escape hatch for invalid persisted data and must
                // not depend on successfully deserializing the current on-disk snapshot.
                _requests.Clear();
                _deduped.Clear();
                _loaded = true;
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _replayGate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        if (!File.Exists(_path))
        {
            _loaded = true;
            return;
        }

        await using var stream = File.OpenRead(_path);
        var snapshot = await JsonSerializer.DeserializeAsync(
            stream,
            AppNavJsonSerializerContext.Default.DeferredNavigationRequestStoreSnapshot,
            cancellationToken).ConfigureAwait(false);

        var restoredRequests = snapshot is null
            ? Array.Empty<RouterNavigationRequest>()
            : _serializer.Restore(snapshot);
        var dedupedRequests = new HashSet<RouterNavigationRequest>(MauiNavigationRequestEquivalenceComparer.Instance);
        var pendingRequests = new List<RouterNavigationRequest>(restoredRequests.Count);
        foreach (var request in restoredRequests)
        {
            if (dedupedRequests.Add(request))
            {
                pendingRequests.Add(request);
            }
        }

        _requests.Clear();
        _deduped.Clear();
        foreach (var request in pendingRequests)
        {
            _requests.Add(new StoredRequest(Guid.NewGuid(), request));
            _deduped.Add(request);
        }

        _loaded = true;
    }

    private async Task PersistAsync(
        IReadOnlyList<RouterNavigationRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            return;
        }

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var snapshot = _serializer.CreateSnapshot(requests);
        var tempPath = $"{_path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    snapshot,
                    AppNavJsonSerializerContext.Default.DeferredNavigationRequestStoreSnapshot,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async ValueTask AcknowledgeAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            int index = _requests.FindIndex(entry => entry.Id == id);
            if (index < 0)
                throw new InvalidOperationException("The deferred request is no longer present in the store.");

            RouterNavigationRequest request = _requests[index].Request;
            RouterNavigationRequest[] projected = _requests
                .Where(entry => entry.Id != id)
                .Select(static entry => entry.Request)
                .ToArray();
            await PersistAsync(projected, cancellationToken).ConfigureAwait(false);
            _requests.RemoveAt(index);
            _deduped.Remove(request);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReleaseReplayLease()
    {
        _replayGate.Release();
    }

    private sealed record StoredRequest(Guid Id, RouterNavigationRequest Request);

    private sealed class ReplayLease : IDeferredNavigationRequestLease
    {
        private readonly MauiFileDeferredNavigationRequestStore _owner;
        private readonly StoredRequest[] _entries;
        private readonly SemaphoreSlim _operationGate = new(1, 1);
        private readonly bool[] _acknowledged;
        private int _disposeStarted;

        public ReplayLease(MauiFileDeferredNavigationRequestStore owner, StoredRequest[] entries)
        {
            _owner = owner;
            _entries = entries;
            _acknowledged = new bool[entries.Length];
            Requests = Array.AsReadOnly(entries.Select(static entry => entry.Request).ToArray());
        }

        public IReadOnlyList<RouterNavigationRequest> Requests { get; }

        public async ValueTask AcknowledgeAsync(
            int requestIndex,
            CancellationToken cancellationToken = default)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
                if ((uint)requestIndex >= (uint)_entries.Length)
                    throw new ArgumentOutOfRangeException(nameof(requestIndex));
                if (_acknowledged[requestIndex])
                    throw new InvalidOperationException("The deferred request has already been acknowledged.");

                await _owner.AcknowledgeAsync(_entries[requestIndex].Id, cancellationToken).ConfigureAwait(false);
                _acknowledged[requestIndex] = true;
            }
            finally
            {
                _operationGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                return;

            await _operationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _owner.ReleaseReplayLease();
            }
            finally
            {
                _operationGate.Release();
            }
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

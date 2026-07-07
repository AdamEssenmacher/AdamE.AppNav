using System.Text.Json;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Maui.Requests;

internal sealed class MauiFileDeferredNavigationRequestStore : IDeferredNavigationRequestStore
{
    public const string DefaultFileName = "appnav-deferred-requests.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string _path;
    private readonly DeferredNavigationRequestSerializer _serializer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<RouterNavigationRequest> _requests = new();
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
            if (!_deduped.Add(request))
            {
                return;
            }

            _requests.Enqueue(request);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<RouterNavigationRequest?> TryDequeueAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_requests.Count == 0)
            {
                return null;
            }

            var request = _requests.Dequeue();
            _deduped.Remove(request);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return request;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<RouterNavigationRequest>> DrainAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_requests.Count == 0)
            {
                return [];
            }

            var drained = _requests.ToArray();
            _requests.Clear();
            _deduped.Clear();
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return drained;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
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
        var snapshot = await JsonSerializer.DeserializeAsync<DeferredNavigationRequestStoreSnapshot>(
            stream,
            JsonOptions,
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
            _requests.Enqueue(request);
            _deduped.Add(request);
        }

        _loaded = true;
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        if (_requests.Count == 0)
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

        var snapshot = _serializer.CreateSnapshot(_requests.ToArray());
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
                    JsonOptions,
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

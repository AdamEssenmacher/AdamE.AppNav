using System.Text.Json;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Maui.Requests;

internal sealed class MauiFileDeferredNavigationRequestStore : IDeferredNavigationRequestStore
{
    public const string DefaultFileName = "appnav-deferred-requests.json";

    private readonly string _path;
    private readonly DeferredNavigationRequestSerializer _serializer;
    private readonly NavigationDiagnostics _diagnostics;
    private readonly IMauiDeferredNavigationFileOperations _fileOperations;
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumPendingRequests;
    private readonly long _maximumFileSize;
    private readonly TimeSpan _maximumRequestAge;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    private readonly List<StoredRequest> _requests = [];
    private readonly HashSet<RouterNavigationRequest> _deduped = new(MauiNavigationRequestEquivalenceComparer.Instance);
    private readonly HashSet<Guid> _activeReplayRequestIds = [];
    private bool _loaded;

    public MauiFileDeferredNavigationRequestStore(
        RouteTable routes,
        MauiFileDeferredNavigationRequestStoreOptions options,
        NavigationDiagnostics? diagnostics = null,
        IMauiDeferredNavigationFileOperations? fileOperations = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumPendingRequests, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumFileSize, 1);
        if (options.MaximumRequestAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(options.MaximumRequestAge),
                options.MaximumRequestAge,
                "The maximum deferred request age must be greater than zero.");

        _path = options.Path;
        _diagnostics = diagnostics ?? NavigationDiagnostics.None;
        _fileOperations = fileOperations ?? MauiDeferredNavigationFileOperations.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maximumPendingRequests = options.MaximumPendingRequests;
        _maximumFileSize = options.MaximumFileSize;
        _maximumRequestAge = options.MaximumRequestAge;
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
            await PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
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
            await PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
            RouterNavigationRequest canonicalRequest = Canonicalize(request);
            if (_deduped.Contains(canonicalRequest))
            {
                int existingIndex = _requests.FindIndex(entry =>
                    MauiNavigationRequestEquivalenceComparer.Instance.Equals(entry.Request, canonicalRequest));
                System.Diagnostics.Debug.Assert(existingIndex >= 0);
                StoredRequest existing = _requests[existingIndex];
                if (_activeReplayRequestIds.Contains(existing.Id))
                {
                    StoredRequest[] renewed = _requests.ToArray();
                    renewed[existingIndex] = new StoredRequest(Guid.NewGuid(), canonicalRequest);
                    PersistResult renewal = await PersistAsync(renewed, cancellationToken).ConfigureAwait(false);
                    ReplaceInMemory(renewal.Requests);
                }

                return;
            }

            var entry = new StoredRequest(Guid.NewGuid(), canonicalRequest);
            StoredRequest[] projected = _requests
                .Append(entry)
                .ToArray();
            PersistResult persisted = await PersistAsync(projected, cancellationToken).ConfigureAwait(false);
            ReplaceInMemory(persisted.Requests);
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
                await PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
                StoredRequest[] snapshot = _requests.ToArray();
                var lease = new ReplayLease(this, snapshot);
                _activeReplayRequestIds.UnionWith(snapshot.Select(static entry => entry.Id));
                return lease;
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

        if (new FileInfo(_path).Length > _maximumFileSize)
        {
            QuarantineInvalidData(
                new InvalidDataException("Deferred navigation request data exceeds the configured file-size limit."),
                "file-too-large");
            ResetInMemory();
            _loaded = true;
            return;
        }

        IReadOnlyList<RouterNavigationRequest> restoredRequests;
        try
        {
            DeferredNavigationRequestStoreSnapshot snapshot = await ReadSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            restoredRequests = _serializer.Restore(snapshot);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDeferredNavigationRequestDataException)
        {
            QuarantineInvalidData(ex, "invalid-data");
            ResetInMemory();
            _loaded = true;
            return;
        }

        var dedupedRequests = new HashSet<RouterNavigationRequest>(MauiNavigationRequestEquivalenceComparer.Instance);
        var pendingRequests = new List<StoredRequest>(restoredRequests.Count);
        foreach (var request in restoredRequests)
        {
            if (dedupedRequests.Add(request))
            {
                pendingRequests.Add(new StoredRequest(Guid.NewGuid(), request));
            }
        }

        // Rewrite every successfully restored schema-3 document through the current serializer.
        // This removes unknown legacy fields (including raw transport/provenance values) instead
        // of leaving them on disk merely because the known request data was otherwise valid.
        PersistResult persisted = await PersistAsync(pendingRequests, cancellationToken).ConfigureAwait(false);
        ReplaceInMemory(persisted.Requests);

        _loaded = true;
    }

    private async Task<DeferredNavigationRequestStoreSnapshot> ReadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        JsonElement root;
        await using (var stream = File.OpenRead(_path))
        using (JsonDocument document = await JsonDocument.ParseAsync(
                   stream,
                   cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            root = document.RootElement.Clone();
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out JsonElement schemaElement) ||
            !schemaElement.TryGetInt32(out int schemaVersion))
        {
            throw new JsonException("Deferred navigation request data has no valid schema version.");
        }

        if (schemaVersion == 2)
        {
            File.Delete(_path);
            WriteMaintenanceDiagnostic(
                NavigationDiagnosticEventKind.DeferredRequestStoreReset,
                "Legacy deferred navigation request data was reset for the schema-3 preview.",
                count: null,
                reason: "schema-2-preview-reset",
                schemaVersion);
            return new DeferredNavigationRequestStoreSnapshot
            {
                SchemaVersion = DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion,
                Requests = []
            };
        }

        if (schemaVersion > DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion)
        {
            QuarantineFutureData(schemaVersion);
            return new DeferredNavigationRequestStoreSnapshot
            {
                SchemaVersion = DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion,
                Requests = []
            };
        }

        if (schemaVersion != DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion)
            throw new UnsupportedDeferredNavigationRequestSchemaException(
                schemaVersion,
                DeferredNavigationRequestStoreSnapshot.CurrentSchemaVersion);

        DeferredNavigationRequestStoreSnapshot? snapshot = root.Deserialize(
            AppNavJsonSerializerContext.Default.DeferredNavigationRequestStoreSnapshot);
        if (snapshot is null)
            throw new JsonException("Deferred navigation request data contains a null document.");
        if (snapshot.Requests is null)
            throw new JsonException("Deferred navigation request data contains a null request collection.");

        return snapshot;
    }

    private void QuarantineInvalidData(Exception exception, string reason)
    {
        string quarantinePath = $"{_path}.invalid-{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmssfffffff'Z'}-{Guid.NewGuid():N}";
        _fileOperations.Move(_path, quarantinePath);
        _diagnostics.Write(
            NavigationDiagnosticEventKind.DeferredRequestStoreQuarantined,
            Guid.NewGuid().ToString("N"),
            "Invalid deferred navigation request data was quarantined.",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.Path] = System.IO.Path.GetFileName(quarantinePath),
                [NavigationDiagnosticDataKeys.ExceptionType] = exception.GetType().FullName,
                [NavigationDiagnosticDataKeys.Reason] = reason
            },
            phase: NavigationDiagnosticPhase.Persistence);
    }

    private void QuarantineFutureData(int schemaVersion)
    {
        string quarantinePath =
            $"{_path}.future-{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmssfffffff'Z'}-{Guid.NewGuid():N}";
        _fileOperations.Move(_path, quarantinePath);
        _diagnostics.Write(
            NavigationDiagnosticEventKind.DeferredRequestStoreQuarantined,
            Guid.NewGuid().ToString("N"),
            "Deferred navigation request data from a future schema was quarantined without modification.",
            new Dictionary<string, object?>
            {
                [NavigationDiagnosticDataKeys.Path] = System.IO.Path.GetFileName(quarantinePath),
                [NavigationDiagnosticDataKeys.SchemaVersion] = schemaVersion,
                [NavigationDiagnosticDataKeys.Reason] = "future-schema"
            },
            phase: NavigationDiagnosticPhase.Persistence);
    }

    private async Task<PersistResult> PersistAsync(
        IReadOnlyList<StoredRequest> requests,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PersistSelection selection = SelectForPersistence(requests, _timeProvider.GetUtcNow());
        var retained = selection.Requests.ToList();
        var fileOverflowCount = 0;

        for (int index = retained.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SerializeRequests([retained[index]]).LongLength <= _maximumFileSize)
                continue;

            retained.RemoveAt(index);
            fileOverflowCount++;
        }

        byte[]? serialized = retained.Count == 0 ? null : SerializeRequests(retained);
        while (serialized is not null && serialized.LongLength > _maximumFileSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            retained.RemoveAt(0);
            fileOverflowCount++;
            serialized = retained.Count == 0 ? null : SerializeRequests(retained);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (retained.Count == 0)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            WritePersistenceSelectionDiagnostics(selection, fileOverflowCount);
            return new PersistResult([]);
        }

        System.Diagnostics.Debug.Assert(serialized is not null);
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

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
                await stream.WriteAsync(serialized, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        WritePersistenceSelectionDiagnostics(selection, fileOverflowCount);
        return new PersistResult(retained.ToArray());
    }

    private byte[] SerializeRequests(IReadOnlyList<StoredRequest> requests)
    {
        DeferredNavigationRequestStoreSnapshot snapshot = _serializer.CreateSnapshot(
            requests.Select(static stored => stored.Request).ToArray());
        return JsonSerializer.SerializeToUtf8Bytes(
            snapshot,
            AppNavJsonSerializerContext.Default.DeferredNavigationRequestStoreSnapshot);
    }

    private async ValueTask AcknowledgeAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            int index = _requests.FindIndex(entry => entry.Id == id);
            if (index < 0)
            {
                // A bounded concurrent enqueue may already have durably evicted this leased
                // request. Treat acknowledgement as idempotent because there is nothing left
                // that could replay after a restart.
                return;
            }

            StoredRequest[] projected = _requests
                .Where(entry => entry.Id != id)
                .ToArray();
            PersistResult persisted = await PersistAsync(projected, cancellationToken).ConfigureAwait(false);
            ReplaceInMemory(persisted.Requests);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RouterNavigationRequest Canonicalize(RouterNavigationRequest request)
    {
        DeferredNavigationRequestStoreSnapshot snapshot = _serializer.CreateSnapshot([request]);
        return AssertSingle(_serializer.Restore(snapshot));

        static RouterNavigationRequest AssertSingle(IReadOnlyList<RouterNavigationRequest> requests)
        {
            if (requests.Count != 1)
                throw new InvalidOperationException("Canonical deferred navigation request serialization failed.");

            return requests[0];
        }
    }

    private async Task PruneExpiredAsync(CancellationToken cancellationToken)
    {
        PersistSelection selection = SelectForPersistence(_requests, _timeProvider.GetUtcNow());
        if (selection.ExpiredCount == 0 && selection.OverflowCount == 0)
            return;

        PersistResult persisted = await PersistAsync(_requests, cancellationToken).ConfigureAwait(false);
        ReplaceInMemory(persisted.Requests);
    }

    private PersistSelection SelectForPersistence(
        IReadOnlyList<StoredRequest> requests,
        DateTimeOffset now)
    {
        DateTimeOffset oldestAllowed = _maximumRequestAge >= now - DateTimeOffset.MinValue
            ? DateTimeOffset.MinValue
            : now - _maximumRequestAge;
        StoredRequest[] fresh = requests
            .Where(stored => stored.Request.Timestamp >= oldestAllowed && stored.Request.Timestamp <= now)
            .ToArray();
        int expiredCount = requests.Count - fresh.Length;
        int overflowCount = Math.Max(0, fresh.Length - _maximumPendingRequests);
        StoredRequest[] retained = overflowCount == 0
            ? fresh
            : fresh[overflowCount..];

        return new PersistSelection(retained, expiredCount, overflowCount);
    }

    private void ReplaceInMemory(IReadOnlyList<StoredRequest> requests)
    {
        ResetInMemory();
        foreach (StoredRequest stored in requests)
        {
            _requests.Add(stored);
            _deduped.Add(stored.Request);
        }
    }

    private void ResetInMemory()
    {
        _requests.Clear();
        _deduped.Clear();
    }

    private void WritePersistenceSelectionDiagnostics(PersistSelection selection, int fileOverflowCount)
    {
        if (selection.ExpiredCount > 0)
        {
            WriteMaintenanceDiagnostic(
                NavigationDiagnosticEventKind.DeferredRequestStorePruned,
                "Expired deferred navigation requests were pruned.",
                selection.ExpiredCount,
                "request-expired");
        }

        int overflowCount = selection.OverflowCount + fileOverflowCount;
        if (overflowCount > 0)
        {
            WriteMaintenanceDiagnostic(
                NavigationDiagnosticEventKind.DeferredRequestStoreOverflowed,
                "Deferred navigation requests were dropped to preserve store bounds.",
                overflowCount,
                fileOverflowCount > 0 ? "file-size-or-count-limit" : "request-count-limit");
        }
    }

    private void WriteMaintenanceDiagnostic(
        NavigationDiagnosticEventKind kind,
        string message,
        int? count,
        string reason,
        int? schemaVersion = null)
    {
        var data = new Dictionary<string, object?>
        {
            [NavigationDiagnosticDataKeys.Reason] = reason
        };
        if (count is not null)
            data[NavigationDiagnosticDataKeys.Count] = count.Value;
        if (schemaVersion is not null)
            data[NavigationDiagnosticDataKeys.SchemaVersion] = schemaVersion.Value;

        _diagnostics.Write(
            kind,
            Guid.NewGuid().ToString("N"),
            message,
            data,
            phase: NavigationDiagnosticPhase.Persistence);
    }

    private async ValueTask ReleaseReplayLeaseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _activeReplayRequestIds.Clear();
        }
        finally
        {
            _gate.Release();
            _replayGate.Release();
        }
    }

    private sealed record StoredRequest(Guid Id, RouterNavigationRequest Request);

    private sealed record PersistSelection(
        StoredRequest[] Requests,
        int ExpiredCount,
        int OverflowCount);

    private sealed record PersistResult(StoredRequest[] Requests);

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
                await _owner.ReleaseReplayLeaseAsync().ConfigureAwait(false);
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

internal interface IMauiDeferredNavigationFileOperations
{
    void Move(string sourcePath, string destinationPath);
}

internal sealed class MauiDeferredNavigationFileOperations : IMauiDeferredNavigationFileOperations
{
    public static MauiDeferredNavigationFileOperations Instance { get; } = new();

    public void Move(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath);
}

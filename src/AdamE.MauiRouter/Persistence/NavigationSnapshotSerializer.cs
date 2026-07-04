using System.ComponentModel;
using System.Globalization;
using AdamE.MauiRouter.History;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Requests;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;

namespace AdamE.MauiRouter.Persistence;

internal sealed class NavigationSnapshotSerializer
{
    private readonly RouteTable _routes;
    private readonly NavigationPersistenceOptions _options;

    public NavigationSnapshotSerializer(RouteTable routes, NavigationPersistenceOptions? options = null)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _options = options ?? new NavigationPersistenceOptions();
    }

    public NavigationSnapshot CreateSnapshot(NavigationState state, NavigationHistory history)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(history);

        return new NavigationSnapshot
        {
            SchemaVersion = NavigationSnapshot.CurrentSchemaVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            State = CreateStateSnapshot(state),
            History = _options.PersistHistory ? CreateHistorySnapshot(history) : null
        };
    }

    public NavigationRestoreResult Restore(NavigationSnapshot snapshot, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.SchemaVersion is not 3 and not NavigationSnapshot.CurrentSchemaVersion)
        {
            return NavigationRestoreResult.Rejected(
                $"Navigation snapshot schema version {snapshot.SchemaVersion} is not supported.",
                new[]
                {
                    new RouteDiagnostic(
                        "snapshot.schema.unsupported",
                            $"Expected schema version 3 or {NavigationSnapshot.CurrentSchemaVersion}, received {snapshot.SchemaVersion}.")
                });
        }

        if (_options.MaxSnapshotAge is { } maxAge)
        {
            var age = (now ?? DateTimeOffset.UtcNow) - snapshot.CreatedAt;
            if (age > maxAge)
            {
                return NavigationRestoreResult.Rejected(
                    $"Navigation snapshot is older than the configured maximum age of {maxAge}.",
                    new[]
                    {
                        new RouteDiagnostic(
                            "snapshot.expired",
                            $"Snapshot age is {age}.")
                    });
            }
        }

        if (!TryRestoreState(snapshot.State, strict: true, out var state, out var stateDiagnostics))
        {
            return NavigationRestoreResult.Rejected(
                "Navigation snapshot current state contains an invalid route.",
                stateDiagnostics);
        }

        var (history, historyDiagnostics) = RestoreHistory(snapshot.History);
        return NavigationRestoreResult.AcceptedResult(state!, history, presented: false, historyDiagnostics);
    }

    private NavigationStateSnapshot CreateStateSnapshot(NavigationState state)
    {
        return new NavigationStateSnapshot(
            state.Windows.Select(CreateWindowSnapshot).ToArray(),
            state.ActiveWindowId);
    }

    private WindowNodeSnapshot CreateWindowSnapshot(WindowNode window)
    {
        return new WindowNodeSnapshot(
            window.Id,
            window.Root is null ? null : CreateNodeSnapshot(window.Root),
            _options.PersistModals
                ? window.Modals.Select(CreateModalSnapshot).ToArray()
                : Array.Empty<ModalNodeSnapshot>());
    }

    private NavigationNodeSnapshot CreateNodeSnapshot(NavigationNode node)
    {
        return node switch
        {
            StackNode stack => new StackNodeSnapshot(
                stack.Id,
                stack.Entries.Select(CreateRouteEntrySnapshot).ToArray()),
            BranchHostNode branchHost => new BranchHostNodeSnapshot(
                branchHost.Id,
                branchHost.Branches.Select(CreateBranchSnapshot).ToArray(),
                branchHost.SelectedBranchId,
                branchHost.DefaultBranchId),
            ModalNode modal => CreateModalSnapshot(modal),
            _ => throw new NotSupportedException($"Navigation node '{node.GetType().FullName}' cannot be snapshotted.")
        };
    }

    private ModalNodeSnapshot CreateModalSnapshot(ModalNode modal)
    {
        return new ModalNodeSnapshot(
            modal.Id,
            CreateRouteEntrySnapshot(modal.RouteEntry),
            modal.Content is null ? null : CreateNodeSnapshot(modal.Content));
    }

    private NavigationBranchSnapshot CreateBranchSnapshot(NavigationBranch branch)
    {
        return new NavigationBranchSnapshot(
            branch.Id,
            branch.Title,
            CreateNodeSnapshot(branch.Content));
    }

    private RouteEntrySnapshot CreateRouteEntrySnapshot(RouteEntry entry)
    {
        return new RouteEntrySnapshot(
            entry.Id,
            FormatCanonicalRouteUri(entry.Route, entry.Metadata),
            entry.Transition is null ? null : CreateTransitionSnapshot(entry.Transition),
            SerializeMetadata(entry.Metadata));
    }

    private static NavigationTransitionSnapshot CreateTransitionSnapshot(NavigationTransition transition)
    {
        return transition switch
        {
            NoNavigationTransition => new NoNavigationTransitionSnapshot(),
            PlatformDefaultNavigationTransition => new PlatformDefaultNavigationTransitionSnapshot(),
            FadeNavigationTransition fade => new FadeNavigationTransitionSnapshot(fade.Duration),
            SlideNavigationTransition slide => new SlideNavigationTransitionSnapshot(slide.Direction, slide.Duration),
            SharedElementNavigationTransition shared => new SharedElementNavigationTransitionSnapshot(
                shared.Elements.Select(element => new SharedElementPairSnapshot(element.SourceId, element.DestinationId)).ToArray(),
                shared.Fallback is null ? null : CreateTransitionSnapshot(shared.Fallback),
                shared.Duration),
            _ => throw new NotSupportedException($"Navigation transition '{transition.GetType().FullName}' cannot be snapshotted.")
        };
    }

    private NavigationHistorySnapshot? CreateHistorySnapshot(NavigationHistory history)
    {
        if (history.Entries.Count == 0)
        {
            return null;
        }

        return new NavigationHistorySnapshot(
            history.Entries.Select(CreateHistoryEntrySnapshot).ToArray(),
            history.CurrentIndex);
    }

    private NavigationHistoryEntrySnapshot CreateHistoryEntrySnapshot(NavigationHistoryEntry entry)
    {
        return new NavigationHistoryEntrySnapshot(
            entry.Id,
            CreateRequestSnapshot(entry.Request, entry.Route),
            _routes.FormatUri(entry.Route, _options.BaseUri).ToString(),
            CreateStateSnapshot(entry.State),
            entry.Reason,
            entry.Timestamp);
    }

    private NavigationRequestSnapshot CreateRequestSnapshot(RouterNavigationRequest request, AppRoute fallbackRoute)
    {
        var route = request.Route ?? fallbackRoute;
        return new NavigationRequestSnapshot(
            request.Uri?.ToString(),
            FormatCanonicalRouteUri(route, request.Metadata),
            request.Source,
            request.WindowId,
            SerializeMetadata(request.Metadata),
            request.Timestamp,
            request.Disposition,
            NavigationRequestProvenanceSnapshotMapper.Create(request.Provenance));
    }

    private bool TryRestoreState(
        NavigationStateSnapshot snapshot,
        bool strict,
        out NavigationState? state,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        var restoredWindows = new List<WindowNode>(snapshot.Windows.Count);
        var allDiagnostics = new List<RouteDiagnostic>();

        foreach (var windowSnapshot in snapshot.Windows)
        {
            if (!TryRestoreWindow(windowSnapshot, strict, out var window, out var windowDiagnostics))
            {
                allDiagnostics.AddRange(windowDiagnostics);
                if (strict)
                {
                    state = null;
                    diagnostics = allDiagnostics;
                    return false;
                }

                continue;
            }

            restoredWindows.Add(window!);
        }

        state = new NavigationState(restoredWindows, snapshot.ActiveWindowId);
        diagnostics = allDiagnostics;
        return true;
    }

    private bool TryRestoreWindow(
        WindowNodeSnapshot snapshot,
        bool strict,
        out WindowNode? window,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        var allDiagnostics = new List<RouteDiagnostic>();
        NavigationNode? root = null;
        if (snapshot.Root is not null &&
            !TryRestoreNode(snapshot.Root, strict, out root, out var rootDiagnostics))
        {
            allDiagnostics.AddRange(rootDiagnostics);
            window = null;
            diagnostics = allDiagnostics;
            return false;
        }

        var modals = new List<ModalNode>(snapshot.Modals.Count);
        foreach (var modalSnapshot in snapshot.Modals)
        {
            if (!TryRestoreNode(modalSnapshot, strict, out var modalNode, out var modalDiagnostics))
            {
                allDiagnostics.AddRange(modalDiagnostics);
                if (strict)
                {
                    window = null;
                    diagnostics = allDiagnostics;
                    return false;
                }

                continue;
            }

            if (modalNode is ModalNode modal)
            {
                modals.Add(modal);
            }
        }

        window = new WindowNode(snapshot.Id, root, modals);
        diagnostics = allDiagnostics;
        return true;
    }

    private bool TryRestoreNode(
        NavigationNodeSnapshot snapshot,
        bool strict,
        out NavigationNode? node,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        switch (snapshot)
        {
            case StackNodeSnapshot stack:
                return TryRestoreStack(stack, strict, out node, out diagnostics);
            case BranchHostNodeSnapshot branchHost:
                return TryRestoreBranchHost(branchHost, strict, out node, out diagnostics);
            case LegacyTabsNodeSnapshot tabs:
                return TryRestoreLegacyTabs(tabs, strict, out node, out diagnostics);
            case LegacyFlyoutNodeSnapshot flyout:
                return TryRestoreLegacyFlyout(flyout, strict, out node, out diagnostics);
            case ModalNodeSnapshot modal:
                return TryRestoreModal(modal, strict, out node, out diagnostics);
            default:
                node = null;
                diagnostics = new[]
                {
                    new RouteDiagnostic(
                        "snapshot.node.unsupported",
                        $"Snapshot node type '{snapshot.GetType().FullName}' is not supported.")
                };
                return false;
        }
    }

    private bool TryRestoreStack(
        StackNodeSnapshot snapshot,
        bool strict,
        out NavigationNode? node,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        var entries = new List<RouteEntry>(snapshot.Entries.Count);
        var allDiagnostics = new List<RouteDiagnostic>();
        foreach (var entrySnapshot in snapshot.Entries)
        {
            if (!TryRestoreRouteEntry(entrySnapshot, out var entry, out var entryDiagnostics))
            {
                allDiagnostics.AddRange(entryDiagnostics);
                if (strict)
                {
                    node = null;
                    diagnostics = allDiagnostics;
                    return false;
                }

                continue;
            }

            entries.Add(entry!);
        }

        node = new StackNode(snapshot.Id, entries);
        diagnostics = allDiagnostics;
        return true;
    }

    private bool TryRestoreBranchHost(
        BranchHostNodeSnapshot snapshot,
        bool strict,
        out NavigationNode? node,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        var branches = new List<NavigationBranch>(snapshot.Branches.Count);
        if (!TryRestoreBranches(snapshot.Branches, strict, branches, out diagnostics))
        {
            node = null;
            return false;
        }

        node = new BranchHostNode(snapshot.Id, branches, snapshot.SelectedBranchId, snapshot.DefaultBranchId);
        return true;
    }

    private bool TryRestoreLegacyTabs(
        LegacyTabsNodeSnapshot snapshot,
        bool strict,
        out NavigationNode? node,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        var branches = new List<NavigationBranch>(snapshot.Branches.Count);
        if (!TryRestoreBranches(snapshot.Branches, strict, branches, out diagnostics))
        {
            node = null;
            return false;
        }

        node = new BranchHostNode(snapshot.Id, branches, snapshot.SelectedTabId, snapshot.DefaultTabId);
        return true;
    }

    private bool TryRestoreLegacyFlyout(
        LegacyFlyoutNodeSnapshot snapshot,
        bool strict,
        out NavigationNode? node,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        var branches = new List<NavigationBranch>(snapshot.Branches.Count);
        if (!TryRestoreBranches(snapshot.Branches, strict, branches, out diagnostics))
        {
            node = null;
            return false;
        }

        node = new BranchHostNode(snapshot.Id, branches, snapshot.SelectedItemId, snapshot.DefaultItemId);
        return true;
    }

    private bool TryRestoreBranches(
        IReadOnlyList<NavigationBranchSnapshot> snapshots,
        bool strict,
        List<NavigationBranch> branches,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        var allDiagnostics = new List<RouteDiagnostic>();
        foreach (var branchSnapshot in snapshots)
        {
            if (!TryRestoreNode(branchSnapshot.Content, strict, out var content, out var branchDiagnostics))
            {
                allDiagnostics.AddRange(branchDiagnostics);
                if (strict)
                {
                    diagnostics = allDiagnostics;
                    return false;
                }

                continue;
            }

            branches.Add(new NavigationBranch(branchSnapshot.Id, branchSnapshot.Title, content!));
        }

        diagnostics = allDiagnostics;
        return true;
    }

    private bool TryRestoreModal(
        ModalNodeSnapshot snapshot,
        bool strict,
        out NavigationNode? node,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        var allDiagnostics = new List<RouteDiagnostic>();
        if (!TryRestoreRouteEntry(snapshot.RouteEntry, out var routeEntry, out var routeDiagnostics))
        {
            node = null;
            diagnostics = routeDiagnostics;
            return false;
        }

        NavigationNode? content = null;
        if (snapshot.Content is not null &&
            !TryRestoreNode(snapshot.Content, strict, out content, out var contentDiagnostics))
        {
            allDiagnostics.AddRange(contentDiagnostics);
            node = null;
            diagnostics = allDiagnostics;
            return false;
        }

        node = new ModalNode(snapshot.Id, routeEntry!, content);
        diagnostics = allDiagnostics;
        return true;
    }

    private bool TryRestoreRouteEntry(
        RouteEntrySnapshot snapshot,
        out RouteEntry? entry,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        if (!TryRestoreRoute(snapshot.RouteUri, out var route, out var routeMetadata, out diagnostics))
        {
            entry = null;
            return false;
        }

        if (!TryDeserializeMetadata(snapshot.Metadata, out var persistedMetadata, out diagnostics))
        {
            entry = null;
            return false;
        }

        entry = new RouteEntry(
            snapshot.Id,
            route!,
            snapshot.Transition is null ? null : RestoreTransition(snapshot.Transition),
            MergeMetadata(routeMetadata, persistedMetadata));
        return true;
    }

    private static NavigationTransition RestoreTransition(NavigationTransitionSnapshot snapshot)
    {
        return snapshot switch
        {
            NoNavigationTransitionSnapshot => new NoNavigationTransition(),
            PlatformDefaultNavigationTransitionSnapshot => new PlatformDefaultNavigationTransition(),
            FadeNavigationTransitionSnapshot fade => new FadeNavigationTransition(fade.Duration),
            SlideNavigationTransitionSnapshot slide => new SlideNavigationTransition(slide.Direction, slide.Duration),
            SharedElementNavigationTransitionSnapshot shared => new SharedElementNavigationTransition(
                shared.Elements.Select(element => new SharedElementPair(element.SourceId, element.DestinationId)).ToArray(),
                shared.Fallback is null ? null : RestoreTransition(shared.Fallback),
                shared.Duration),
            _ => throw new NotSupportedException($"Navigation transition snapshot '{snapshot.GetType().FullName}' cannot be restored.")
        };
    }

    private (NavigationHistory History, IReadOnlyList<RouteDiagnostic> Diagnostics) RestoreHistory(NavigationHistorySnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Entries.Count == 0 || !_options.PersistHistory)
        {
            return (NavigationHistory.Empty, Array.Empty<RouteDiagnostic>());
        }

        var entries = new List<NavigationHistoryEntry>(snapshot.Entries.Count);
        var diagnostics = new List<RouteDiagnostic>();
        var currentIndex = -1;
        for (var i = 0; i < snapshot.Entries.Count; i++)
        {
            if (!TryRestoreHistoryEntry(snapshot.Entries[i], out var entry, out var entryDiagnostics))
            {
                diagnostics.AddRange(entryDiagnostics);
                continue;
            }

            entries.Add(entry!);
            if (i <= snapshot.CurrentIndex)
            {
                currentIndex = entries.Count - 1;
            }
        }

        if (entries.Count == 0)
        {
            return (NavigationHistory.Empty, diagnostics);
        }

        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        if (currentIndex >= entries.Count)
        {
            currentIndex = entries.Count - 1;
        }

        return (new NavigationHistory(entries, currentIndex), diagnostics);
    }

    private bool TryRestoreHistoryEntry(
        NavigationHistoryEntrySnapshot snapshot,
        out NavigationHistoryEntry? entry,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        entry = null;
        var allDiagnostics = new List<RouteDiagnostic>();
        if (!TryRestoreRoute(snapshot.RouteUri, out var route, out _, out var routeDiagnostics))
        {
            diagnostics = routeDiagnostics;
            return false;
        }

        if (!TryRestoreRoute(snapshot.Request.RouteUri, out var requestRoute, out var requestRouteMetadata, out var requestRouteDiagnostics))
        {
            diagnostics = requestRouteDiagnostics;
            return false;
        }

        if (!TryRestoreState(snapshot.State, strict: false, out var state, out var stateDiagnostics) ||
            state is null)
        {
            allDiagnostics.AddRange(stateDiagnostics);
            diagnostics = allDiagnostics;
            return false;
        }

        Uri? requestUri = null;
        if (!string.IsNullOrWhiteSpace(snapshot.Request.Uri) &&
            !Uri.TryCreate(snapshot.Request.Uri, UriKind.RelativeOrAbsolute, out requestUri))
        {
            diagnostics = new[]
            {
                new RouteDiagnostic(
                    "snapshot.history.request_uri_invalid",
                    $"History request URI '{snapshot.Request.Uri}' is not a valid URI.")
            };
            return false;
        }

        if (!TryDeserializeMetadata(snapshot.Request.Metadata, out var requestMetadata, out diagnostics))
        {
            return false;
        }

        var request = new RouterNavigationRequest(
            requestUri,
            requestRoute,
            snapshot.Request.Source,
            snapshot.Request.WindowId,
            MergeMetadata(requestRouteMetadata, requestMetadata),
            snapshot.Request.Timestamp,
            snapshot.Request.Disposition,
            NavigationRequestProvenanceSnapshotMapper.Restore(snapshot.Request.Provenance));

        entry = new NavigationHistoryEntry(
            snapshot.Id,
            request,
            route!,
            state,
            snapshot.Reason,
            snapshot.Timestamp);
        diagnostics = Array.Empty<RouteDiagnostic>();
        return true;
    }

    private bool TryRestoreRoute(
        string routeUri,
        out AppRoute? route,
        out IReadOnlyDictionary<string, object?>? metadata,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        metadata = null;
        if (!Uri.TryCreate(routeUri, UriKind.RelativeOrAbsolute, out var uri))
        {
            route = null;
            diagnostics = new[]
            {
                new RouteDiagnostic(
                    "snapshot.route.uri_invalid",
                    $"Snapshot route URI '{routeUri}' is not a valid URI.")
            };
            return false;
        }

        var match = _routes.Match(uri);
        if (match.IsSuccess && match.Route is not null)
        {
            route = match.Route;
            metadata = match.Metadata;
            diagnostics = Array.Empty<RouteDiagnostic>();
            return true;
        }

        route = null;
        diagnostics = match.Diagnostics;
        return false;
    }

    private bool TryDeserializeMetadata(
        IReadOnlyDictionary<string, NavigationMetadataValueSnapshot>? metadata,
        out IReadOnlyDictionary<string, object?>? restoredMetadata,
        out IReadOnlyList<RouteDiagnostic> diagnostics)
    {
        try
        {
            restoredMetadata = DeserializeMetadata(metadata);
            diagnostics = Array.Empty<RouteDiagnostic>();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or NotSupportedException)
        {
            restoredMetadata = null;
            diagnostics = new[]
            {
                new RouteDiagnostic(
                    "snapshot.metadata.invalid",
                    ex.Message)
            };
            return false;
        }
    }

    private IReadOnlyDictionary<string, NavigationMetadataValueSnapshot>? SerializeMetadata(
        IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        Dictionary<string, NavigationMetadataValueSnapshot>? serialized = null;
        if (_options.RouteStateRegistry is { } routeStateRegistry)
        {
            foreach (var pair in metadata)
            {
                if (!routeStateRegistry.TryGetRegistration(pair.Key, out var registration) ||
                    registration.Lifetime != RouteStateLifetime.Restorable)
                {
                    continue;
                }

                serialized ??= new Dictionary<string, NavigationMetadataValueSnapshot>(StringComparer.Ordinal);
                serialized[pair.Key] = SerializeValueSnapshot(pair.Key, pair.Value, registration.ValueType);
            }
        }

        if (_options.MetadataSerializer is not null)
        {
            var unknownMetadata = FilterUnknownMetadata(metadata);
            if (unknownMetadata is { Count: > 0 })
            {
                var customMetadata = _options.MetadataSerializer.Serialize(unknownMetadata);
                if (customMetadata is { Count: > 0 })
                {
                    foreach (var pair in customMetadata)
                    {
                        serialized ??= new Dictionary<string, NavigationMetadataValueSnapshot>(StringComparer.Ordinal);
                        serialized[pair.Key] = SerializeValueSnapshot(pair.Key, pair.Value);
                    }
                }
            }
        }

        return serialized;
    }

    private string FormatCanonicalRouteUri(AppRoute route, IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return _routes.FormatUri(route, _options.BaseUri).ToString();
        }

        var request = new AppRouteRequest(route, metadata);
        if (_options.RouteStateRegistry is { } routeStateRegistry)
        {
            request = routeStateRegistry.Canonicalize(request);
        }

        return _routes.FormatUri(request, _options.BaseUri).ToString();
    }

    private IReadOnlyDictionary<string, object?>? DeserializeMetadata(
        IReadOnlyDictionary<string, NavigationMetadataValueSnapshot>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        Dictionary<string, object?>? restored = null;
        Dictionary<string, object?>? customMetadata = null;
        foreach (var pair in metadata)
        {
            if (_options.RouteStateRegistry is { } routeStateRegistry &&
                routeStateRegistry.TryGetRegistration(pair.Key, out var registration))
            {
                if (registration.Lifetime != RouteStateLifetime.Restorable)
                {
                    continue;
                }

                restored ??= new Dictionary<string, object?>(StringComparer.Ordinal);
                restored[pair.Key] = DeserializeValueSnapshot(pair.Key, pair.Value, registration.ValueType);
                continue;
            }

            if (_options.MetadataSerializer is null)
            {
                continue;
            }

            customMetadata ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            customMetadata[pair.Key] = DeserializeValueSnapshot(pair.Key, pair.Value);
        }

        if (customMetadata is { Count: > 0 } && _options.MetadataSerializer is not null)
        {
            var deserializedCustomMetadata = _options.MetadataSerializer.Deserialize(customMetadata);
            if (deserializedCustomMetadata is { Count: > 0 })
            {
                restored ??= new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var pair in deserializedCustomMetadata)
                {
                    restored[pair.Key] = pair.Value;
                }
            }
        }

        return restored;
    }

    private IReadOnlyDictionary<string, object?>? FilterUnknownMetadata(
        IReadOnlyDictionary<string, object?> metadata)
    {
        if (_options.RouteStateRegistry is null)
        {
            return metadata;
        }

        Dictionary<string, object?>? unknownMetadata = null;
        foreach (var pair in metadata)
        {
            if (_options.RouteStateRegistry.TryGetRegistration(pair.Key, out _))
            {
                continue;
            }

            unknownMetadata ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            unknownMetadata[pair.Key] = pair.Value;
        }

        return unknownMetadata;
    }

    private static NavigationMetadataValueSnapshot SerializeValueSnapshot(
        string key,
        object? value,
        Type? declaredType = null)
    {
        if (value is null)
        {
            return new NavigationMetadataValueSnapshot(
                declaredType?.AssemblyQualifiedName,
                Value: null,
                IsNull: true);
        }

        var valueType = declaredType ?? value.GetType();
        return new NavigationMetadataValueSnapshot(
            valueType.AssemblyQualifiedName,
            SerializeValue(key, value, valueType));
    }

    private static object? DeserializeValueSnapshot(
        string key,
        NavigationMetadataValueSnapshot snapshot,
        Type? declaredType = null)
    {
        if (snapshot.IsNull)
        {
            return null;
        }

        if (snapshot.Value is null)
        {
            return null;
        }

        var valueType = declaredType ?? ResolveValueType(key, snapshot.Type);
        return valueType is null || valueType == typeof(string)
            ? snapshot.Value
            : RouteValueConverter.Convert(snapshot.Value, valueType, key);
    }

    private static Type? ResolveValueType(string key, string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var valueType = Type.GetType(typeName, throwOnError: false);
        if (valueType is not null)
        {
            return valueType;
        }

        throw new InvalidOperationException(
            $"Navigation metadata '{key}' declared persisted type '{typeName}' could not be resolved.");
    }

    private static string SerializeValue(string key, object value, Type declaredType)
    {
        var conversionType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        try
        {
            if (conversionType == typeof(string))
            {
                return (string)value;
            }

            if (conversionType.IsEnum)
            {
                return value.ToString()!;
            }

            var converter = TypeDescriptor.GetConverter(conversionType);
            if (converter.CanConvertTo(typeof(string)))
            {
                var converted = converter.ConvertTo(null, CultureInfo.InvariantCulture, value, typeof(string)) as string;
                if (converted is not null)
                {
                    return converted;
                }
            }

            if (value is IFormattable formattable)
            {
                var formatted = formattable.ToString(null, CultureInfo.InvariantCulture);
                if (formatted is not null)
                {
                    return formatted;
                }
            }

            if (value.ToString() is { } fallback)
            {
                return fallback;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or NotSupportedException)
        {
            throw new FormatException(
                $"Navigation metadata '{key}' could not be serialized as {conversionType.Name}.",
                ex);
        }

        throw new NotSupportedException(
            $"Navigation metadata '{key}' cannot be serialized as {conversionType.FullName}.");
    }

    private static IReadOnlyDictionary<string, object?>? MergeMetadata(
        IReadOnlyDictionary<string, object?>? lowerPriority,
        IReadOnlyDictionary<string, object?>? higherPriority)
    {
        if ((lowerPriority is null || lowerPriority.Count == 0) &&
            (higherPriority is null || higherPriority.Count == 0))
        {
            return null;
        }

        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (lowerPriority is not null)
        {
            foreach (var pair in lowerPriority)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        if (higherPriority is not null)
        {
            foreach (var pair in higherPriority)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return merged;
    }
}

using System.Text.Json.Serialization;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Persistence;

public sealed record NavigationSnapshot
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public NavigationStateSnapshot State { get; init; } = NavigationStateSnapshot.Empty;

    public NavigationHistorySnapshot? History { get; init; }

    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

public sealed record NavigationStateSnapshot(
    IReadOnlyList<WindowNodeSnapshot> Windows,
    string? ActiveWindowId)
{
    public static NavigationStateSnapshot Empty { get; } = new(Array.Empty<WindowNodeSnapshot>(), null);
}

public sealed record WindowNodeSnapshot(
    string Id,
    NavigationNodeSnapshot? Root,
    IReadOnlyList<ModalNodeSnapshot> Modals);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StackNodeSnapshot), "stack")]
[JsonDerivedType(typeof(TabsNodeSnapshot), "tabs")]
[JsonDerivedType(typeof(FlyoutNodeSnapshot), "flyout")]
[JsonDerivedType(typeof(ModalNodeSnapshot), "modal")]
public abstract record NavigationNodeSnapshot(string Id);

public sealed record StackNodeSnapshot(
    string Id,
    IReadOnlyList<RouteEntrySnapshot> Entries) : NavigationNodeSnapshot(Id);

public sealed record TabsNodeSnapshot(
    string Id,
    IReadOnlyList<NavigationBranchSnapshot> Branches,
    string SelectedTabId,
    string? DefaultTabId) : NavigationNodeSnapshot(Id);

public sealed record FlyoutNodeSnapshot(
    string Id,
    IReadOnlyList<NavigationBranchSnapshot> Branches,
    string SelectedItemId,
    string? DefaultItemId) : NavigationNodeSnapshot(Id);

public sealed record ModalNodeSnapshot(
    string Id,
    RouteEntrySnapshot RouteEntry,
    NavigationNodeSnapshot? Content) : NavigationNodeSnapshot(Id);

public sealed record NavigationBranchSnapshot(
    string Id,
    string Title,
    NavigationNodeSnapshot Content);

public sealed record NavigationMetadataValueSnapshot(
    string? Type,
    string? Value,
    bool IsNull = false);

public sealed record RouteEntrySnapshot(
    string Id,
    string RouteUri,
    NavigationTransitionSnapshot? Transition,
    IReadOnlyDictionary<string, NavigationMetadataValueSnapshot>? Metadata);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NoNavigationTransitionSnapshot), "none")]
[JsonDerivedType(typeof(PlatformDefaultNavigationTransitionSnapshot), "platformDefault")]
[JsonDerivedType(typeof(FadeNavigationTransitionSnapshot), "fade")]
[JsonDerivedType(typeof(SlideNavigationTransitionSnapshot), "slide")]
[JsonDerivedType(typeof(SharedElementNavigationTransitionSnapshot), "sharedElement")]
public abstract record NavigationTransitionSnapshot;

public sealed record NoNavigationTransitionSnapshot : NavigationTransitionSnapshot;

public sealed record PlatformDefaultNavigationTransitionSnapshot : NavigationTransitionSnapshot;

public sealed record FadeNavigationTransitionSnapshot(TimeSpan? Duration) : NavigationTransitionSnapshot;

public sealed record SlideNavigationTransitionSnapshot(
    NavigationSlideDirection Direction,
    TimeSpan? Duration) : NavigationTransitionSnapshot;

public sealed record SharedElementNavigationTransitionSnapshot(
    IReadOnlyList<SharedElementPairSnapshot> Elements,
    NavigationTransitionSnapshot? Fallback,
    TimeSpan? Duration) : NavigationTransitionSnapshot;

public sealed record SharedElementPairSnapshot(string SourceId, string DestinationId);

public sealed record NavigationHistorySnapshot(
    IReadOnlyList<NavigationHistoryEntrySnapshot> Entries,
    int CurrentIndex);

public sealed record NavigationHistoryEntrySnapshot(
    string Id,
    NavigationRequestSnapshot Request,
    string RouteUri,
    NavigationStateSnapshot State,
    string? Reason,
    DateTimeOffset Timestamp);

public sealed record NavigationRequestProvenanceSnapshot(
    string? Provider,
    string? OriginalUri,
    string? ReferrerUri,
    string? CorrelationId,
    bool? IsColdStart,
    IReadOnlyDictionary<string, string?>? Attributes);

public sealed record NavigationRequestSnapshot(
    string? Uri,
    string RouteUri,
    NavigationRequestSource Source,
    string? WindowId,
    IReadOnlyDictionary<string, NavigationMetadataValueSnapshot>? Metadata,
    DateTimeOffset Timestamp,
    RouterNavigationDisposition Disposition = RouterNavigationDisposition.Auto,
    NavigationRequestProvenanceSnapshot? Provenance = null);

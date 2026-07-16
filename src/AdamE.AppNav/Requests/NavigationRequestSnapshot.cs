namespace AdamE.AppNav.Requests;

/// <summary>
/// Represents a serialized navigation request for deferred request persistence.
/// </summary>
public sealed record NavigationRequestSnapshot(
    string? Uri,
    string RouteUri,
    NavigationRequestSource Source,
    string? WindowId,
    IReadOnlyDictionary<string, NavigationMetadataValueSnapshot>? Metadata,
    DateTimeOffset Timestamp,
    RouterNavigationDisposition Disposition,
    NavigationRequestProvenanceSnapshot? Provenance);

/// <summary>
/// Represents serialized provenance metadata for a deferred navigation request.
/// </summary>
public sealed record NavigationRequestProvenanceSnapshot(
    string? Provider,
    string? OriginalUri,
    string? ReferrerUri,
    string? CorrelationId,
    bool? IsColdStart,
    IReadOnlyDictionary<string, string?>? Attributes);

/// <summary>
/// Represents one serialized navigation metadata value.
/// </summary>
/// <param name="Type">A stable scalar discriminator, or <see langword="null"/> for registered route state.</param>
/// <param name="Value">The invariant serialized value.</param>
/// <param name="IsNull">Whether the original value was <see langword="null"/>.</param>
public sealed record NavigationMetadataValueSnapshot(
    string? Type,
    string? Value,
    bool IsNull);

/// <summary>
/// Serializes custom request metadata not handled by a route state registry.
/// </summary>
public interface INavigationRequestMetadataSerializer
{
    /// <summary>
    /// Converts custom request metadata into primitive values that can be stored with a deferred request.
    /// </summary>
    /// <param name="metadata">The metadata values to serialize.</param>
    /// <returns>The serialized metadata values, or <see langword="null"/> when no values should be stored.</returns>
    IReadOnlyDictionary<string, object?>? Serialize(IReadOnlyDictionary<string, object?> metadata);

    /// <summary>
    /// Converts stored custom request metadata back into runtime metadata values.
    /// </summary>
    /// <param name="metadata">The stored metadata values to deserialize.</param>
    /// <returns>The deserialized metadata values, or <see langword="null"/> when no values should be restored.</returns>
    IReadOnlyDictionary<string, object?>? Deserialize(IReadOnlyDictionary<string, object?> metadata);
}

internal static class NavigationRequestProvenanceSnapshotMapper
{
    public static NavigationRequestProvenanceSnapshot? Create(NavigationRequestProvenance? provenance)
    {
        if (provenance is null)
            return null;

        return new NavigationRequestProvenanceSnapshot(
            provenance.Provider,
            provenance.OriginalUri?.ToString(),
            provenance.ReferrerUri?.ToString(),
            provenance.CorrelationId,
            provenance.IsColdStart,
            provenance.Attributes.Count == 0
                ? null
                : new Dictionary<string, string?>(provenance.Attributes, StringComparer.Ordinal));
    }

    public static NavigationRequestProvenance? Restore(NavigationRequestProvenanceSnapshot? snapshot)
    {
        if (snapshot is null)
            return null;

        return new NavigationRequestProvenance(
            snapshot.Provider,
            RestoreUri(snapshot.OriginalUri, nameof(snapshot.OriginalUri)),
            RestoreUri(snapshot.ReferrerUri, nameof(snapshot.ReferrerUri)),
            snapshot.CorrelationId,
            snapshot.IsColdStart,
            snapshot.Attributes is { Count: > 0 }
                ? new Dictionary<string, string?>(snapshot.Attributes, StringComparer.Ordinal)
                : null);
    }

    private static Uri? RestoreUri(string? value, string fieldName)
    {
        if (value is null)
            return null;

        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out Uri? uri))
            throw new FormatException($"Persisted provenance field '{fieldName}' is not a valid URI.");

        return uri;
    }
}

using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Persistence;

internal static class NavigationRequestProvenanceSnapshotMapper
{
    public static NavigationRequestProvenanceSnapshot? Create(NavigationRequestProvenance? provenance)
    {
        if (provenance is null)
        {
            return null;
        }

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
        {
            return null;
        }

        return new NavigationRequestProvenance(
            snapshot.Provider,
            RestoreUri(snapshot.OriginalUri),
            RestoreUri(snapshot.ReferrerUri),
            snapshot.CorrelationId,
            snapshot.IsColdStart,
            snapshot.Attributes is { Count: > 0 }
                ? new Dictionary<string, string?>(snapshot.Attributes, StringComparer.Ordinal)
                : null);
    }

    private static Uri? RestoreUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri)
            ? uri
            : null;
    }
}

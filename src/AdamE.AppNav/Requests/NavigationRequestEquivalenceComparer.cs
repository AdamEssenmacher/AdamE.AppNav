namespace AdamE.AppNav.Requests;

internal sealed class NavigationRequestEquivalenceComparer : IEqualityComparer<RouterNavigationRequest>
{
    public static NavigationRequestEquivalenceComparer Instance { get; } = new();

    public bool Equals(RouterNavigationRequest? x, RouterNavigationRequest? y)
    {
        if (ReferenceEquals(x, y))
            return true;

        if (x is null || y is null)
            return false;

        if (!Equals(x.Uri, y.Uri) ||
            !Equals(x.Route, y.Route) ||
            x.Source != y.Source ||
            !StringComparer.Ordinal.Equals(x.WindowId, y.WindowId) ||
            x.Disposition != y.Disposition ||
            !Equals(x.Provenance, y.Provenance))
            return false;

        return MetadataEquals(x.Metadata, y.Metadata);
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

        foreach (KeyValuePair<string, object?> pair in obj.Metadata.OrderBy(static pair => pair.Key,
                     StringComparer.Ordinal))
        {
            hash.Add(pair.Key, StringComparer.Ordinal);
            hash.Add(pair.Value?.GetType());
            hash.Add(pair.Value);
        }

        return hash.ToHashCode();
    }

    private static bool MetadataEquals(
        IReadOnlyDictionary<string, object?> x,
        IReadOnlyDictionary<string, object?> y)
    {
        if (ReferenceEquals(x, y))
            return true;

        if (x.Count != y.Count)
            return false;

        foreach (KeyValuePair<string, object?> pair in x)
        {
            if (!y.TryGetValue(pair.Key, out object? otherValue))
                return false;

            if (!Equals(pair.Value, otherValue))
                return false;
        }

        return true;
    }
}

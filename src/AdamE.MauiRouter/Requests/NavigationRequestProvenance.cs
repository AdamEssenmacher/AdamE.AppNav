using System.Collections.ObjectModel;

namespace AdamE.MauiRouter.Requests;

/// <summary>
/// Runtime context that describes how a navigation request entered the router.
/// </summary>
/// <remarks>
/// Provenance is not route identity, route metadata, or route formatting state. MauiRouter sets it automatically only
/// for built-in MAUI app-link ingress; app-owned external sources such as Branch, push, QR, or SDK bridges should set
/// their own provider-specific values before dispatching the request.
/// </remarks>
public sealed record NavigationRequestProvenance
{
    private static readonly IReadOnlyDictionary<string, string?> EmptyAttributes =
        new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(StringComparer.Ordinal));

    public NavigationRequestProvenance(
        string? provider = null,
        Uri? originalUri = null,
        Uri? referrerUri = null,
        string? correlationId = null,
        bool? isColdStart = null,
        IReadOnlyDictionary<string, string?>? attributes = null)
    {
        Provider = provider;
        OriginalUri = originalUri;
        ReferrerUri = referrerUri;
        CorrelationId = correlationId;
        IsColdStart = isColdStart;
        Attributes = attributes ?? EmptyAttributes;
    }

    /// <summary>
    /// Concrete provider that delivered the request, such as a built-in MAUI app-link provider or an app-owned SDK.
    /// </summary>
    /// <remarks>
    /// MauiRouter sets this for built-in MAUI app-link ingress. App-owned external sources should set their own
    /// stable provider names, for example <c>branch</c>, <c>firebase-push</c>, or <c>qr-scanner</c>.
    /// </remarks>
    public string? Provider { get; }

    /// <summary>
    /// Original URI supplied by the ingress provider.
    /// </summary>
    /// <remarks>
    /// MauiRouter sets this to the incoming platform app-link URI for built-in MAUI app-link ingress. App-owned
    /// sources should set it when the source has an original URI or SDK-resolved URI.
    /// </remarks>
    public Uri? OriginalUri { get; init; }

    /// <summary>
    /// Referrer URI supplied by the ingress provider, when reliable.
    /// </summary>
    /// <remarks>
    /// MauiRouter does not infer this value. App-owned sources should set it only when the provider supplies reliable
    /// referrer context.
    /// </remarks>
    public Uri? ReferrerUri { get; }

    /// <summary>
    /// Stable request correlation id supplied by the ingress provider or app boundary.
    /// </summary>
    /// <remarks>
    /// MauiRouter does not infer this value. App-owned sources should set notification ids, Branch click ids,
    /// QR scan ids, deferred navigation ids, or similar correlation values when available.
    /// </remarks>
    public string? CorrelationId { get; }

    /// <summary>
    /// Indicates whether the request entered during a cold start when known.
    /// </summary>
    /// <remarks>
    /// MauiRouter does not guess this value. App-owned sources should set it only when the app boundary can determine
    /// cold-start state without inference.
    /// </remarks>
    public bool? IsColdStart { get; }

    /// <summary>
    /// Provider-specific string attributes associated with the request.
    /// </summary>
    /// <remarks>
    /// MauiRouter leaves this empty for built-in app-link ingress. App-owned sources may use it for stable
    /// provider-specific context, but not for route state or secrets.
    /// </remarks>
    public IReadOnlyDictionary<string, string?> Attributes
    {
        get;
        // ReSharper disable once MemberCanBePrivate.Global
        init => field = SnapshotAttributes(value);
    } = EmptyAttributes;

    public bool Equals(NavigationRequestProvenance? other)
    {
        if (ReferenceEquals(this, other))
            return true;

        return other is not null &&
               StringComparer.Ordinal.Equals(Provider, other.Provider) &&
               Equals(OriginalUri, other.OriginalUri) &&
               Equals(ReferrerUri, other.ReferrerUri) &&
               StringComparer.Ordinal.Equals(CorrelationId, other.CorrelationId) &&
               IsColdStart == other.IsColdStart &&
               AttributesEqual(Attributes, other.Attributes);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Provider, StringComparer.Ordinal);
        hash.Add(OriginalUri);
        hash.Add(ReferrerUri);
        hash.Add(CorrelationId, StringComparer.Ordinal);
        hash.Add(IsColdStart);

        foreach (KeyValuePair<string, string?> pair in Attributes.OrderBy(static pair => pair.Key,
                     StringComparer.Ordinal))
        {
            hash.Add(pair.Key, StringComparer.Ordinal);
            hash.Add(pair.Value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private static IReadOnlyDictionary<string, string?> SnapshotAttributes(
        IReadOnlyDictionary<string, string?>? attributes)
    {
        if (attributes is null || attributes.Count == 0)
            return EmptyAttributes;

        return new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(attributes, StringComparer.Ordinal));
    }

    private static bool AttributesEqual(
        IReadOnlyDictionary<string, string?> x,
        IReadOnlyDictionary<string, string?> y)
    {
        if (ReferenceEquals(x, y))
            return true;

        if (x.Count != y.Count)
            return false;

        foreach (KeyValuePair<string, string?> pair in x)
            if (!y.TryGetValue(pair.Key, out string? otherValue) ||
                !StringComparer.Ordinal.Equals(pair.Value, otherValue))
                return false;

        return true;
    }
}

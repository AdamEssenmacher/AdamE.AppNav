using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Policies;

public sealed class AllowedUriOriginPolicy : INavigationRequestPolicy
{
    private static readonly HashSet<NavigationRequestSource> DefaultExternalSources = new()
    {
        NavigationRequestSource.AppLink,
        NavigationRequestSource.Push,
        NavigationRequestSource.QrCode
    };

    private readonly IReadOnlyList<Uri> _allowedOrigins;
    private readonly IReadOnlySet<NavigationRequestSource> _sources;

    public AllowedUriOriginPolicy(
        IEnumerable<Uri> allowedOrigins,
        IEnumerable<NavigationRequestSource>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);

        _allowedOrigins = allowedOrigins
            .Select(NormalizeOrigin)
            .ToArray();
        _sources = (sources ?? DefaultExternalSources).ToHashSet();
    }

    public ValueTask<RouterNavigationRequest> ApplyAsync(
        NavigationRequestPolicyContext context,
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Uri is null || !_sources.Contains(request.Source))
        {
            return ValueTask.FromResult(request);
        }

        if (_allowedOrigins.Count == 0)
        {
            throw new InvalidOperationException("External URI navigation is not allowed because no trusted origins are configured.");
        }

        var requestOrigin = NormalizeOrigin(request.Uri);
        var allowed = _allowedOrigins.Any(origin =>
            StringComparer.OrdinalIgnoreCase.Equals(origin.Scheme, requestOrigin.Scheme) &&
            StringComparer.OrdinalIgnoreCase.Equals(origin.Host, requestOrigin.Host) &&
            origin.Port == requestOrigin.Port);

        if (!allowed)
        {
            throw new InvalidOperationException($"External URI origin '{requestOrigin}' is not trusted.");
        }

        return ValueTask.FromResult(request);
    }

    private static Uri NormalizeOrigin(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Trusted URI origins must be absolute.");
        }

        return new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri;
    }
}

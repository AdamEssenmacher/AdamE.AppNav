namespace AdamE.AppNav.Requests;

public sealed class DeferredNavigationRequestPersistenceOptions
{
    private Uri? _baseUri;

    /// <summary>
    /// Gets the explicit absolute base URI used to format canonical persisted route URIs.
    /// </summary>
    /// <exception cref="InvalidOperationException">No base URI has been configured.</exception>
    public Uri BaseUri
    {
        get => _baseUri ?? throw new InvalidOperationException(
            $"{nameof(BaseUri)} must be configured explicitly for deferred navigation persistence.");
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!value.IsAbsoluteUri)
                throw new ArgumentException("The deferred navigation persistence base URI must be absolute.", nameof(value));
            if (string.IsNullOrWhiteSpace(value.Scheme) || string.IsNullOrWhiteSpace(value.Host))
                throw new ArgumentException(
                    "The deferred navigation persistence base URI must include a scheme and host.",
                    nameof(value));
            if (HasCredentialSyntax(value))
                throw new ArgumentException(
                    "The deferred navigation persistence base URI cannot contain credentials.",
                    nameof(value));
            if (value.AbsolutePath is not ("" or "/") ||
                HasQueryOrFragmentSyntax(value))
            {
                throw new ArgumentException(
                    "The deferred navigation persistence base URI must be a root origin without a query or fragment.",
                    nameof(value));
            }

            _baseUri = value;
        }
    }

    private static bool HasCredentialSyntax(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return true;

        string original = uri.OriginalString;
        int schemeSeparator = original.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator < 0)
            return false;

        int authorityStart = schemeSeparator + 3;
        int authorityEnd = original.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
            authorityEnd = original.Length;

        return original.AsSpan(authorityStart, authorityEnd - authorityStart).Contains('@');
    }

    private static bool HasQueryOrFragmentSyntax(Uri uri)
    {
        return uri.OriginalString.Contains('?', StringComparison.Ordinal) ||
               uri.OriginalString.Contains('#', StringComparison.Ordinal);
    }

    public INavigationRequestMetadataSerializer? MetadataSerializer { get; init; }

    public RouteStateRegistry? RouteStateRegistry { get; init; }
}

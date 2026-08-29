using System.Collections.ObjectModel;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Policies;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Maui.AppLinks;

/// <summary>
/// Configures the security and delivery policy for navigation requests entering through a MAUI host boundary.
/// </summary>
public sealed class MauiExternalNavigationOptions
{
    private readonly List<Uri> _allowedOrigins = [];
    private readonly List<NormalizedOrigin> _normalizedOrigins = [];
    private readonly ReadOnlyCollection<Uri> _readOnlyAllowedOrigins;
    private Func<RouterNavigationRequest, bool> _shouldDispatch = static _ => true;
    private Func<Exception, MauiExternalNavigationFailureDisposition> _classifyFailure =
        DefaultFailureClassifier;

    public MauiExternalNavigationOptions()
    {
        _readOnlyAllowedOrigins = _allowedOrigins.AsReadOnly();
    }

    /// <summary>
    /// Gets the configured URI origins that may enter the router through this boundary.
    /// </summary>
    public IReadOnlyList<Uri> AllowedOrigins => _readOnlyAllowedOrigins;

    /// <summary>
    /// Gets or sets the largest accepted transport URI, in characters.
    /// </summary>
    public int MaximumUriLength { get; set; } = 2048;

    /// <summary>
    /// Gets or sets the maximum number of requests retained in the pending queue.
    /// An actively executing request does not consume a pending-queue slot.
    /// </summary>
    public int MaximumPendingRequests { get; set; } = 32;

    /// <summary>
    /// Gets or sets the maximum number of dispatch attempts for a retryable request.
    /// </summary>
    public int MaximumDispatchAttempts { get; set; } = 3;

    /// <summary>
    /// Gets or sets the minimum delay before a retryable request is attempted again.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets or sets the maximum age of a request retained by this boundary.
    /// </summary>
    public TimeSpan MaximumRequestAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets an application filter evaluated only after built-in URI security validation succeeds.
    /// </summary>
    public Func<RouterNavigationRequest, bool> ShouldDispatch
    {
        get => _shouldDispatch;
        set => _shouldDispatch = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Gets or sets the classifier used after a dispatch attempt fails.
    /// </summary>
    public Func<Exception, MauiExternalNavigationFailureDisposition> ClassifyFailure
    {
        get => _classifyFailure;
        set => _classifyFailure = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Allows one absolute root origin. Paths, credentials, queries, and fragments are not valid origin values.
    /// </summary>
    public MauiExternalNavigationOptions AllowOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);

        NormalizedOrigin normalized = NormalizeConfiguredOrigin(origin);
        if (_normalizedOrigins.Contains(normalized))
            return this;

        _allowedOrigins.Add(origin);
        _normalizedOrigins.Add(normalized);
        return this;
    }

    internal void ValidateForEnablement()
    {
        ValidateLimits();
        if (_normalizedOrigins.Count == 0)
        {
            throw new InvalidOperationException(
                "External URI handling requires at least one trusted origin. Call AllowOrigin before enabling it.");
        }
    }

    internal void ValidateLimits()
    {
        if (MaximumUriLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumUriLength), "MaximumUriLength must be positive.");
        if (MaximumPendingRequests <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaximumPendingRequests),
                "MaximumPendingRequests must be positive.");
        if (MaximumDispatchAttempts <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MaximumDispatchAttempts),
                "MaximumDispatchAttempts must be positive.");
        if (RetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), "RetryDelay cannot be negative.");
        if (MaximumRequestAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaximumRequestAge), "MaximumRequestAge must be positive.");
    }

    internal bool TryAccept(
        RouterNavigationRequest request,
        DateTimeOffset now,
        out MauiExternalNavigationRejectionReason rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLimits();

        if (request.Timestamp > now)
        {
            rejectionReason = MauiExternalNavigationRejectionReason.FutureTimestamp;
            return false;
        }

        if (now - request.Timestamp > MaximumRequestAge)
        {
            rejectionReason = MauiExternalNavigationRejectionReason.Expired;
            return false;
        }

        if (request.Uri is not null && !TryValidateUri(request.Uri, out rejectionReason))
            return false;

        if (!ShouldDispatch(request))
        {
            rejectionReason = MauiExternalNavigationRejectionReason.ApplicationFilter;
            return false;
        }

        rejectionReason = MauiExternalNavigationRejectionReason.None;
        return true;
    }

    internal MauiExternalNavigationFailureDisposition Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        try
        {
            return ClassifyFailure(exception);
        }
        catch
        {
            // A broken application classifier cannot turn a failed request into an unbounded retry loop.
            return MauiExternalNavigationFailureDisposition.Drop;
        }
    }

    private bool TryValidateUri(Uri uri, out MauiExternalNavigationRejectionReason rejectionReason)
    {
        if (!uri.IsAbsoluteUri)
        {
            rejectionReason = MauiExternalNavigationRejectionReason.RelativeUri;
            return false;
        }

        if (uri.OriginalString.Length > MaximumUriLength)
        {
            rejectionReason = MauiExternalNavigationRejectionReason.UriTooLong;
            return false;
        }

        if (HasCredentialSyntax(uri))
        {
            rejectionReason = MauiExternalNavigationRejectionReason.Credentials;
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Scheme) || string.IsNullOrWhiteSpace(uri.Host))
        {
            rejectionReason = MauiExternalNavigationRejectionReason.InvalidOrigin;
            return false;
        }

        var candidate = NormalizedOrigin.FromUri(uri);
        if (!_normalizedOrigins.Contains(candidate))
        {
            rejectionReason = _normalizedOrigins.Any(origin => origin.HasSameSchemeAndHost(candidate))
                ? MauiExternalNavigationRejectionReason.PortNotAllowed
                : MauiExternalNavigationRejectionReason.OriginNotAllowed;
            return false;
        }

        rejectionReason = MauiExternalNavigationRejectionReason.None;
        return true;
    }

    private static NormalizedOrigin NormalizeConfiguredOrigin(Uri origin)
    {
        if (!origin.IsAbsoluteUri)
            throw new ArgumentException("An allowed origin must be absolute.", nameof(origin));
        if (string.IsNullOrWhiteSpace(origin.Scheme) || string.IsNullOrWhiteSpace(origin.Host))
            throw new ArgumentException("An allowed origin must include a scheme and host.", nameof(origin));
        if (HasCredentialSyntax(origin))
            throw new ArgumentException("An allowed origin cannot contain credentials.", nameof(origin));
        if (HasQueryOrFragmentSyntax(origin))
            throw new ArgumentException("An allowed origin cannot contain a query or fragment.", nameof(origin));
        if (origin.AbsolutePath is not ("" or "/"))
            throw new ArgumentException("An allowed origin must not contain a path.", nameof(origin));

        return NormalizedOrigin.FromUri(origin);
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
        string original = uri.OriginalString;
        return original.Contains('?', StringComparison.Ordinal) ||
               original.Contains('#', StringComparison.Ordinal);
    }

    private static MauiExternalNavigationFailureDisposition DefaultFailureClassifier(Exception exception)
    {
        Exception candidate = exception is AggregateException { InnerExceptions.Count: 1 } aggregate
            ? aggregate.InnerExceptions[0]
            : exception;

        return candidate is RouteNotMatchedException or
            RouteRedirectLoopException or
            RoutePlannerNotFoundException or
            AppNavigationConfigurationException or
            ArgumentException or
            FormatException or
            NotSupportedException or
            MauiPresentationConsistencyException
                ? MauiExternalNavigationFailureDisposition.Drop
                : MauiExternalNavigationFailureDisposition.Retry;
    }

    private readonly record struct NormalizedOrigin(string Scheme, string IdnHost, int EffectivePort)
    {
        public static NormalizedOrigin FromUri(Uri uri)
        {
            return new NormalizedOrigin(
                uri.Scheme.ToLowerInvariant(),
                uri.IdnHost.TrimEnd('.').ToLowerInvariant(),
                uri.Port);
        }

        public bool HasSameSchemeAndHost(NormalizedOrigin other)
        {
            return StringComparer.Ordinal.Equals(Scheme, other.Scheme) &&
                   StringComparer.Ordinal.Equals(IdnHost, other.IdnHost);
        }
    }
}

/// <summary>
/// Describes what the MAUI external-navigation boundary should do after a failed dispatch attempt.
/// </summary>
public enum MauiExternalNavigationFailureDisposition
{
    Retry,
    Drop
}

internal enum MauiExternalNavigationRejectionReason
{
    None,
    RelativeUri,
    InvalidUri,
    UriTooLong,
    Credentials,
    InvalidOrigin,
    OriginNotAllowed,
    PortNotAllowed,
    ApplicationFilter,
    Expired,
    FutureTimestamp
}

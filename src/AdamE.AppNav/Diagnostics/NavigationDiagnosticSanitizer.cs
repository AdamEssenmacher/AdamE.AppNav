using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Diagnostics;

internal static class NavigationDiagnosticSanitizer
{
    private static readonly HashSet<string> AllowedDataKeys =
    [
        NavigationDiagnosticDataKeys.Count,
        NavigationDiagnosticDataKeys.Reason,
        NavigationDiagnosticDataKeys.SchemaVersion,
        NavigationDiagnosticDataKeys.DurationMs,
        NavigationDiagnosticDataKeys.ExceptionType,
        NavigationDiagnosticDataKeys.Uri,
        NavigationDiagnosticDataKeys.RequestSource,
        NavigationDiagnosticDataKeys.BackSource,
        NavigationDiagnosticDataKeys.RequestDisposition,
        NavigationDiagnosticDataKeys.RouteType,
        NavigationDiagnosticDataKeys.RouteTemplate,
        NavigationDiagnosticDataKeys.RouteDiagnosticCode,
        NavigationDiagnosticDataKeys.CandidateCount,
        NavigationDiagnosticDataKeys.PlanKind,
        NavigationDiagnosticDataKeys.ContextualFallback,
        NavigationDiagnosticDataKeys.PolicyType,
        NavigationDiagnosticDataKeys.Decision,
        NavigationDiagnosticDataKeys.RequestTransformerType,
        NavigationDiagnosticDataKeys.RedirectCount,
        NavigationDiagnosticDataKeys.RedirectFrom,
        NavigationDiagnosticDataKeys.RedirectTo,
        NavigationDiagnosticDataKeys.RedirectTrace,
        NavigationDiagnosticDataKeys.ReconciliationSource,
        NavigationDiagnosticDataKeys.OriginalKind,
        NavigationDiagnosticDataKeys.PageType,
        NavigationDiagnosticDataKeys.HandlerName,
        NavigationDiagnosticDataKeys.Platform,
        NavigationDiagnosticDataKeys.StartupOutcome,
        NavigationDiagnosticDataKeys.StartupDeferredRequestPending,
        NavigationDiagnosticDataKeys.AppLinkGraceMs,
        NavigationDiagnosticDataKeys.ExternalNavigationReason,
        NavigationDiagnosticDataKeys.DispatchAttempt,
        NavigationDiagnosticDataKeys.MaximumDispatchAttempts,
        NavigationDiagnosticDataKeys.PendingRequestCount,
        NavigationDiagnosticDataKeys.RetryDelayMs
    ];

    public static NavigationDiagnosticEvent Sanitize(NavigationDiagnosticEvent diagnosticEvent)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> pair in diagnosticEvent.Data)
        {
            if (!AllowedDataKeys.Contains(pair.Key) ||
                pair.Key is NavigationDiagnosticDataKeys.ExceptionMessage or
                    NavigationDiagnosticDataKeys.RouteDiagnosticMessage or
                    NavigationDiagnosticDataKeys.ProvenanceCorrelationId)
            {
                continue;
            }

            object? value = pair.Key switch
            {
                NavigationDiagnosticDataKeys.Uri => SanitizeUri(pair.Value),
                NavigationDiagnosticDataKeys.RedirectFrom or NavigationDiagnosticDataKeys.RedirectTo =>
                    SanitizeRedirectTarget(pair.Value?.ToString()),
                NavigationDiagnosticDataKeys.RedirectTrace => SanitizeRedirectTrace(pair.Value?.ToString()),
                _ => pair.Value
            };

            if (value is not null)
                data[pair.Key] = value;
        }

        return diagnosticEvent with
        {
            Message = $"Navigation diagnostic: {diagnosticEvent.Kind}.",
            Data = data
        };
    }

    private static object? SanitizeUri(object? value)
    {
        string? text = value switch
        {
            null => null,
            Uri uriValue => uriValue.ToString(),
            _ => value.ToString()
        };

        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (!Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out Uri? uri))
            return "<invalid-uri>";

        if (!uri.IsAbsoluteUri)
            return "<relative-uri>";

        if (string.IsNullOrEmpty(uri.Host))
            return $"{uri.Scheme}:<absolute-uri>";

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer,
            UriFormat.UriEscaped);
    }

    private static string? SanitizeRedirectTrace(string? value)
    {
        return value is null
            ? null
            : string.Join(" -> ", value.Split(" -> ", StringSplitOptions.None)
                .Select(SanitizeRedirectTarget));
    }

    private static string? SanitizeRedirectTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        int envelopeStart = value.LastIndexOf(" [", StringComparison.Ordinal);
        string target = envelopeStart < 0 ? value : value[..envelopeStart];
        string envelope = envelopeStart < 0 ? string.Empty : value[envelopeStart..];

        if (target.StartsWith("uri=", StringComparison.Ordinal))
            return $"uri={SanitizeUri(target[4..])}{SanitizeEnvelope(envelope)}";

        if (target.StartsWith("route=", StringComparison.Ordinal))
        {
            string route = target[6..];
            int valueSeparator = route.IndexOf(':');
            if (valueSeparator >= 0)
                route = route[..valueSeparator];

            return $"route={route}{SanitizeEnvelope(envelope)}";
        }

        return "target=redacted";
    }

    private static string SanitizeEnvelope(string envelope)
    {
        if (string.IsNullOrEmpty(envelope) ||
            !envelope.StartsWith(" [", StringComparison.Ordinal) ||
            !envelope.EndsWith(']'))
            return string.Empty;

        ReadOnlySpan<char> contents = envelope.AsSpan(2, envelope.Length - 3);
        const string dispositionMarker = ", disposition=";
        int dispositionStart = contents.IndexOf(dispositionMarker, StringComparison.Ordinal);
        if (dispositionStart <= 0)
            return string.Empty;

        ReadOnlySpan<char> sourceText = contents[..dispositionStart];
        ReadOnlySpan<char> dispositionAndWindow = contents[(dispositionStart + dispositionMarker.Length)..];
        int windowStart = dispositionAndWindow.IndexOf(", window=", StringComparison.Ordinal);
        ReadOnlySpan<char> dispositionText = windowStart < 0
            ? dispositionAndWindow
            : dispositionAndWindow[..windowStart];

        if (!TryParseDefinedEnum(sourceText, out NavigationRequestSource source) ||
            !TryParseDefinedEnum(dispositionText, out RouterNavigationDisposition disposition))
        {
            return string.Empty;
        }

        return $" [{source}, disposition={disposition}]";
    }

    private static bool TryParseDefinedEnum<TEnum>(
        ReadOnlySpan<char> text,
        out TEnum value)
        where TEnum : struct, Enum
    {
        return Enum.TryParse(text, ignoreCase: false, out value) &&
               Enum.IsDefined(value) &&
               text.Equals(value.ToString(), StringComparison.Ordinal);
    }
}

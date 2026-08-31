using System.Diagnostics;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Requests;
using AdamE.AppNav.Routing;

namespace AdamE.AppNav.Navigation;

internal sealed class RouterNavigationDiagnostics(
    NavigationDiagnostics diagnostics,
    ActivitySource activitySource)
{
    private readonly NavigationDiagnostics _diagnostics =
        diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

    private readonly ActivitySource _activitySource =
        activitySource ?? throw new ArgumentNullException(nameof(activitySource));

    public void Write(
        NavigationDiagnosticEventKind kind,
        string operationId,
        string message,
        IReadOnlyDictionary<string, object?>? data = null,
        NavigationDiagnosticPhase? phase = null)
    {
        _diagnostics.Write(kind, operationId, message, data, phase: phase);
    }

    public RouterNavigationActivityScope StartActivity(
        string name,
        string operationId,
        RouterNavigationRequest request)
    {
        return StartActivity(name, operationId, request.Source.ToString(), request.Disposition);
    }

    public RouterNavigationActivityScope StartActivity(
        string name,
        string operationId,
        string source,
        RouterNavigationDisposition disposition = RouterNavigationDisposition.Auto)
    {
        Activity? previousActivity = Activity.Current;
        try
        {
            Activity? activity = _activitySource.StartActivity(name);
            var scope = new RouterNavigationActivityScope(activity, previousActivity);
            scope.SetTag("navigation.operation_id", operationId);
            scope.SetTag("navigation.source", source);
            scope.SetTag("navigation.disposition", disposition);
            return scope;
        }
        catch
        {
            Activity? failedActivity = Activity.Current;
            if (failedActivity is not null &&
                !ReferenceEquals(failedActivity, previousActivity) &&
                ReferenceEquals(failedActivity.Source, _activitySource) &&
                string.Equals(failedActivity.OperationName, name, StringComparison.Ordinal))
            {
                RouterNavigationActivityScope.TryDisposeAndRestore(
                    failedActivity,
                    previousActivity);
            }

            return new RouterNavigationActivityScope(null, previousActivity);
        }
    }

    public void WriteFailure(
        NavigationDiagnosticEventKind kind,
        string operationId,
        string message,
        Exception exception,
        Stopwatch timer,
        params (string Key, object? Value)[] data)
    {
        RouterNavigationActivityScope.TrySetCurrentTag(
            "navigation.failure.type",
            exception.GetType().FullName);
        _diagnostics.Write(kind, operationId, message, FailureData(exception, timer, data));
    }

    public static IReadOnlyDictionary<string, object?> Duration(
        Stopwatch timer,
        params (string Key, object? Value)[] data)
    {
        timer.Stop();
        Dictionary<string, object?> result = Data(data);
        result[NavigationDiagnosticDataKeys.DurationMs] = timer.Elapsed.TotalMilliseconds;
        return result;
    }

    public static IReadOnlyDictionary<string, object?> Duration(
        Stopwatch timer,
        RouterNavigationRequest request,
        params (string Key, object? Value)[] data)
    {
        timer.Stop();
        Dictionary<string, object?> result = RequestData(request, data);
        result[NavigationDiagnosticDataKeys.DurationMs] = timer.Elapsed.TotalMilliseconds;
        return result;
    }

    public static IReadOnlyDictionary<string, object?> RouteFailureData(
        Stopwatch timer,
        RouterNavigationRequest request,
        RouteDiagnostic? diagnostic)
    {
        var data = new List<(string Key, object? Value)>
        {
            (NavigationDiagnosticDataKeys.Uri, request.Uri?.ToString())
        };

        if (diagnostic is null)
            return Duration(timer, request, data.ToArray());

        data.Add((NavigationDiagnosticDataKeys.RouteDiagnosticCode, diagnostic.Code));
        data.Add((NavigationDiagnosticDataKeys.RouteDiagnosticMessage, diagnostic.Message));

        foreach ((string key, object? value) in diagnostic.Data)
        {
            string normalizedKey = key switch
            {
                "path" => NavigationDiagnosticDataKeys.Path,
                "template" => NavigationDiagnosticDataKeys.RouteTemplate,
                "routeType" => NavigationDiagnosticDataKeys.RouteType,
                "candidateCount" => NavigationDiagnosticDataKeys.CandidateCount,
                _ => key
            };

            data.Add((normalizedKey, value));
        }

        return Duration(timer, request, data.ToArray());
    }

    public void WriteRedirectLoopDetected(
        string operationId,
        string componentTypeKey,
        string? componentType,
        RouterNavigationRequest redirectFrom,
        RouterNavigationRequest redirectTo,
        int redirectCount,
        string redirectTrace,
        string message,
        NavigationDiagnosticPhase phase)
    {
        RouterNavigationActivityScope.TrySetCurrentTag("navigation.redirect_count", redirectCount);
        _diagnostics.Write(
            NavigationDiagnosticEventKind.RequestRedirectLoopDetected,
            operationId,
            message,
            Data(
                (componentTypeKey, componentType),
                (NavigationDiagnosticDataKeys.RedirectCount, redirectCount),
                (NavigationDiagnosticDataKeys.RedirectFrom, DescribeRedirectTarget(redirectFrom)),
                (NavigationDiagnosticDataKeys.RedirectTo, DescribeRedirectTarget(redirectTo)),
                (NavigationDiagnosticDataKeys.RedirectTrace, redirectTrace)),
            phase: phase);
    }

    public static string BuildRedirectTrace(
        RouterNavigationRequest initialRequest,
        IReadOnlyList<RouterNavigationRequest> redirects)
    {
        return string.Join(
            " -> ",
            new[] { DescribeRedirectTarget(initialRequest) }
                .Concat(redirects.Select(DescribeRedirectTarget)));
    }

    public static string DescribeRedirectTarget(RouterNavigationRequest request)
    {
        var parts = new List<string>();
        if (request.Uri is not null)
            parts.Add($"uri={request.Uri}");

        if (request.Route is not null)
            parts.Add($"route={request.Route.GetType().Name}:{request.Route}");

        string target = parts.Count == 0
            ? "<none>"
            : string.Join(", ", parts);

        return request.WindowId is null
            ? $"{target} [{request.Source}, disposition={request.Disposition}]"
            : $"{target} [{request.Source}, disposition={request.Disposition}, window={request.WindowId}]";
    }

    public static Dictionary<string, object?> RequestData(
        RouterNavigationRequest request,
        params (string Key, object? Value)[] values)
    {
        Dictionary<string, object?> result = Data(values);
        AddProvenanceData(result, request.Provenance);

        return result;
    }

    public static Dictionary<string, object?> Data(params (string Key, object? Value)[] values)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string key, object? value) in values)
            result[key] = value;

        return result;
    }

    private static Dictionary<string, object?> FailureData(
        Exception exception,
        Stopwatch timer,
        params (string Key, object? Value)[] data)
    {
        timer.Stop();
        Dictionary<string, object?> result = Data(data);
        result[NavigationDiagnosticDataKeys.DurationMs] = timer.Elapsed.TotalMilliseconds;
        result[NavigationDiagnosticDataKeys.ExceptionType] = exception.GetType().FullName;
        result[NavigationDiagnosticDataKeys.ExceptionMessage] = exception.Message;

        return result;
    }

    private static void AddProvenanceData(
        Dictionary<string, object?> data,
        NavigationRequestProvenance? provenance)
    {
        if (provenance is null)
            return;

        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceProvider, provenance.Provider);
        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceOriginalUri, provenance.OriginalUri?.ToString());
        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceReferrerUri, provenance.ReferrerUri?.ToString());
        AddIfPresent(data, NavigationDiagnosticDataKeys.ProvenanceCorrelationId, provenance.CorrelationId);
        if (provenance.IsColdStart.HasValue)
            data[NavigationDiagnosticDataKeys.ProvenanceIsColdStart] = provenance.IsColdStart.Value;

        if (provenance.Attributes.Count > 0)
            data[NavigationDiagnosticDataKeys.ProvenanceAttributes] =
                new Dictionary<string, string?>(provenance.Attributes, StringComparer.Ordinal);
    }

    private static void AddIfPresent(
        Dictionary<string, object?> data,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            data[key] = value;
    }
}

internal readonly struct RouterNavigationActivityScope(
    Activity? activity,
    Activity? previousActivity) : IDisposable
{
    private readonly Activity? _activity = activity;
    private readonly Activity? _previousActivity = previousActivity;

    public void SetTag(string name, object? value)
    {
        try
        {
            _activity?.SetTag(name, value);
        }
        catch
        {
            // Tracing providers are external observers and cannot participate in navigation control flow.
        }
    }

    public void SetTag<T>(string name, T value)
        where T : struct, Enum
    {
        if (_activity is null)
            return;

        try
        {
            _activity.SetTag(name, value.ToString());
        }
        catch
        {
            // Tracing providers are external observers and cannot participate in navigation control flow.
        }
    }

    public void SetStatus(ActivityStatusCode status)
    {
        try
        {
            _activity?.SetStatus(status);
        }
        catch
        {
            // Tracing providers are external observers and cannot participate in navigation control flow.
        }
    }

    public void Dispose()
    {
        if (_activity is null)
            return;

        TryDisposeAndRestore(_activity, _previousActivity);
    }

    internal static void TrySetCurrentTag(string name, object? value)
    {
        try
        {
            Activity.Current?.SetTag(name, value);
        }
        catch
        {
            // Tracing providers are external observers and cannot participate in navigation control flow.
        }
    }

    internal static void TrySetCurrentTag<T>(string name, T value)
        where T : struct
    {
        Activity? activity = Activity.Current;
        if (activity is null)
            return;

        try
        {
            activity.SetTag(name, value);
        }
        catch
        {
            // Tracing providers are external observers and cannot participate in navigation control flow.
        }
    }

    internal static void TryDisposeAndRestore(Activity activity, Activity? previousActivity)
    {
        try
        {
            activity.Dispose();
        }
        catch
        {
            // ActivityListener stop callbacks are external observers and cannot fail navigation.
        }
        finally
        {
            if (ReferenceEquals(Activity.Current, activity))
            {
                try
                {
                    Activity.Current = previousActivity;
                }
                catch
                {
                    // Restoring ambient tracing state is best effort when external callbacks also fail.
                }
            }
        }
    }
}

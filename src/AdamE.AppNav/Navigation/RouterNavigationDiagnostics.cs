using System.Diagnostics;
using AdamE.AppNav.Diagnostics;
using AdamE.AppNav.Plans;
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

    public void WriteLazy<TState>(
        NavigationDiagnosticEventKind kind,
        string operationId,
        TState state,
        Func<TState, (string Message, IReadOnlyDictionary<string, object?>? Data)?> createPayload,
        NavigationDiagnosticPhase? phase = null)
    {
        _diagnostics.WriteLazy(kind, operationId, state, createPayload, phase: phase);
    }

    public void WritePresentationStarted(string operationId, NavigationPlanKind planKind)
    {
        WriteLazy(NavigationDiagnosticEventKind.PresentationStarted, operationId, planKind, static kind =>
        {
            string name = kind.ToString();
            return (name, Data((NavigationDiagnosticDataKeys.PlanKind, name)));
        });
    }

    public void WritePresentationCompleted(string operationId, NavigationPlanKind planKind, long startedAt)
    {
        WriteLazy(NavigationDiagnosticEventKind.PresentationCompleted, operationId, (planKind, startedAt), static state =>
        {
            string name = state.planKind.ToString();
            return (name, Duration(state.startedAt, (NavigationDiagnosticDataKeys.PlanKind, name)));
        });
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
        long startedAt,
        (string Key, object? Value)? data = null)
    {
        RouterNavigationActivityScope.TrySetCurrentTag(
            "navigation.failure.type",
            exception.GetType().FullName);
        WriteLazy(kind, operationId, (message, exception, startedAt, data),
            static state => (state.message, FailureData(state.exception, state.startedAt, state.data)));
    }

    public static IReadOnlyDictionary<string, object?> Duration(
        long startedAt,
        params (string Key, object? Value)[] data)
    {
        double durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        Dictionary<string, object?> result = Data(data);
        result[NavigationDiagnosticDataKeys.DurationMs] = durationMs;
        return result;
    }

    public static IReadOnlyDictionary<string, object?> Duration(
        long startedAt,
        RouterNavigationRequest request,
        params (string Key, object? Value)[] data)
    {
        double durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        Dictionary<string, object?> result = RequestData(request, data);
        result[NavigationDiagnosticDataKeys.DurationMs] = durationMs;
        return result;
    }

    public static IReadOnlyDictionary<string, object?> RouteFailureData(
        long startedAt,
        RouterNavigationRequest request,
        RouteDiagnostic? diagnostic)
    {
        var data = new List<(string Key, object? Value)>
        {
            (NavigationDiagnosticDataKeys.Uri, request.Uri?.ToString())
        };

        if (diagnostic is null)
            return Duration(startedAt, request, data.ToArray());

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

        return Duration(startedAt, request, data.ToArray());
    }

    public void WriteRedirectLoopDetected(
        string operationId,
        string componentTypeKey,
        string? componentType,
        RouterNavigationRequest redirectFrom,
        RouterNavigationRequest redirectTo,
        int redirectCount,
        RouterNavigationRequest initialRequest,
        IReadOnlyList<RouterNavigationRequest> redirects,
        string message,
        NavigationDiagnosticPhase phase)
    {
        RouterNavigationActivityScope.TrySetCurrentTag("navigation.redirect_count", redirectCount);
        WriteLazy(
            NavigationDiagnosticEventKind.RequestRedirectLoopDetected,
            operationId,
            (message, componentTypeKey, componentType, redirectCount, redirectFrom, redirectTo, initialRequest,
                redirects),
            static state => (state.message, Data(
                (state.componentTypeKey, state.componentType),
                (NavigationDiagnosticDataKeys.RedirectCount, state.redirectCount),
                (NavigationDiagnosticDataKeys.RedirectFrom, DescribeRedirectTarget(state.redirectFrom)),
                (NavigationDiagnosticDataKeys.RedirectTo, DescribeRedirectTarget(state.redirectTo)),
                (NavigationDiagnosticDataKeys.RedirectTrace, BuildRedirectTrace(state.initialRequest, state.redirects)))),
            phase);
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
        long startedAt,
        (string Key, object? Value)? data)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (data is { } field)
            result[field.Key] = field.Value;

        result[NavigationDiagnosticDataKeys.DurationMs] = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
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

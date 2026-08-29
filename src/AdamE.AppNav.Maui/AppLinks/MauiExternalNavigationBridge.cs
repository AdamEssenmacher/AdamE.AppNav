using AdamE.AppNav.Requests;
using AdamE.AppNav.Diagnostics;

namespace AdamE.AppNav.Maui.AppLinks;

internal static class MauiExternalNavigationBridge
{
    private static readonly Lock Gate = new();
    private static readonly Queue<RouterNavigationRequest> BootstrapPending = new();
    private static readonly HashSet<RouterNavigationRequest> BootstrapDeduped =
        new(MauiNavigationRequestEquivalenceComparer.Instance);
    private static readonly Queue<MauiExternalNavigationBootstrapDiagnostic> BootstrapDiagnostics = new();
    private const int MaximumBootstrapDiagnostics = 32;
    private static WeakReference<MauiExternalNavigationDispatcher>? _current;

    public static MauiExternalNavigationBridgeRegistration Register(MauiExternalNavigationDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        lock (Gate)
        {
            _current = new WeakReference<MauiExternalNavigationDispatcher>(dispatcher);

            RouterNavigationRequest[] requests = BootstrapPending.ToArray();
            MauiExternalNavigationBootstrapDiagnostic[] diagnostics = BootstrapDiagnostics.ToArray();
            BootstrapPending.Clear();
            BootstrapDeduped.Clear();
            BootstrapDiagnostics.Clear();
            return new MauiExternalNavigationBridgeRegistration(requests, diagnostics);
        }
    }

    public static void Unregister(MauiExternalNavigationDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        lock (Gate)
        {
            if (TryGetCurrent(out MauiExternalNavigationDispatcher? current) &&
                ReferenceEquals(current, dispatcher))
            {
                _current = null;
            }
        }
    }

    public static bool Submit(
        RouterNavigationRequest? request,
        MauiExternalNavigationOptions? options = null)
    {
        if (request is null)
            return false;

        while (true)
        {
            MauiExternalNavigationDispatcher? current;
            lock (Gate)
            {
                if (!TryGetCurrent(out current))
                    return TryValidateAndBuffer(request, options);
            }

            MauiExternalNavigationBridgeDispatchResult result = current!.TryDispatchFromBridge(request);
            if (result != MauiExternalNavigationBridgeDispatchResult.Unavailable)
                return result == MauiExternalNavigationBridgeDispatchResult.Accepted;

            lock (Gate)
            {
                if (TryGetCurrent(out MauiExternalNavigationDispatcher? registered) &&
                    ReferenceEquals(registered, current))
                {
                    _current = null;
                }

                if (!TryGetCurrent(out _))
                    return TryValidateAndBuffer(request, options);
            }
        }
    }

    public static bool Submit(
        MauiExternalNavigationIngress ingress,
        MauiExternalNavigationOptions? options = null)
    {
        if (ingress.Request is not null)
            return Submit(ingress.Request, options);
        if (ingress.RejectionReason == MauiExternalNavigationRejectionReason.None)
            return false;

        while (true)
        {
            MauiExternalNavigationDispatcher? current;
            lock (Gate)
            {
                if (!TryGetCurrent(out current))
                {
                    RecordBootstrapDiagnostic(
                        NavigationDiagnosticEventKind.ExternalNavigationRejected,
                        ingress.RejectionReason.ToString(),
                        BootstrapPending.Count);
                    return false;
                }
            }

            MauiExternalNavigationBridgeDispatchResult result =
                current!.RejectIngressFromBridge(ingress.RejectionReason);
            if (result != MauiExternalNavigationBridgeDispatchResult.Unavailable)
                return false;

            lock (Gate)
            {
                if (TryGetCurrent(out MauiExternalNavigationDispatcher? registered) &&
                    ReferenceEquals(registered, current))
                {
                    _current = null;
                }
            }
        }
    }

    private static bool TryValidateAndBuffer(
        RouterNavigationRequest request,
        MauiExternalNavigationOptions? options)
    {
        options ??= new MauiExternalNavigationOptions();
        try
        {
            if (!options.TryAccept(
                    request,
                    DateTimeOffset.UtcNow,
                    out MauiExternalNavigationRejectionReason rejectionReason))
            {
                RecordBootstrapDiagnostic(
                    rejectionReason == MauiExternalNavigationRejectionReason.Expired
                        ? NavigationDiagnosticEventKind.ExternalNavigationExpired
                        : NavigationDiagnosticEventKind.ExternalNavigationRejected,
                    rejectionReason.ToString(),
                    BootstrapPending.Count);
                return false;
            }
        }
        catch
        {
            RecordBootstrapDiagnostic(
                NavigationDiagnosticEventKind.ExternalNavigationRejected,
                MauiExternalNavigationRejectionReason.ApplicationFilter.ToString(),
                BootstrapPending.Count);
            return false;
        }

        return Buffer(request, options.MaximumPendingRequests);
    }

    private static bool TryGetCurrent(out MauiExternalNavigationDispatcher? dispatcher)
    {
        if (_current is not null && _current.TryGetTarget(out dispatcher))
            return true;

        _current = null;
        dispatcher = null;
        return false;
    }

    private static bool Buffer(RouterNavigationRequest request, int maximumPendingRequests)
    {
        if (!BootstrapDeduped.Add(request))
        {
            RecordBootstrapDiagnostic(
                NavigationDiagnosticEventKind.ExternalNavigationDeduplicated,
                "EquivalentRequest",
                BootstrapPending.Count);
            return false;
        }

        while (BootstrapPending.Count >= maximumPendingRequests)
        {
            RouterNavigationRequest dropped = BootstrapPending.Dequeue();
            BootstrapDeduped.Remove(dropped);
            RecordBootstrapDiagnostic(
                NavigationDiagnosticEventKind.ExternalNavigationOverflowed,
                "PendingLimit",
                BootstrapPending.Count);
        }

        BootstrapPending.Enqueue(request);
        return true;
    }

    private static void RecordBootstrapDiagnostic(
        NavigationDiagnosticEventKind kind,
        string reason,
        int pendingCount)
    {
        while (BootstrapDiagnostics.Count >= MaximumBootstrapDiagnostics)
            BootstrapDiagnostics.Dequeue();

        BootstrapDiagnostics.Enqueue(new MauiExternalNavigationBootstrapDiagnostic(kind, reason, pendingCount));
    }
}

internal sealed record MauiExternalNavigationBridgeRegistration(
    IReadOnlyList<RouterNavigationRequest> Requests,
    IReadOnlyList<MauiExternalNavigationBootstrapDiagnostic> Diagnostics);

internal sealed record MauiExternalNavigationBootstrapDiagnostic(
    NavigationDiagnosticEventKind Kind,
    string Reason,
    int PendingCount);

internal enum MauiExternalNavigationBridgeDispatchResult
{
    Accepted,
    Ignored,
    Unavailable
}

internal sealed class MauiNavigationRequestEquivalenceComparer : IEqualityComparer<RouterNavigationRequest>
{
    public static MauiNavigationRequestEquivalenceComparer Instance { get; } = new();

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
            !Equals(x.Provenance, y.Provenance) ||
            x.Metadata.Count != y.Metadata.Count)
        {
            return false;
        }

        foreach (var pair in x.Metadata)
        {
            if (!y.Metadata.TryGetValue(pair.Key, out var otherValue) ||
                !Equals(pair.Value, otherValue))
            {
                return false;
            }
        }

        return true;
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
        foreach (var pair in obj.Metadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            hash.Add(pair.Key, StringComparer.Ordinal);
            hash.Add(pair.Value?.GetType());
            hash.Add(pair.Value);
        }

        return hash.ToHashCode();
    }
}

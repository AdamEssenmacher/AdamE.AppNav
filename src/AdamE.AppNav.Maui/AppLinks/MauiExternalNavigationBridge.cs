using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Maui.AppLinks;

internal static class MauiExternalNavigationBridge
{
    private static readonly Lock Gate = new();
    private static readonly Queue<RouterNavigationRequest> BootstrapPending = new();
    private static readonly HashSet<RouterNavigationRequest> BootstrapDeduped =
        new(MauiNavigationRequestEquivalenceComparer.Instance);
    private static WeakReference<MauiExternalNavigationDispatcher>? _current;

    public static RouterNavigationRequest[] Register(MauiExternalNavigationDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        lock (Gate)
        {
            _current = new WeakReference<MauiExternalNavigationDispatcher>(dispatcher);

            RouterNavigationRequest[] requests = BootstrapPending.ToArray();
            BootstrapPending.Clear();
            BootstrapDeduped.Clear();
            return requests;
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

    public static void Submit(RouterNavigationRequest? request)
    {
        if (request is null)
            return;

        while (true)
        {
            MauiExternalNavigationDispatcher? current;
            lock (Gate)
            {
                if (!TryGetCurrent(out current))
                {
                    Buffer(request);
                    return;
                }
            }

            if (current!.TryDispatchFromBridge(request))
                return;

            lock (Gate)
            {
                if (TryGetCurrent(out MauiExternalNavigationDispatcher? registered) &&
                    ReferenceEquals(registered, current))
                {
                    _current = null;
                }

                if (!TryGetCurrent(out _))
                {
                    Buffer(request);
                    return;
                }
            }
        }
    }

    private static bool TryGetCurrent(out MauiExternalNavigationDispatcher? dispatcher)
    {
        if (_current is not null && _current.TryGetTarget(out dispatcher))
            return true;

        _current = null;
        dispatcher = null;
        return false;
    }

    private static void Buffer(RouterNavigationRequest request)
    {
        if (BootstrapDeduped.Add(request))
            BootstrapPending.Enqueue(request);
    }
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

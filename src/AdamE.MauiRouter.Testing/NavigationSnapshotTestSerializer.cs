using AdamE.MauiRouter.Persistence;
using AdamE.MauiRouter.Routing;
using AdamE.MauiRouter.State;
using AdamE.MauiRouter.History;

namespace AdamE.MauiRouter.Testing;

public sealed class NavigationSnapshotTestSerializer
{
    private readonly NavigationSnapshotSerializer _inner;

    public NavigationSnapshotTestSerializer(
        RouteTable routes,
        NavigationPersistenceOptions? options = null)
    {
        _inner = new NavigationSnapshotSerializer(routes, options);
    }

    public NavigationSnapshot CreateSnapshot(NavigationState state, NavigationHistory history)
    {
        return _inner.CreateSnapshot(state, history);
    }

    public NavigationRestoreResult Restore(NavigationSnapshot snapshot, DateTimeOffset? now = null)
    {
        return _inner.Restore(snapshot, now);
    }
}

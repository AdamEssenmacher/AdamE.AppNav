namespace AdamE.MauiRouter.Persistence;

public interface INavigationStateStore
{
    ValueTask<NavigationSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(NavigationSnapshot snapshot, CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}

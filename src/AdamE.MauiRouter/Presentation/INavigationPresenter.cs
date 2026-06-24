using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;

namespace AdamE.MauiRouter.Presentation;

internal interface INavigationPresenter
{
    event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested;

    ValueTask ApplyAsync(
        NavigationPlan plan,
        NavigationPresentationContext context,
        CancellationToken cancellationToken = default);
}

internal sealed class NullNavigationPresenter : INavigationPresenter
{
    public static NullNavigationPresenter Instance { get; } = new();

    public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested
    {
        add { }
        remove { }
    }

    public ValueTask ApplyAsync(
        NavigationPlan plan,
        NavigationPresentationContext context,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}

using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Presentation;

namespace AdamE.MauiRouter.Testing;

internal sealed class RecordingNavigationPresenter : INavigationPresenter
{
    private readonly List<NavigationPlan> _appliedPlans = new();
    private readonly List<NavigationPresentationContext> _contexts = new();

    public event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested;

    public Func<NavigationPlan, NavigationPresentationContext, CancellationToken, ValueTask>? OnApplyAsync { get; set; }

    public Exception? ThrowOnApply { get; set; }

    public int ApplyCount => _appliedPlans.Count;

    public IReadOnlyList<NavigationPlan> AppliedPlans => _appliedPlans.ToArray();

    public IReadOnlyList<NavigationPresentationContext> Contexts => _contexts.ToArray();

    public NavigationPlan? LastPlan => _appliedPlans.Count == 0 ? null : _appliedPlans[^1];

    public NavigationPresentationContext? LastContext => _contexts.Count == 0 ? null : _contexts[^1];

    public async ValueTask ApplyAsync(
        NavigationPlan plan,
        NavigationPresentationContext context,
        CancellationToken cancellationToken = default)
    {
        _appliedPlans.Add(plan);
        _contexts.Add(context);

        if (ThrowOnApply is not null)
        {
            throw ThrowOnApply;
        }

        if (OnApplyAsync is not null)
        {
            await OnApplyAsync(plan, context, cancellationToken).ConfigureAwait(false);
        }
    }

    public void RequestReconciliation(NavigationReconciliation reconciliation)
    {
        ArgumentNullException.ThrowIfNull(reconciliation);
        ReconciliationRequested?.Invoke(this, new NavigationReconciliationRequestedEventArgs(reconciliation));
    }
}

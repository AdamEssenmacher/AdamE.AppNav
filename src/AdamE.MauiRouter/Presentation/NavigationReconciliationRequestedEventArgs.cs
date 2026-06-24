namespace AdamE.MauiRouter.Presentation;

internal sealed class NavigationReconciliationRequestedEventArgs : EventArgs
{
    public NavigationReconciliationRequestedEventArgs(NavigationReconciliation reconciliation)
    {
        Reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
    }

    public NavigationReconciliation Reconciliation { get; }
}

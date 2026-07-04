namespace AdamE.MauiRouter.Presentation;

/// <summary>
/// Provides data for a host-requested navigation reconciliation event.
/// </summary>
/// <param name="reconciliation">The reconciliation requested by the host presentation surface.</param>
public sealed class NavigationReconciliationRequestedEventArgs(NavigationReconciliation reconciliation) : EventArgs
{
    /// <summary>
    /// Gets the reconciliation requested by the host presentation surface.
    /// </summary>
    public NavigationReconciliation Reconciliation { get; } =
        reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
}

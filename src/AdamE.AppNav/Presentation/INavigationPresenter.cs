using AdamE.AppNav.Plans;

namespace AdamE.AppNav.Presentation;

/// <summary>
/// Applies router navigation plans to a host presentation surface.
/// </summary>
/// <remarks>
/// Implement this interface from platform adapters that own the visible navigation surface. The
/// router calls <see cref="ApplyAsync"/> after it has accepted a navigation plan and listens for
/// <see cref="ReconciliationRequested"/> when host navigation changes outside a router command.
/// </remarks>
public interface INavigationPresenter
{
    /// <summary>
    /// Occurs when the host presentation surface changes the navigation state outside a router-issued command.
    /// </summary>
    event EventHandler<NavigationReconciliationRequestedEventArgs>? ReconciliationRequested;

    /// <summary>
    /// Applies a navigation plan to the host presentation surface.
    /// </summary>
    /// <remarks>
    /// Implementations must complete this task only after the target navigation state has been fully
    /// reflected in the host presentation surface. If the target state cannot be fully presented, the
    /// implementation must throw or observe cancellation, so the router does not commit the logical
    /// state, history, or persistence snapshot for a partial presentation.
    /// </remarks>
    /// <param name="plan">The accepted navigation plan to present.</param>
    /// <param name="context">Runtime context for the presentation operation.</param>
    /// <param name="cancellationToken">A token that can cancel a presentation.</param>
    /// <returns>A value task that completes when the presentation has finished.</returns>
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

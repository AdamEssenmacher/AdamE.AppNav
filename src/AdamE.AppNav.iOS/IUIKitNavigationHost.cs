using AdamE.AppNav.Navigation;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;

namespace AdamE.AppNav.iOS;

/// <summary>
/// What the presenter tells the application about native events. <see cref="UIKitRouterNavigationHost"/> sends each
/// to the router; an application that coordinates its own navigation state implements it to route them through that.
/// </summary>
/// <remarks>The presenter calls these on the main thread, never while it is applying a plan, and never awaits them
/// inside an operation. A failure is reported through <see cref="ReportRecoveredFailure"/>.</remarks>
public interface IUIKitNavigationHost
{
    /// <summary>
    /// The person tried to dismiss a modal by swiping it down. The presenter refused the dismissal; this is a Back
    /// request, which the router's Back policy may refuse too.
    /// </summary>
    Task RequestBackAsync(CancellationToken cancellationToken);

    /// <summary>
    /// UIKit has already popped the pushed entry <paramref name="entryId"/>: its back button or back-swipe completed.
    /// This is a fact to record, never a request to refuse. Record the stack without that entry and anything above it.
    /// </summary>
    Task RecordNativePopAsync(string entryId, CancellationToken cancellationToken);

    /// <summary>Native state left the accepted presentation (a modal dismissed by something other than the
    /// presenter): reapply the router's accepted state with <see cref="IRouterNavigator.RefreshPresentationAsync"/>.
    /// </summary>
    Task RequestRefreshAsync(CancellationToken cancellationToken);

    /// <summary>A failure the presenter recovered from or could not attribute to an operation. It must not throw.
    /// </summary>
    void ReportRecoveredFailure(Exception failure);
}

/// <summary>Sends the presenter's native events to an <see cref="IRouterNavigator"/>.</summary>
/// <param name="router">The router, read when an event arrives, so the host can be built before the router that
/// presents through the same presenter.</param>
/// <param name="topology">The presenter's topology, whose stack a native pop shortens.</param>
/// <param name="reportRecoveredFailure">Receives recovered failures. It must not throw.</param>
public sealed class UIKitRouterNavigationHost(
    Func<IRouterNavigator> router,
    UIKitNavigationTopology topology,
    Action<Exception> reportRecoveredFailure) : IUIKitNavigationHost
{
    private const int StaleAttempts = 10;

    /// <inheritdoc />
    public async Task RequestBackAsync(CancellationToken cancellationToken) =>
        _ = await router().BackAsync(topology.WindowId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>A reconciliation names the state it changes from; if another operation commits first, the router
    /// rejects it as stale. The pop still happened, so the state that operation committed is read again.</remarks>
    public async Task RecordNativePopAsync(string entryId, CancellationToken cancellationToken)
    {
        IRouterNavigator navigator = router();
        for (int attempt = 1; ; attempt++)
        {
            NavigationState current = navigator.CurrentState;
            if (WithoutPopped(current, entryId) is not { } target)
                return;
            try
            {
                _ = await navigator.ReconcileAsync(
                        new NavigationReconciliation(
                            current,
                            target,
                            NavigationReconciliationSource.HostBack,
                            Reason: "UIKit committed a pop."),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            // Each rejection follows another committed operation, so this ends; this many in a row is a fault.
            catch (StaleNavigationReconciliationException) when (attempt < StaleAttempts)
            {
            }
        }
    }

    /// <inheritdoc />
    public async Task RequestRefreshAsync(CancellationToken cancellationToken) =>
        await router().RefreshPresentationAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void ReportRecoveredFailure(Exception failure) => reportRecoveredFailure(failure);

    private NavigationState? WithoutPopped(NavigationState state, string entryId)
    {
        if (state.FindWindow(topology.WindowId) is not { Root: StackNode stack } window || stack.Id != topology.StackId)
            return null;
        int index = stack.Entries.TakeWhile(entry => entry.Id != entryId).Count();
        // The root entry is never popped, and an entry the router no longer shows has already been recorded.
        return index is 0 || index == stack.Entries.Count
            ? null
            : state.ReplaceWindow(window with { Root = stack with { Entries = [.. stack.Entries.Take(index)] } });
    }
}

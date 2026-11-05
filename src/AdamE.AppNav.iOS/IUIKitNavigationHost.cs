using AdamE.AppNav.Navigation;
using AdamE.AppNav.Presentation;
using AdamE.AppNav.State;

namespace AdamE.AppNav.iOS;

/// <summary>
/// What the presenter tells the application about native events. <see cref="UIKitRouterNavigationHost"/> sends each
/// to the router; an application that coordinates its own navigation state implements it to route them through that.
/// </summary>
/// <remarks>
/// The presenter calls these on the main thread. It calls the asynchronous members in a later turn of the main run loop,
/// with no execution context from the operation that noticed the event, so a host may call the router from them; it
/// never awaits them inside an operation. An operation may still be applying when one is called: the router serializes
/// the host's own call behind it. A failure they throw is reported through <see cref="ReportRecoveredFailure"/>.
/// </remarks>
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
/// <remarks>Constructor arguments: the router, read when an event arrives, so the host can be built before the router
/// that presents through the same presenter; the presenter's topology, whose stack a native pop shortens; and the
/// receiver of recovered failures, which must not throw.</remarks>
public sealed class UIKitRouterNavigationHost : IUIKitNavigationHost
{
    private const int StaleAttempts = 10;
    private readonly Func<IRouterNavigator> router;
    private readonly UIKitNavigationTopology topology;
    private readonly Action<Exception> reportRecoveredFailure;

    /// <summary>A host for the router <paramref name="router"/> returns.</summary>
    public UIKitRouterNavigationHost(
        Func<IRouterNavigator> router,
        UIKitNavigationTopology topology,
        Action<Exception> reportRecoveredFailure)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(reportRecoveredFailure);
        this.router = router;
        this.topology = topology;
        this.reportRecoveredFailure = reportRecoveredFailure;
    }

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

    private bool OwnedByPopped(ModalNode modal, HashSet<string> popped) =>
        topology.Modals.TryGetValue(modal.Id, out UIKitModalDeclaration? declaration)
        && declaration.OwnerEntryId?.Invoke(modal.RouteEntry) is { } owner
        && popped.Contains(owner);

    private NavigationState? WithoutPopped(NavigationState state, string entryId)
    {
        if (state.FindWindow(topology.WindowId) is not { Root: StackNode stack } window || stack.Id != topology.StackId)
            return null;
        int index = stack.Entries.TakeWhile(entry => entry.Id != entryId).Count();
        // The root entry is never popped, and an entry the router no longer shows has already been recorded.
        if (index is 0 || index == stack.Entries.Count)
            return null;
        // A modal owned by a popped entry goes with it.
        HashSet<string> popped = [.. stack.Entries.Skip(index).Select(static entry => entry.Id)];
        return state.ReplaceWindow(
            window with
            {
                Root = stack with { Entries = [.. stack.Entries.Take(index)] },
                Modals = [.. window.Modals.Where(modal => !OwnedByPopped(modal, popped))]
            });
    }
}

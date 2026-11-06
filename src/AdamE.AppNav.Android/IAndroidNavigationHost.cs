using AdamE.AppNav.Back;
using AdamE.AppNav.Navigation;
using AdamE.AppNav.Presentation;

namespace AdamE.AppNav.Android;

/// <summary>
/// What the presenter tells the application about native events. <see cref="AndroidRouterNavigationHost"/> sends each
/// to the router; an application that coordinates its own navigation state implements it to route them through that.
/// </summary>
/// <remarks>
/// The presenter calls these on the main thread. It calls the asynchronous members in a later turn of the main looper,
/// with no execution context from the operation that noticed the event, so a host may call the router from them; it
/// never awaits them inside an operation. An operation may still be applying when one is called: the router serializes
/// the host's own call behind it. A request is dropped, never made, if by the time it would be the activity it came
/// from has detached or the page it is about has gone. A failure they throw is reported through
/// <see cref="ReportRecoveredFailure"/>.
/// <para>
/// On Android every Back is a request: the presenter never removes a page by itself, and the router's Back policy
/// decides what Back does.
/// </para>
/// </remarks>
public interface IAndroidNavigationHost
{
    /// <summary>
    /// A Back request for the window: system Back while a page is pushed over the root page, which the router's Back
    /// policy may refuse. The presenter calls <see cref="RequestModalBackAsync"/> for a modal instead; this is what that
    /// member's default implementation forwards to.
    /// </summary>
    Task RequestBackAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A Back request for the modal entry <paramref name="entryId"/>: system Back on its dialog, or the dialog's own
    /// cancel (a tap outside it, a sheet dragged away). The router's Back policy may refuse it; the presenter then shows
    /// the dialog again.
    /// </summary>
    /// <remarks>A cancel can arrive after the modal has already gone, so a host that queues this request should check
    /// the entry when it admits the request, as <see cref="AndroidRouterNavigationHost"/> does. The default
    /// implementation forwards to <see cref="RequestBackAsync"/> for hosts that handle their own admission.</remarks>
    Task RequestModalBackAsync(string entryId, CancellationToken cancellationToken) => RequestBackAsync(cancellationToken);

    /// <summary>Native state left the accepted presentation (a dialog dismissed by something other than the presenter,
    /// or one a refused Back request left closed): reapply the router's accepted state with
    /// <see cref="IRouterNavigator.RefreshPresentationAsync"/>.</summary>
    Task RequestRefreshAsync(CancellationToken cancellationToken);

    /// <summary>A failure the presenter recovered from or could not attribute to an operation. It must not throw.
    /// </summary>
    void ReportRecoveredFailure(Exception failure);
}

/// <summary>Sends the presenter's native events to an <see cref="IRouterNavigator"/>.</summary>
/// <remarks>Constructor arguments: the router, read when an event arrives, so the host can be built before the router
/// that presents through the same presenter; the presenter's topology, whose window Back requests name; and the
/// receiver of recovered failures, which must not throw.</remarks>
public sealed class AndroidRouterNavigationHost : IAndroidNavigationHost
{
    private readonly Func<IRouterNavigator> router;
    private readonly NavigationTopology topology;
    private readonly Action<Exception> reportRecoveredFailure;

    /// <summary>A host for the router <paramref name="router"/> returns.</summary>
    public AndroidRouterNavigationHost(
        Func<IRouterNavigator> router,
        NavigationTopology topology,
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
        _ = await router().BackAsync(new BackNavigationRequest(topology.WindowId, BackNavigationSource.Host),
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task RequestModalBackAsync(string entryId, CancellationToken cancellationToken) =>
        _ = await router().BackAsync(
            new BackNavigationRequest(topology.WindowId, BackNavigationSource.Host) { ExpectedModalEntryId = entryId },
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task RequestRefreshAsync(CancellationToken cancellationToken) =>
        await router().RefreshPresentationAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public void ReportRecoveredFailure(Exception failure) => reportRecoveredFailure(failure);
}

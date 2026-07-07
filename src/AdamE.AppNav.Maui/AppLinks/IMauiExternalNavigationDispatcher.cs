using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Maui.AppLinks;

/// <summary>
/// Dispatches app-owned external navigation requests into the AppNav MAUI boundary.
/// </summary>
public interface IMauiExternalNavigationDispatcher
{
    /// <summary>
    /// Dispatches an external request, buffering it until AppNav is ready when necessary.
    /// </summary>
    /// <remarks>
    /// Built-in MAUI app-link callbacks attach provenance automatically. App-owned sources such as Branch, push,
    /// QR, or provider SDK bridges should attach <see cref="NavigationRequestProvenance" /> before calling this
    /// method. If dispatch later fails inside the router boundary, the buffered request remains pending for a later retry.
    /// Raw auth callbacks should normally terminate in the app's auth subsystem; the router should see deferred replay
    /// or an app-authored post-auth navigation request.
    /// </remarks>
    void Dispatch(RouterNavigationRequest? request);
}

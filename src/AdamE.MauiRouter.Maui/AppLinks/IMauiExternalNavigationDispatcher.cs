using AdamE.MauiRouter.Requests;

namespace AdamE.MauiRouter.Maui.AppLinks;

/// <summary>
/// Dispatches app-owned external navigation requests into the MAUI router boundary.
/// </summary>
public interface IMauiExternalNavigationDispatcher
{
    /// <summary>
    /// Dispatches an external request, buffering it until the MAUI router is ready when necessary.
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

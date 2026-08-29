using AdamE.AppNav.Requests;

namespace AdamE.AppNav.Maui.AppLinks;

/// <summary>
/// Dispatches app-owned external navigation requests into the AppNav MAUI boundary.
/// </summary>
public interface IMauiExternalNavigationDispatcher
{
    /// <summary>
    /// Attempts to accept an external request, buffering it until AppNav is ready when necessary.
    /// </summary>
    /// <remarks>
    /// Built-in MAUI app-link callbacks attach provenance automatically. App-owned sources such as Branch, push,
    /// QR, or provider SDK bridges should attach <see cref="NavigationRequestProvenance" /> before calling this
    /// method. Retryable failures are bounded by the configured attempt and age limits.
    /// Raw auth callbacks should normally terminate in the app's auth subsystem; the router should see deferred replay
    /// or an app-authored post-auth navigation request.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when a new request was accepted; <see langword="false"/> when the request was null,
    /// rejected, expired, duplicated, or the dispatcher is no longer available.
    /// </returns>
    bool TryDispatch(RouterNavigationRequest? request);
}

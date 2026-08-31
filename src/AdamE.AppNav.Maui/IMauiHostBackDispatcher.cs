namespace AdamE.AppNav.Maui;

/// <summary>
/// Routes an opt-in MAUI host Back action through presentation-page and logical Back handling.
/// </summary>
public interface IMauiHostBackDispatcher
{
    /// <summary>
    /// Dispatches Back, first popping a route-owned presentation page and otherwise evaluating logical Back policies.
    /// </summary>
    ValueTask<MauiHostBackResult> BackAsync(
        string? windowId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues one safely observed host Back operation for use by synchronous MAUI Back overrides.
    /// </summary>
    /// <param name="windowId">The router window to navigate within, or <see langword="null"/> for the active window.</param>
    /// <param name="onUnhandled">
    /// An optional main-thread callback that performs the platform fallback when neither presentation nor logical Back
    /// handles the queued request. The callback is associated with the first accepted request when presses are coalesced.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the request was accepted or coalesced with an accepted request;
    /// otherwise, <see langword="false"/> when the runtime can no longer accept navigation.
    /// </returns>
    bool TryBack(string? windowId = null, Action? onUnhandled = null);
}

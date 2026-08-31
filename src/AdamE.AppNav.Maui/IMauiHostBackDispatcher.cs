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
    /// <returns>
    /// <see langword="true"/> when the request was accepted or coalesced with an accepted request;
    /// otherwise, <see langword="false"/> when the runtime can no longer accept navigation.
    /// </returns>
    bool TryBack(string? windowId = null);
}

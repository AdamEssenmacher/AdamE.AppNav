namespace AdamE.AppNav.Requests;

/// <summary>
/// Represents exclusive ownership of a FIFO snapshot of deferred navigation requests.
/// </summary>
/// <remarks>
/// Requests remain stored until acknowledged. This provides at-least-once replay: process termination after
/// navigation but before acknowledgement may replay a request again, but does not lose it.
/// </remarks>
public interface IDeferredNavigationRequestLease : IAsyncDisposable
{
    /// <summary>
    /// Gets the immutable FIFO request snapshot captured when the lease was acquired.
    /// </summary>
    IReadOnlyList<RouterNavigationRequest> Requests { get; }

    /// <summary>
    /// Durably removes a successfully replayed request from the store.
    /// </summary>
    /// <param name="requestIndex">The index of the request in <see cref="Requests"/>.</param>
    /// <param name="cancellationToken">A token that can cancel acknowledgement.</param>
    ValueTask AcknowledgeAsync(
        int requestIndex,
        CancellationToken cancellationToken = default);
}

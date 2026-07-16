namespace AdamE.AppNav.Requests;

/// <summary>
/// Indicates that one request in an otherwise supported deferred-navigation snapshot is invalid.
/// </summary>
public sealed class InvalidDeferredNavigationRequestDataException : Exception
{
    /// <summary>
    /// Initializes an exception for an invalid request record.
    /// </summary>
    /// <param name="requestIndex">The zero-based index of the invalid persisted request.</param>
    /// <param name="innerException">The validation or restoration failure.</param>
    public InvalidDeferredNavigationRequestDataException(int requestIndex, Exception innerException)
        : base($"Deferred navigation request at index {requestIndex} is invalid.", innerException)
    {
        if (requestIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(requestIndex));

        RequestIndex = requestIndex;
    }

    /// <summary>
    /// Gets the zero-based index of the invalid persisted request.
    /// </summary>
    public int RequestIndex { get; }
}

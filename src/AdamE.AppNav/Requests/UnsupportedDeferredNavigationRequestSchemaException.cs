namespace AdamE.AppNav.Requests;

/// <summary>
/// Indicates that persisted deferred-navigation data uses a schema other than the exact supported version.
/// </summary>
public sealed class UnsupportedDeferredNavigationRequestSchemaException : Exception
{
    /// <summary>
    /// Initializes an exception for a deferred-request schema version mismatch.
    /// </summary>
    /// <param name="actualVersion">The version found in persisted data.</param>
    /// <param name="supportedVersion">The exact version accepted by this library.</param>
    public UnsupportedDeferredNavigationRequestSchemaException(int actualVersion, int supportedVersion)
        : base($"Deferred navigation request schema version {actualVersion} is not supported; version {supportedVersion} is required.")
    {
        ActualVersion = actualVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>
    /// Gets the version found in persisted data.
    /// </summary>
    public int ActualVersion { get; }

    /// <summary>
    /// Gets the exact schema version accepted by this library.
    /// </summary>
    public int SupportedVersion { get; }
}

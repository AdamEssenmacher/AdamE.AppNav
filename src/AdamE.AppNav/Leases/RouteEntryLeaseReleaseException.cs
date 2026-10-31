namespace AdamE.AppNav.Leases;

/// <summary>
/// Reports an entry-lease release that could not be shown safe. The lease kept the resources it had not yet disposed.
/// </summary>
public sealed class RouteEntryLeaseReleaseException : Exception
{
    public RouteEntryLeaseReleaseException(string? entryId, string message, Exception innerException)
        : base(message, innerException)
    {
        EntryId = entryId;
    }

    /// <summary>Gets the entry ID of the lease, or <see langword="null"/> for a child lease without one.</summary>
    public string? EntryId { get; }
}

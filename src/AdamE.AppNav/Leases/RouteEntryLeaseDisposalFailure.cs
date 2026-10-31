namespace AdamE.AppNav.Leases;

/// <summary>What a failure to dispose a resource owned by a <see cref="RouteEntryLease"/> means.</summary>
public enum RouteEntryLeaseDisposalFailure
{
    /// <summary>A survived defect: it is reported to the host and the release continues.</summary>
    Defect = 0,

    /// <summary>
    /// The release cannot be shown safe: the lease and its ancestors keep their remaining resources, and the failure
    /// goes to the host's fatal boundary.
    /// </summary>
    Fatal = 1
}

internal sealed class RouteEntryLeaseFatalDisposalException(Exception inner)
    : Exception("A resource whose disposal failure is fatal could not be disposed.", inner);

namespace AdamE.AppNav.Leases;

/// <summary>Why a <see cref="RouteEntryLease"/> retired.</summary>
public enum RouteEntryLeaseRetirement
{
    /// <summary>The lease has not retired.</summary>
    None = 0,

    /// <summary>A commit produced a state that no longer contains the entry.</summary>
    Dropped = 1,

    /// <summary>
    /// The lease's entry ended without being committed: its request failed, was cancelled, rejected by a policy, or
    /// refused; its plan omitted the entry; the router shut down while it waited; it was closed before it was sent; or an
    /// adapter opened it for an entry the resulting state does not contain.
    /// </summary>
    Abandoned = 2,

    /// <summary>The application retired the lease.</summary>
    Application = 3,

    /// <summary>The lease's parent retired.</summary>
    Parent = 4,

    /// <summary>The router shut down.</summary>
    Shutdown = 5
}

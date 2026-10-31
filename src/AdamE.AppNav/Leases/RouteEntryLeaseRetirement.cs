namespace AdamE.AppNav.Leases;

/// <summary>Why a <see cref="RouteEntryLease"/> retired.</summary>
public enum RouteEntryLeaseRetirement
{
    /// <summary>The lease has not retired.</summary>
    None = 0,

    /// <summary>A commit produced a state that no longer contains the entry.</summary>
    Dropped = 1,

    /// <summary>The presentation that introduced the entry failed or was cancelled before committing it.</summary>
    Abandoned = 2,

    /// <summary>The application retired the lease.</summary>
    Application = 3,

    /// <summary>The lease's parent retired.</summary>
    Parent = 4,

    /// <summary>The router shut down.</summary>
    Shutdown = 5
}

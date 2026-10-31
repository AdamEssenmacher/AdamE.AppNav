namespace AdamE.AppNav.Leases;

// The host's InvokeAsync task completed before the work core gave it had finished: a host failure.
internal sealed class RouteEntryLeaseHostCompletedEarlyException()
    : InvalidOperationException(
        "The route-entry lease host's InvokeAsync completed before the work it was given had finished. Its task must " +
        "complete only after the work's ValueTask completes, and fault with its exception.");

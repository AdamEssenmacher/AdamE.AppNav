# Entry leases

[Documentation home](../index.md)

This is advanced material for adapter authors and for applications that build
what a screen borrows before they navigate to it.

A `RouteEntryLease` owns what one occurrence of a `RouteEntry` borrows: view
models, DI scopes, subscriptions, or anything else that must outlive the
request but not the entry. Core decides when the lease's authority ends and
when its resources may be released; the lease's opener decides what it holds;
the adapter reports which native artifacts still use it. The name is
deliberately different from `RouteStateLifetime`, which classifies route
metadata, not resources.

Here "adapter" means any `INavigationPresenter`, and "the host dispatcher" is
the `IRouteEntryLeaseHost` the host supplies when it creates the registry and
the router.

## The lease tree

Most leases belong to one entry occurrence. An application lease, opened with
`OpenApplicationLease`, belongs to no entry: it is retired only by the
application, by its parent, or at shutdown, and it can be the parent of entry
leases that share something, such as a selection several screens use. Any lease
can have children, entry leases or application leases, and a parent is
released only after all of them.

## One occurrence, one lease

A lease belongs to an entry ID from the moment it is opened. The occurrence ends
when a commit drops the entry or its request is abandoned; the next lease opened
for that ID is a new occurrence. While a lease is the current occurrence of its
ID, `Open` refuses a second one, so an application that needs a fresh
occurrence of a still-current entry uses a fresh entry ID. A lease that was
never committed stops being current once it is released; a committed one stays
current, even after release, until a commit drops it.

Leases are keyed by entry ID across the whole navigation state, so with a
registry configured an entry ID must appear at most once in any state, not just
once per stack. The router rejects a plan, or an initial state, that repeats
one, before presentation, with `AppNavigationConfigurationException`. Two tabs
that show the same route need entry IDs that include the tab.

## Opening, attaching, and closing

A lease is opened by whoever builds what the entry borrows.

- **The application**, on its UI thread, before it navigates: `Open(entryId)`
  or `Open(entryId, parent)`, then `navigator.NavigateAsync(request, lease)`,
  which links the lease to that one request instance (a navigator decorator
  must pass it through unchanged). Only the router configured with
  the lease's registry accepts it; any other router refuses the request before
  admission, and the lease is abandoned. When the plan does not contain the
  lease's entry, the router reports `EntryLeaseNotInPlan` before the lease is
  abandoned.
- **The adapter**, inside `ApplyAsync`: `GetOrOpen(entryId, out opened)`
  returns the current lease, or opens one that belongs to the presentation in
  progress. The MAUI page factory opens a lease this way for each entry and
  attaches the entry's DI scope to it. When the application opened the lease
  itself, the application owns what the entry borrows: the factory adds
  nothing to that lease and gives each page a scope of its own. When the
  operation ends, a lease it opened for an entry the resulting state does not
  contain is abandoned and closed, so a presenter that uses `GetOrOpen` as a
  lookup cannot leave one behind. A lease it opened for an entry the state does
  contain stays open until the adapter closes it, as the page factory does once
  it has attached the entry's scope.

The opener attaches resources with `Own`, retirement work with `OnRetired`, and
in-flight preparations with `Track`. `Own` takes an optional
`RouteEntryLeaseDisposalFailure` that says what a failure to dispose that
resource means. Resources may be attached after
retirement: an application may hand over a view model only when the entry
retires. So a lease accepts resources and children until its opener calls
`Close()`, and it is never released before then. Closing an application-opened
entry lease that was never sent nor committed abandons it.

`IsCommitted` says whether the entry is in the router's committed state. A
lease opened for an entry that is already committed is committed from birth,
and a commit that drops the entry clears it.
Core never builds what an entry borrows. A committed entry that has no lease is
a defect: an adapter that requires leases fails the presentation.

## Retirement

Retirement ends authority: `IsRetired` becomes true, `RetiredToken` is
cancelled, and the lease accepts no new native artifact. A lease retires at the
earliest of:

- a commit whose state no longer contains the entry (`Dropped`);
- abandonment (`Abandoned`): the lease's request ends without a commit that
  contains the entry. That covers a failed or cancelled presentation, a policy
  rejection, a planner failure, a request cancelled while queued, router
  disposal while it waits, and a lease its opener closes without sending. Only
  the lease's own request abandons it: another request's failure never does,
  even one for the same entry. An adapter-opened lease is abandoned when the
  operation that opened it ends without committing it;
- `Retire()` from the application, at any time and for any reason it owns
  (`Application`);
- its parent retiring (`Parent`), or the router shutting down (`Shutdown`).

Retirement notifications are always asynchronous. Core never runs the token's
callbacks or the `OnRetired` work inline: not inside `ApplyAsync`, not under
core's operation lock, and not under the lock of a caller that retires a lease.
The token is cancelled with `CancelAsync`, so its callbacks run on the thread
pool; as with any token, a callback registered after retirement runs at once,
inside `Register`. `OnRetired` work runs on the host dispatcher and is awaited
before release; a failure in it is a survived defect.

Retiring a lease does not navigate. A retired entry that is still committed
stays in the state until the application starts an ordinary navigation that
removes it. Until that navigation commits, a presentation of the entry, such as
a refresh after native host recreation, finds `IsRetired && IsCommitted` and
shows a placeholder instead of rebuilding the entry's screen or failing.

## Release

Release disposes the lease's resources. It requires all of:

- the lease is retired, and its retirement work has finished;
- its opener has closed it;
- no native artifact is attached. The adapter calls `TryAttach` for each
  artifact it builds and `Detach` once the artifact no longer uses the lease.
  Acknowledgement is per artifact: when a host recreates its views, the old
  views detach and the refresh attaches new ones, so a committed entry is never
  released by detachment alone;
- every child has been released, and every tracked preparation has finished.

An abandoned presentation's leases retire only after `ApplyAsync` has returned,
so the adapter's rollback has finished and has detached what it removed.

Release never runs inline either. It runs on the host dispatcher, children
before their parent, and disposes the lease's own resources in reverse order of
`Own`. `Released` completes afterwards.

## Artifacts in other hosts

An artifact is whatever native object borrows the lease; the attachment API
names no host type. A UIKit presenter attaches each entry's view controller and
detaches it when that controller leaves the hierarchy, including a pop the
platform commits before the router reconciles: the lease is still committed
then, so nothing is released until the reconciliation's commit retires it. An
Android presenter attaches the views of each Activity epoch and detaches them
all when the Activity is destroyed; the refresh after recreation attaches new
ones. A presenter that requires a lease for every entry looks it up with
`TryGet` and fails the presentation when there is none, rather than calling
`GetOrOpen`.

## Failures

- A disposer owned as `RouteEntryLeaseDisposalFailure.Defect`, the default,
  or retirement work that throws is a survived defect, reported to
  `IRouteEntryLeaseHost.ReportDefect`; release continues.
- A release that cannot complete keeps that lease's undisposed resources and
  every ancestor's, never completes `Released`, and goes to
  `IRouteEntryLeaseHost.ReportFatal`, which surfaces it through the host's
  fatal boundary. That happens when a disposer owned as
  `RouteEntryLeaseDisposalFailure.Fatal` throws, so a child whose release did
  not complete keeps its parent's resources, or when the host cannot run the
  release. Core never forces disposal.
- Core imposes no timeouts. A disposer that never completes, an attachment
  that is never detached, or a lease that is never closed keeps its release,
  and so the router's disposal, waiting.

## Shutdown

Router disposal follows this order:

1. Core retires every lease, and the router and the registry stop accepting
   work.
2. The adapter detaches and disposes its native artifacts, acknowledging each
   one. Core does not call the adapter's teardown: the host does, alongside the
   router's disposal, as the MAUI `AppNavRuntime` does.
3. The openers close their leases, attaching anything they still hand over.
4. Every release finishes, or its failure reaches the fatal boundary.
5. The router's disposal returns. If any release failed, during shutdown or
   earlier, `DisposeAsync` throws `RouteEntryLeaseReleaseException` (or an
   `AggregateException` of them) instead, and the failed leases and their
   ancestors keep their resources, so the owner disposes nothing that they
   borrow.

## Not in scope

Leases describe ownership, not presence. Core reports no appear, disappear,
foreground, or background lifecycle; work that depends on visibility stays in
the host's renderers.

## Next steps

- Read the [adapter contract](adapter-contract.md).
- Review [navigation outcomes and
  failures](../guides/04-navigation-outcomes-and-failure-handling.md).

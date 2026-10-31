# Entry leases

[Documentation home](../index.md)

This is advanced material for adapter authors and for applications that build
what a screen borrows before they navigate to it.

A `RouteEntryLease` owns what one occurrence of a `RouteEntry` borrows: view
models, DI scopes, subscriptions, or anything else that must outlive the
request but not the entry. Core decides when the lease's authority ends and
when its resources may be released; the application decides what the lease
holds; the adapter reports which native artifacts still use it. The name is
deliberately different from `RouteStateLifetime`, which classifies route
metadata, not resources.

## One occurrence, one lease

A lease belongs to an entry ID from the moment it is opened until it is
released. An occurrence ends when the lease retires because a commit dropped
the entry or because its presentation was abandoned; the next lease opened for
the same ID is a new occurrence. While a lease is the current occurrence of its
ID, `RouteEntryLeaseRegistry.Open` refuses a second one, so an application that
needs a fresh occurrence of a still-current entry uses a fresh entry ID.

Leased entry IDs are looked up across the whole navigation state, so an ID that
has a lease should appear at most once in any state.

## Opening and attaching

The application creates one `RouteEntryLeaseRegistry` with an
`IRouteEntryLeaseHost`, passes it to the router through
`RouterNavigatorFactoryOptions.EntryLeases`, and gives it to its presenter. To
navigate to an entry whose resources it builds itself, the application:

1. opens the lease with `Open(entryId)`, on its UI thread;
2. hands it what it built with `Own(...)`, child leases with `OpenChild()` or
   `Open(entryId, parent)`, and in-flight preparations with `Track(task)`;
3. sends the request whose plan contains that entry ID.

Core never builds what an entry borrows. An adapter that does build per-entry
resources, such as the MAUI page factory's DI scope, calls `GetOrOpen(entryId)`
during presentation; it receives the application's lease when one is open.

A lease opened for an entry that is already committed is committed from birth.

## Retirement

Retirement ends authority: `RetiredToken` is cancelled, `IsRetired` becomes
true, and new native artifacts can no longer attach. A lease retires at the
earliest of:

- a commit whose state no longer contains the entry (`Dropped`);
- abandonment: the presentation whose plan introduced the entry failed or was
  cancelled before committing it (`Abandoned`);
- `Retire()` from the application, at any time and for any reason it owns
  (`Application`);
- its parent retiring (`Parent`), or the router shutting down (`Shutdown`).

Retiring a lease does not navigate. A retired entry that is still committed
stays in the state until the application starts an ordinary navigation that
removes it, and its lease stays the entry's current lease until then, even
after release. Until that navigation commits, a presentation of the entry, such
as a refresh after native host recreation, finds `IsRetired && IsCommitted` and
shows a placeholder instead of rebuilding the entry's screen or failing.

Abandonment applies only to leases that were never committed and whose IDs are
in the failed plan's target but not in the committed state. A lease opened for
a request that has not reached presentation is not abandoned by another
request's failure. A request that never reaches presentation, for example one
a policy rejects, leaves its lease open; the application retires it.

## Release

Release disposes the lease's resources. It requires all of:

- the lease is retired;
- no native artifact is attached. The adapter calls `TryAttach` for each
  artifact it builds and `Detach` on that attachment once the artifact no
  longer uses the lease. Acknowledgement is per artifact: when a host
  recreates its views, the old views detach and the refresh attaches new ones,
  so a committed entry is never released by detachment alone;
- every child lease has been released;
- every tracked preparation has finished.

An abandoned lease is retired only after `INavigationPresenter.ApplyAsync`
has returned, so the adapter's rollback has finished and has detached any
artifact it removed before release can start.

Release runs on the host's dispatcher through
`IRouteEntryLeaseHost.InvokeAsync`, children before their parent, and disposes
the lease's own resources in reverse order of `Own`. `Released` completes
afterwards.

## Unsafe release

Core never forces disposal. If a resource's disposal throws, or the host cannot
run the release, the lease keeps every resource not yet disposed, never
completes `Released`, and the failure is passed to
`IRouteEntryLeaseHost.ReportFatal` as a `RouteEntryLeaseReleaseException`. Its
ancestors keep their resources too. The host surfaces the failure through its
fatal boundary; it is not a survivable navigation failure.

## Shutdown

Disposing the router retires every lease and seals the registry. The router's
disposal completes only after every lease has been released or its failure has
been reported. Asynchronous retirement work that a lease owns, such as a
pending provider request that observes `RetiredToken`, is tracked with
`Track(task)` and is awaited before release. The adapter's own shutdown must
detach every artifact it attached; until it does, the router's disposal does not
complete.

## Not in scope

Leases describe ownership, not presence. Core reports no appear, disappear,
foreground, or background lifecycle; work that depends on visibility stays in
the host's renderers.

## Next steps

- Read the [adapter contract](adapter-contract.md).
- Review [navigation outcomes and
  failures](../guides/04-navigation-outcomes-and-failure-handling.md).

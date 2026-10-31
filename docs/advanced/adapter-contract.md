# Adapter contract

[Documentation home](../index.md)

This is advanced material for adapter authors. Application integrations should
start with [Getting started](../guides/01-getting-started.md).

AppNav core is host-independent. An adapter implements `INavigationPresenter`
using only public core APIs and is created with the router through
`RouterNavigatorFactory`.

## Core owns

- semantic route identity and formatting;
- request transforms, matching, policy, and access decisions;
- standard or app-specific logical planning;
- supported topology validation;
- logical state, history, diagnostics, and operation serialization;
- commit only after successful presentation;
- when a route entry's [lease](entry-leases.md) retires: on a commit that
  drops the entry, on abandonment when its request ends without committing it,
  when its parent retires, and at shutdown, always notified asynchronously;
- when a retired lease is released: only once its opener has closed it and no
  artifact, child, retirement work, or tracked preparation still holds it,
  children first, on the host dispatcher, never inline and never by force;
- shutdown and presenter-event detachment orchestration, returning only after
  every lease has been released or its failure has reached the fatal boundary.

## Adapter owns

- UI artifacts and mapping from `RouteEntry` to them;
- attaching each native artifact to its entry's lease, and detaching it once
  the artifact no longer uses the lease, including during rollback and
  shutdown, and finishing rollback before `ApplyAsync` throws;
- the leases it opens inside `ApplyAsync` for resources it builds, such as the
  MAUI page factory's DI scope, which it closes as their opener;
- presenting a placeholder for an entry whose lease is retired but still
  committed, instead of rebuilding it;
- host lifecycle, thread affinity, and native event subscription;
- native container mutation, rollback, verification, and recovery;
- native-to-logical reconciliation;
- platform ingress and storage choices.

The host supplies the `IRouteEntryLeaseHost`: the UI dispatcher that runs
retirement work and releases, and the boundary that receives defects and fatal
failures. It also starts the adapter's teardown alongside the router's
disposal. The application owns what an entry borrows when it builds it: it
opens the lease before navigating with `NavigateAsync(request, lease)`,
attaches what it built, retires the lease for its own reasons, and closes it.

That mapping is presentation policy, not route identity. Core does not require
a `RouteEntry` to equal one page or one view model. An adapter may use one or
several host artifacts, provided it can apply, verify, roll back, and reconcile
the logical target consistently. The built-in MAUI adapter uses one anchor
`Page` per entry and optionally lets that route own additional presentation
pages; another host can choose a different artifact model.

The presenter must leave logical state uncommitted on failure or cancellation,
and must finish its rollback before `ApplyAsync` throws: core abandons the
leases the presentation introduced only then. After recreating native host artifacts, detach the
old artifacts and call `IRouterNavigator.RefreshPresentationAsync`.
The router serializes refresh behind pending operations, then reapplies its current
accepted state without planning a new route or changing history. Do not reapply a
privately cached target outside that operation: it may precede or follow a concurrent
logical commit. The adapter still owns native epochs and cancellation while detached.

Reconciliation events contain the base state the host changed from, the
observed target state, a host-neutral source, optional route, and reason.
Disposal detaches events and stops new work; asynchronous disposal waits for
accepted work and adapter cleanup. Adapters may also submit an explicitly
observed host state through `IRouterNavigator.ReconcileAsync` when
reconciliation does not originate from the presenter's event stream.

Reconciliations wait behind any operation already holding the navigation lock,
so every reconciliation names its `BaseState`: the committed router state the
host's change started from. The router applies it only while that exact
instance is still `CurrentState`. Otherwise it emits `ReconciliationRejected`
and throws `StaleNavigationReconciliationException` before calling the
presenter, leaving state and history unchanged. A queued event-path rejection
is reported only through that diagnostic.

`BaseState` is compared by reference. An adapter tracks the state the router
will hold when its reconciliation is admitted: the plan target of its last
successful non-reconcile presentation, then the target of each reconciliation
it raises, so consecutive native changes chain. That is not necessarily the
adapter's own view of native state. The MAUI presenter, for example, folds
suppressed native pops into its view before the router has committed them. A
rejected reconciliation needs no retry: the operation that won has already
presented its own target.

The MAUI branch-host extension follows the same boundary. An
`IMauiBranchHostFactory` declares placement capabilities before creation and
returns an `IMauiBranchHost` that owns its page, ordered branch presentation,
selection event, and reversible updates. Host-originated selection is the only
source of branch reconciliation; presenter-driven updates must be suppression
safe. Factory capability failures are preflight failures, while create/update,
rollback, and release failures participate in the adapter transaction and
recovery contract. A custom host must expose all branch pages needed for
structural verification and release, including inactive branches.

The public adapter-contract test assembly exercises successful apply,
failure/cancellation without commit, reconciliation, shutdown/event detachment,
and Stack/BranchHost/Modal topology. A future Blazor adapter should satisfy the
same contract while owning browser history, component mapping, lifecycle, and
storage rather than MAUI artifacts.

## Next steps

- Review [topology and planning](../concepts/02-topology-and-planning.md).
- Read the [entry-leases](entry-leases.md) design note.
- Use maintainer [testing](../maintainers/testing.md) to run adapter contracts.
- Handle [navigation outcomes and
  failures](../guides/04-navigation-outcomes-and-failure-handling.md).
- Diagnose host failures with [Diagnostics](../reference/diagnostics.md).

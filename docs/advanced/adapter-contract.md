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
- shutdown and presenter-event detachment orchestration.

## Adapter owns

- UI artifacts and mapping from `RouteEntry` to them;
- host lifecycle, thread affinity, and native event subscription;
- DI lifetime and release of UI scopes;
- native container mutation, rollback, verification, and recovery;
- native-to-logical reconciliation;
- platform ingress and storage choices.

That mapping is presentation policy, not route identity. Core does not require
a `RouteEntry` to equal one page or one view model. An adapter may use one or
several host artifacts, provided it can apply, verify, roll back, and reconcile
the logical target consistently. The built-in MAUI adapter uses one anchor
`Page` per entry and optionally lets that route own additional presentation
pages; another host can choose a different artifact model.

The presenter must leave logical state uncommitted on failure or cancellation.
Reconciliation events contain the observed target state, a host-neutral source,
optional route, and reason. Disposal detaches events and stops new work;
asynchronous disposal waits for accepted work and adapter cleanup. Adapters may
also submit an explicitly observed host state through
`IRouterNavigator.ReconcileAsync` when reconciliation does not originate from
the presenter's event stream.

The MAUI branch-host extension follows the same boundary. An
`IMauiBranchHostFactory` declares placement capabilities before creation and
returns an `IMauiBranchHost` that owns its page, ordered branch presentation,
selection event, and reversible updates. Host-originated selection is the only
source of branch reconciliation; presenter-driven updates must be suppression
safe. Factory capability failures are preflight failures, while create/update,
rollback, and release failures participate in the adapter transaction and
recovery contract. A custom host must expose all branch pages needed for
structural verification and release, including inactive branches.

## Cancellation across the native-tree boundary

AppNav separates durable logical navigation from disposable native presentation.
When a MAUI window is destroyed, the presenter closes the native-tree *epoch*
that owned its page tree: the logical `NavigationState` survives, but every
window and page reference from that epoch becomes unusable immediately.

Every call AppNav makes into application-supplied code -- `IMauiRoutePageLifecycleHook`,
`IMauiBranchHostFactory`, `IMauiBranchHost`, and `IMauiBranchHostUpdate` -- crosses
that boundary, so the two sides split as follows.

**AppNav guarantees:**

- Every extension point receives a `CancellationToken` that is cancelled when the
  owning native tree is destroyed, even when the calling operation is otherwise
  uncancellable. Recovery, rollback, and release paths are included; there is no
  path that hands application code a token which cannot cancel.
- After control returns from application code, AppNav revalidates the epoch before
  touching any native page, window, or host again.
- A result produced after the epoch closed -- a page, host, or update returned by a
  callback that ignored its token -- is disposed through the page-free abandonment
  path instead of being registered into the replacement tree. Shutdown waits for
  those leases to drain.
- Window destruction always completes. A callback that throws during teardown is
  recorded as a diagnostic; it cannot leave the presenter without a replacement
  epoch.

**Your code is responsible for:**

- Observing the supplied `CancellationToken`. A hook that ignores it will run to
  completion, and any work it performs on a page from a destroyed tree is
  unobservable to AppNav and may act on invalid native controls.
- Tolerating handler detachment and metadata reads that AppNav performs *before*
  invoking `DisposeAsync`. AppNav detaches its own event handlers and captures the
  metadata it needs first, so a host that self-disposes early is not asked to
  service AppNav afterwards.
- Making `RollbackAsync` safe to call on an update that was never committed;
  it is the documented reversal of `ApplyAsync`.

AppNav does not attempt to make application code that ignores cancellation safe.
It guarantees only that such code cannot corrupt the replacement tree or strand
the presenter.

The public adapter-contract test assembly exercises successful apply,
failure/cancellation without commit, reconciliation, shutdown/event detachment,
and Stack/BranchHost/Modal topology. A future Blazor adapter should satisfy the
same contract while owning browser history, component mapping, lifecycle, and
storage rather than MAUI artifacts.

## Next steps

- Review [topology and planning](../concepts/02-topology-and-planning.md).
- Use maintainer [testing](../maintainers/testing.md) to run adapter contracts.
- Handle [navigation outcomes and
  failures](../guides/04-navigation-outcomes-and-failure-handling.md).
- Diagnose host failures with [Diagnostics](../reference/diagnostics.md).

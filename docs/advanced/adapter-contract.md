# Adapter contract

[Documentation home](../index.md)

This is advanced material for adapter authors. Application integrations should
start with [Getting started](../guides/01-getting-started.md).

AppNav core is host-independent. An adapter implements `INavigationPresenter` using only public core APIs and is created
with the router through `RouterNavigatorFactory`.

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

The presenter must leave logical state uncommitted on failure or cancellation. Reconciliation events contain the
observed target state, a host-neutral source, optional route, and reason. Disposal detaches events and stops new work;
asynchronous disposal waits for accepted work and adapter cleanup.
Adapters may also submit an explicitly observed host state through `IRouterNavigator.ReconcileAsync` when reconciliation
does not originate from the presenter's event stream.

The public adapter-contract test assembly exercises successful apply, failure/cancellation without commit,
reconciliation, shutdown/event detachment, and Stack/BranchHost/Modal topology. A future Blazor adapter should satisfy
the same contract while owning browser history, component mapping, lifecycle, and storage rather than MAUI artifacts.

## Next steps

- Review [topology and planning](../concepts/02-topology-and-planning.md).
- Use maintainer [testing](../maintainers/testing.md) to run adapter contracts.
- Diagnose host failures with [Diagnostics](../reference/diagnostics.md).

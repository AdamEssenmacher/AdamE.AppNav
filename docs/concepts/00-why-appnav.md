# Why AppNav?

[Documentation home](../index.md)

Navigation often starts as a few page pushes. It becomes harder to reason about
when the same destination must also work from a tab, native Back, an app link, a
push notification, startup, auth recovery, or process restoration. Each entry
path can otherwise accumulate its own rules for constructing pages and fixing
the surrounding UI.

AppNav makes the destination—not the page operation—the common unit of
navigation.

```csharp
await navigator.NavigateAsync(new InventoryItemRoute(itemId));
```

In the fictional RPG Glyphmere, that route means “show this inventory item.” It
does not mean “select Inventory, push the Weapons page, then push an item page.”
The navigation model decides the required logical topology, and the MAUI
adapter presents it with native controls.

That separation allows an in-app tap, a shared URI, and a restored request to
resolve to the same semantic destination without requiring each caller to know
how the current UI is arranged.

## One navigation pipeline

```text
intent -> typed route or complete request -> transform/match -> policy
       -> logical plan -> native presentation -> commit/reconciliation
```

AppNav separates the concerns in that pipeline:

| Concern | AppNav approach |
| --- | --- |
| Destination identity | Typed routes describe durable domain destinations rather than pages or navigation commands |
| UI shape | A navigation model plans windows, stacks, independent branches, entries, and modals before native mutation |
| Entry context | Complete runtime requests preserve source, disposition, window, metadata, and provenance when those details matter |
| Presentation | A host adapter maps the logical plan to native controls and commits logical state only after presentation succeeds |
| Native user actions | Back gestures and branch changes reconcile into the same logical state and history |

The result is one place to define what a destination means and one predictable
pipeline for reaching it.

## Why plan topology explicitly?

A route such as `InventoryItemRoute(itemId)` can require more than one page. The
navigation model can select the Inventory branch and construct a valid stack.
During in-app exploration that stack might be
`Inventory -> Weapons -> Iron Sword`, and the Inventory branch can retain it
while the player briefly visits World Map.

Because this shape exists as host-independent state before MAUI changes the UI,
an application can:

- test destination and Back behavior without creating native pages;
- make deep links construct a complete, valid destination shape;
- preserve independent branch histories deliberately;
- reject invalid plans before partial native mutation;
- reason about presentation failure separately from committed navigation state.

## Why keep the core host-independent?

Routing, policy, planning, state, history, and orchestration do not inherently
belong to MAUI. Native pages, lifecycle callbacks, UI-thread rules, platform
links, and durable storage do.

AppNav keeps those platform concerns behind explicit host-facing abstractions.
For example, core defines the deferred-request store contract and replay
semantics, while `AdamE.AppNav.Maui` supplies a file-backed implementation.
Another adapter can use different UI and storage mechanisms without redefining
route identity or navigation policy.

This boundary does not make platform work disappear. The application still
owns Android and Apple domain association, provider SDK integrations, signing,
and app-specific security decisions. It keeps that work at a controlled edge
instead of spreading platform assumptions through the navigation model.

## When AppNav is a good fit

Consider AppNav when an application needs several of these together:

- typed destinations shared by in-app navigation and external entry points;
- native stacks, independent branches, modals, and predictable Back behavior;
- canonical navigation that can rebuild a valid UI from any current state;
- request transformation, access policy, redirect, or deferred replay;
- host-independent tests for navigation decisions;
- explicit diagnostics and failure behavior around native presentation.

## When it may not be the right fit

AppNav adds an explicit route and topology model. That cost may not be justified
for a small application whose navigation is adequately expressed by a few
direct page operations.

The current preview is also not a fit when an application requires Windows
MAUI, Shell or Prism integration, true multi-window MAUI presentation, a
transition/shared-element system, a production Blazor adapter, NuGet.org
availability, or stable `1.0` compatibility guarantees.

AppNav does not replace pages, view models, dependency injection, authentication
logic, platform link association, or provider SDKs. It coordinates navigation
around those application and platform responsibilities.

## Next steps

- Learn how destinations are represented in
  [Routing and metadata](01-routing-and-metadata.md).
- See how routes become native UI shapes in
  [Topology and planning](02-topology-and-planning.md).
- Review the current [preview release notes](../release-notes/index.md) before
  adopting AppNav.

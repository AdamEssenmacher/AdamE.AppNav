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

## A route is neither a page nor a view model

A route identifies application meaning; pages and view models are possible
ways to present and interact with that meaning. `InventoryItemRoute(itemId)`
still means “show this inventory item” whether Glyphmere renders it as:

- one MAUI page configured for that item;
- a native segment containing an overview page and a comparison page;
- a custom control or modal within a larger host surface; or
- different UI artifacts in another renderer.

The application and its host adapter own that mapping. AppNav core plans a
logical `RouteEntry`, not a page instance. The built-in MAUI adapter maps each
route entry to an anchor `Page`, because that is how it participates in native
MAUI navigation. That page can render route-specific state and can own
additional [presentation pages](../advanced/route-owned-presentation-pages.md).
A different adapter can map the same logical entry to different host artifacts.

A view model is not destination identity either. A view model may request an
`InventoryItemRoute`, consume that route while presenting it, or be one of
several view models involved in the destination. Its type and lifetime remain
presentation choices. Treating a view-model type as the route makes those
choices part of navigation identity and creates the same one-to-one assumption
as page routing.

Using page or view-model types as destination keys is convenient while every
destination maps neatly to one UI object. AppNav deliberately does not make
that convenience a universal constraint; the distinction matters when a
destination spans several pages, changes presentation, enters through a URI,
or must be shared across renderers.

## Established ideas, adapted for .NET

AppNav does not claim that data-driven destinations, explicit navigation
state, or separating navigation meaning from rendered UI are new ideas. Mature
UI ecosystems have converged on related concepts:

| Ecosystem | Related idea |
| --- | --- |
| SwiftUI | A `NavigationPath` stores data values, and `navigationDestination` maps a presented data type to a destination view. Codable path values can participate in restoration. See Apple's [navigation stack](https://developer.apple.com/documentation/swiftui/understanding-the-navigation-stack) documentation. |
| Android Compose | Jetpack Navigation supports serializable typed route objects distinct from the composable that displays a destination. Newer Navigation 3 APIs model the back stack as application-owned keys resolved to content and can display more than one destination in adaptive layouts. See Android's [navigation graph](https://developer.android.com/guide/navigation/design) and [Navigation 3](https://developer.android.com/guide/navigation/navigation-3) documentation. |
| React Navigation | Navigation is represented as explicit, potentially nested state with routes and per-navigator history; linking translates URLs into that model, and partial state can be rehydrated. See its [navigation state](https://reactnavigation.org/docs/navigation-state/) and [linking](https://reactnavigation.org/docs/configuring-links/) documentation. |

These libraries use different terminology, constraints, and rendering models.
They are precedents for the general direction, not claims of API equivalence or
drop-in compatibility.

AppNav's purpose is to bring a coherent version of those proven ideas to .NET
application code and native MAUI presentation: typed semantic routes,
host-independent topology and policy, explicit request provenance, and
transactional presentation with native reconciliation. The combination and
its tradeoffs matter more than claiming novelty for the individual concepts.

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

## Keep navigation decisions out of the renderer

Because `AdamE.AppNav` targets plain `net10.0`, routes, route-owned state,
policies, topology planning, Back behavior, and view models that request typed
destinations can live in a render-independent application project. MAUI pages
and the `AdamE.AppNav.Maui` adapter remain in the outer host.

One application might call its inner layers `Presentation -> Application ->
Domain`. Those names are illustrative, not required by AppNav. The important
dependency direction is that the outer renderer depends on the inner
navigation decisions; the inner projects do not require MAUI or a native target
framework.

```text
MAUI host ------\
Blazor host -----+---> render-independent Presentation -> Application -> Domain
Avalonia host --/
                         |
                         +---> AdamE.AppNav (net10.0)
```

This can let multiple renderers share navigation intent, but renderer swapping
is not required to benefit. Ordinary .NET tests can exercise typed destination
requests from presentation logic, routing, policy, topology, history, and Back
without initializing MAUI, a simulator, a device, or a UI thread. Focused host
tests remain for native presentation, lifecycle, platform ingress, and storage
implementations.

AppNav currently ships only a production MAUI adapter. Blazor and Avalonia in
the diagram illustrate the boundary another adapter could use; they are not
included integrations. See [Application architecture and
testing](../guides/03-application-architecture-and-testing.md) for a concrete
Glyphmere project boundary and testing strategy.

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

- Use the [Glossary](../reference/glossary.md) for precise definitions of the
  concepts introduced here.
- Learn how destinations are represented in
  [Routing and metadata](01-routing-and-metadata.md).
- See how routes become native UI shapes in
  [Topology and planning](02-topology-and-planning.md).
- Keep application navigation render-independent with
  [Application architecture and testing](../guides/03-application-architecture-and-testing.md).
- Review the current [preview release notes](../release-notes/index.md) before
  adopting AppNav.

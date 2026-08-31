# Glossary

[Documentation home](../index.md)

AppNav uses route, request, state, plan, and presentation as separate concepts.
Keeping those boundaries precise makes the API easier to reason about—most
importantly, a route is neither a page nor a view model.

API type names appear in `code`. General architectural terms appear in plain
language. Links lead to the guide that develops each concept.

## Essential distinctions

| Do not conflate | Distinction |
| --- | --- |
| Route and page | A route identifies where the user wants to go; a page is one possible MAUI rendering artifact. One route can use several pages, a control, a modal, or one page in a particular state. |
| Route and view model | A route is destination identity. A view model can request or consume a route but remains replaceable presentation logic. |
| Route and route entry | A route describes a destination; a `RouteEntry` is one occurrence of that route in the logical navigation tree, with a stable entry ID and route-owned metadata. |
| Route and request | A route describes the destination. A request adds the runtime circumstances under which navigation should occur. |
| Navigation state and UI tree | `NavigationState` is AppNav's logical topology. It does not contain MAUI controls, pages, view models, or arbitrary control state. |
| Plan and presentation | A `NavigationPlan` describes the target logical state. A presenter decides how to reflect that plan in its host UI. |
| Canonical URI and canonical navigation | A canonical URI is stable route serialization. Canonical navigation is a disposition that constructs the route's declared topology. Neither implies the other. |
| Request source and provenance | Source is AppNav's broad ingress category. Provenance is optional provider-specific context about how that request arrived. |
| Accepted for dispatch and navigated | A queue can accept an external request before navigation runs. Only the awaited navigation or boundary-specific completion outcome says what eventually happened. |
| Diagnostics and outcomes | Diagnostics describe execution. The returned result, unhandled Back value, boundary result, cancellation, or exception is authoritative. |

## Terms

### Adapter

Host-specific code that translates between AppNav's logical contracts and a UI
framework. `AdamE.AppNav.Maui` is the only production adapter in this preview.
An adapter owns native presentation, host events, lifecycle integration, and
platform-specific implementations of core ports. See the [adapter
contract](../advanced/adapter-contract.md).

### `AppRoute`

The base type for a typed semantic destination. A concrete route should carry
durable destination identity, such as `InventoryItemRoute(itemId)`, rather
than page operations, page types, view-model types, or ingress context. See
[Routing and metadata](../concepts/01-routing-and-metadata.md).

### `AppRouteRequest`

An app-facing pair of one typed `AppRoute` and route-owned metadata. Use it
when in-app code needs metadata whose lifetime is canonical, restorable, or
ephemeral. It is narrower than `RouterNavigationRequest`.

### Back

A request to move backward through logical navigation. AppNav considers modal
content, modal dismissal, stack entries, and configured branch fallback.
`BackNavigationResult.Handled == false` is a normal result that lets the host
apply its root-level Back behavior. See [Navigation outcomes and failure
handling](../guides/04-navigation-outcomes-and-failure-handling.md).

### Branch

One independently retained navigation tree inside a `BranchHostNode`.
Glyphmere's Inventory and World Map branches can each keep their own stack
while only one is selected.

### Branch host

A logical node that owns multiple branches, one selected branch, and an
optional default branch. A MAUI adapter may render it with tabs or another
native selection control, but the logical concept does not require a specific
control.

### Canonical metadata

Route-owned metadata declared with `RouteStateLifetime.Canonical`. AppNav may
format it into the canonical URI when the route maps that key with
`AppNavQueryMetadata`. It consequently participates in sharing, comparison,
and persistence through that URI.

### Canonical navigation

Navigation with `RouterNavigationDisposition.Canonical`. The planner builds
the destination's declared topology rather than first attempting to mutate a
compatible current stack. Inactive branches are sanitized to their configured
roots. See [Topology and planning](../concepts/02-topology-and-planning.md).

### Canonical surface

The app-defined logical window and root stack or branch-host IDs configured by
`CanonicalSurface`. These stable structural IDs identify where a standard
navigation model constructs canonical topology; they are not page names.

### Canonical URI

The stable URI representation produced from a route's attributed template,
route identity, query-bound route properties, and mapped canonical metadata.
Restorable and ephemeral metadata are not included merely because they exist
on an `AppRouteRequest`.

### Commit

Publishing a successfully presented target as the router's current logical
state and recording its history entry. AppNav commits after presentation
succeeds. A failure or cancellation before that point does not publish the
target logical state.

### Contextual navigation

Navigation that first tries to push or replace within a compatible current
stack. If that is not possible, the standard models fall back to the route's
canonical topology. `Auto` chooses contextual behavior for in-app and test
sources and canonical behavior for external sources.

### Deferred navigation

A navigation request retained for later replay, commonly across an
authentication flow or process restart. The store persists a deliberately
limited snapshot, not the provider payload, page tree, or application data.
See [Deferred navigation](../guides/06-deferred-navigation.md).

### Diagnostic event

A structured observation emitted during a navigation phase. The same event
can feed `ILogger`, in-process observers, and tracing. Diagnostic events never
replace the operation's result or exception. See [Logging, tracing, and
diagnostics](diagnostics.md).

### Disposition

The request's instruction for how planning should relate the destination to
current topology. `RouterNavigationDisposition` supports `Auto`,
`Contextual`, `ReplaceCurrent`, and `Canonical`.

### Ephemeral metadata

Route-owned metadata declared with `RouteStateLifetime.Ephemeral`. It exists
only in live navigation state. It is omitted from canonical formatting and
configured persistence—for example, a one-time highlight or animation hint.

### External navigation

Navigation initiated outside ordinary in-app interaction, such as an app link,
push notification, QR scan, or provider callback. It is an ingress and security
boundary: the app owns platform registration and provider integration, while
AppNav validates configured trusted origins for its built-in MAUI app-link
path. See [External navigation](../guides/05-external-navigation.md).

### Generated module

Source-generated registration code. The core generator emits a route-table
module; the MAUI generator emits a route-to-page module. Applications register
those modules at their composition root rather than manually duplicating the
generated mappings.

### History

The bounded sequence of committed navigation entries maintained by the
router. History is logical AppNav state used by navigation behavior; it is not
the same as a browser history, operating-system task history, or a raw list of
MAUI pages.

### Host

The outer environment that renders and owns visible UI, such as the MAUI
application. A host supplies an adapter and platform services while depending
inward on the host-independent core. See [Application architecture and
testing](../guides/03-application-architecture-and-testing.md).

### Logical window

A `WindowNode` and its app-defined ID in `NavigationState`. Core models retain
this abstraction independently of native window objects. The preview MAUI
presenter supports one logical window, whose ID must match the attached MAUI
window configuration.

### Metadata

Additional typed or untyped values associated with navigation. The owner
matters:

- route properties are durable destination identity;
- `AppRouteRequest` metadata belongs to one route occurrence and has a declared
  canonical, restorable, or ephemeral lifetime;
- `RouterNavigationRequest.Metadata` is runtime request context and is distinct
  from route-owned metadata;
- provenance attributes describe provider context and are neither route state
  nor a place for secrets.

### Navigation model

An `INavigationModel<TRoute>` that declares how semantic routes map to logical
topology. Standard stack and branch-host models define canonical recipes plus
contextual push or replacement behavior. A model plans structure; it does not
construct pages.

### Navigation plan

A `NavigationPlan` containing the complete target `NavigationState`, a plan
kind, and an optional reason. The router validates a plan before asking the
presenter to apply it.

### Navigation result

A `NavigationResult` returned after successful navigation or reconciliation.
It contains the final accepted route, applied plan, committed state, and a
`Presented` flag. It is not a success/failure union; failures generally throw.

### Navigation state

The immutable logical tree described by `NavigationState`: windows, root
nodes, independent branches, stacks, route entries, and modals. It is the
router's model of navigation structure, not the complete rendered UI tree.

### Operation ID

An identifier AppNav creates for one router or startup operation so its
diagnostic events and trace activity can be correlated. It is distinct from a
request provenance `CorrelationId`, which an app or external provider may
supply and which can survive across system boundaries.

### Page

A MAUI rendering artifact owned by the outer host. The built-in adapter maps a
logical route entry to an anchor `Page`, but a route can also own additional
presentation pages or be rendered by that page in a particular state. Page
type is not route identity.

### Planner

Code that turns a resolved semantic route plus current navigation state into a
validated `NavigationPlan`. `NavigationModelPlanner<TRoute>` applies the
standard navigation models; applications can use `IAppNavigationPlanner` for
coordination or domain-specific planning.

### Policy

An `INavigationRequestPolicy` evaluated after route matching and before
planning. A policy can preserve a request, redirect it, or—in the access-gate
policy—defer the original request and redirect. A policy decides whether and
where navigation may proceed; it does not present UI.

### Presenter

An `INavigationPresenter` implementation that applies a logical plan to the
host's visible navigation surface and reports host-originated changes for
reconciliation. It is an adapter seam, not a render-independent view model or
the application's Presentation layer.

### Presentation

The adapter's process of reflecting an accepted logical plan in native UI.
For MAUI this can include selecting branches, pushing or popping pages, and
showing or dismissing modals. Successful presentation precedes logical commit.

### Presentation page

An additional native page owned by one logical route after its anchor page has
been presented. It can support route-local subnavigation without creating a
new semantic route or `RouteEntry`. This is advanced MAUI behavior; see
[Route-owned presentation pages](../advanced/route-owned-presentation-pages.md).

### Provenance

Optional `NavigationRequestProvenance` describing how a complete request
entered the router: provider, original or referrer URI, correlation ID,
cold-start knowledge, and stable provider attributes. Provenance is runtime
context, not destination identity or route-owned state. See [Requests and
provenance](../concepts/03-requests-and-provenance.md).

### Reconciliation

Updating logical AppNav state after the host reports a completed native change
that did not originate as a new router command, such as native Back or branch
selection. Cancelled native gestures do not reconcile committed state.

### Redirect

A transformer or policy replacing the request target before planning. AppNav
restarts resolution for the new target, limits redirect depth, and rejects
loops. A successful operation returns the final redirected route.

### Request source

The `NavigationRequestSource` category describing the kind of caller, such as
an in-app command, app link, push, QR code, restore, test, or host
reconciliation. Source influences `Auto` disposition and is broader than
provider-specific provenance.

### Restorable metadata

Route-owned metadata declared with `RouteStateLifetime.Restorable`. It is
eligible for persistence when the app configures a store with the corresponding
`RouteStateRegistry`; the lifetime does not itself save anything. The outer
host supplies the platform-specific storage implementation. Restorable values
are omitted from canonical URIs; model a shareable value as canonical instead.

### Route

See [`AppRoute`](#approute). In prose, route means a typed semantic
destination—not a URI string, page registration, view-model type, or command
to manipulate native navigation.

### Route definition

A route template plus the logic needed to create and format its concrete
`AppRoute`. Definitions are collected in a `RouteTable`, usually through a
generated module.

### Route entry

One `AppRoute` occurrence in a logical stack or modal. A `RouteEntry` carries
a stable presenter-reuse ID and optional route-owned metadata. Two entries can
refer to equal routes while remaining distinct occurrences.

### Route table

The ordered collection used to match URIs to typed routes and format routes as
canonical URIs. It contains generated and explicitly registered route
definitions, constraints, and codecs.

### Route template

The attributed URI path pattern associated with a concrete route, such as
`/pause/inventory/items/{itemId:guid}`. Parameters bind to route properties;
constraints restrict and convert accepted values. Query binding is declared
separately.

### Router

The host-independent runtime that resolves requests, invokes planning and
presentation, commits state and history, handles logical Back, and coordinates
reconciliation. `IRouterNavigator` is its primary navigation surface.

### `RouterNavigationRequest`

The complete runtime envelope used when source, target, disposition, timestamp,
window, request metadata, or provenance must be explicit. It contains exactly
one URI or typed-route target. Ordinary in-app code should prefer `AppRoute` or
`AppRouteRequest` unless it truly owns that full context.

### Semantic destination

A description of where the user wants to go in application terms, independent
of the UI operations needed to render it. “Show inventory item 42” is semantic;
“select tab 1 and push `ItemPage`” is presentation procedure.

### Stack

A `StackNode` containing ordered `RouteEntry` values from root to top. It is a
logical navigation structure. A MAUI adapter commonly maps it to a native
navigation stack, but the core does not store native pages.

### Topology

The shape of the logical navigation tree: windows, roots, branch hosts,
branches, stacks, route entries, and modals. Routes express destination
meaning; topology expresses the structure required to reach and retain it.

### Transformer

An `INavigationRequestTransformer` evaluated before route matching. It can
normalize or replace a request target while preserving its runtime context—for
example, rewriting a legacy Glyphmere URI to its canonical form.

## Related reading

- Start with [Why AppNav?](../concepts/00-why-appnav.md) for the architectural
  motivation behind these distinctions.
- Follow [Routing and metadata](../concepts/01-routing-and-metadata.md),
  [Topology and planning](../concepts/02-topology-and-planning.md), and
  [Requests and provenance](../concepts/03-requests-and-provenance.md) for the
  complete conceptual model.
- Use [Navigation outcomes and failure
  handling](../guides/04-navigation-outcomes-and-failure-handling.md) for
  completion semantics.

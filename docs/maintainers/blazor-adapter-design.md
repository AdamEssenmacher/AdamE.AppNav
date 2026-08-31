# Blazor adapter design baseline

[Documentation home](../index.md)

This document records the accepted design baseline for adding
`AdamE.AppNav.Blazor`. It is a maintainer implementation guide, not consumer
documentation. The feasibility slice described below must validate the browser
history and render-lifecycle assumptions before the proposed public APIs are
stabilized.

This baseline has been checked against the official ASP.NET Core 10 Blazor
documentation. Where Blazor behavior depends on its built-in `Router`, this
document treats equivalent AppNav behavior as work to implement explicitly,
not as behavior inherited automatically from the framework.

## Outcome

AppNav can support interactive Blazor without moving browser, component, or
ASP.NET Core dependencies into `AdamE.AppNav`. Core remains responsible for
semantic routes, request processing, policy, planning, logical state, the
operation journal, diagnostics, serialization of router operations, and commit
after successful presentation.

The Blazor adapter owns component mapping, render coordination, browser URL and
history integration, browser snapshot storage, focus and scroll behavior, and
hosting lifecycle. ASP.NET Core endpoint and prerender integration lives in a
separate optional server package.

Clean long-term contracts take precedence over strict compatibility with the
preview presenter API. Any core contract change must update the MAUI adapter,
public API baselines, adapter-contract tests, and release notes in the same
change.

## Supported v1 scope

- .NET 10 only, with trimming and WebAssembly AOT compatibility required.
- Interactive Server and Interactive WebAssembly.
- Both standalone Blazor WebAssembly and Blazor Web App
  `InteractiveWebAssembly` hosting.
- Destination prerendering and state handoff where a server host is present.
- Exactly one presentation-owning `AppNavOutlet` and one `WindowNode` per
  scoped navigator.
- All existing single-window shapes: stacks, branch hosts, nested content, and
  modals.
- Chromium, Firefox, and WebKit verification before release.

The following are excluded from v1:

- static-SSR-only navigation;
- Blazor Hybrid;
- Interactive Auto;
- mixed per-route render modes within one outlet;
- multiple logical windows in one navigator;
- first-class lazy route-assembly loading;
- automatic route or data prefetch;
- a built-in visual-transition or animation system;
- component-specific leave guards;
- cross-tab navigation synchronization.

The following Blazor `Router`- or endpoint-dependent conveniences are also
explicitly deferred unless a later section says otherwise:

- automatic `[SupplyParameterFromQuery]` route binding;
- enhanced forms, static SSR form posts, and `[SupplyParameterFromForm]`;
- Blazor `Router` lazy assembly discovery and its built-in `Navigating` UI;
- automatic `FocusOnNavigate` behavior (AppNav supplies its own focus policy).

Ordinary interactive `EditForm` usage inside an AppNav destination remains
supported. These deferrals reserve future integrations; they do not prohibit
applications from implementing equivalent behavior themselves.

Each browser tab or window owns an independent navigator. A duplicated tab may
adopt the copied snapshot as a startup hint, but it creates a new adapter
instance identifier and immediately diverges from the original tab.

## Package layout

| Package | Responsibility |
| --- | --- |
| `AdamE.AppNav` | Host-neutral routing, policy, planning, state, journal, restoration orchestration, and presenter contract |
| `AdamE.AppNav.Blazor` | Portable Razor components, presenter, outlet, component registry, DI integration, browser history/storage module, and client lifecycle |
| `AdamE.AppNav.Blazor.Server` | ASP.NET Core catch-all endpoint, prerendering, HTTP outcome, and interactive handoff integration |
| `AdamE.AppNav.Blazor.Generators` | `netstandard2.0` component-mapping generator and diagnostics |

The portable package must be usable by standalone WebAssembly without taking a
server-only ASP.NET Core dependency. The generator is packaged transitively
from `AdamE.AppNav.Blazor` in the same manner as the MAUI generator.

All production packages share the core/MAUI package version and release train.
The Blazor packages remain internal, unpacked, and unpublished during the
feasibility phase; they join release artifacts only when they are ready to ship
at the train's quality level and every required gate passes. Release tooling
must be generalized from its current two-package assumptions before that point.

## Render-mode boundary

`AppNavRoot` (the public name may ultimately remain `AppNavOutlet`) is the
highest interactive component in the AppNav-owned subtree. The outlet,
destination components, layouts, branch/modal hosts, pending UI, and error UI
all execute in that one interactive subtree and use the same render mode.
`HeadOutlet` must be configured compatibly with that render mode.

No public parameter crossing from a static parent into this interactive root
may require a non-JSON-serializable value. In particular, the root does not
accept `RenderFragment`, component `Type`, delegates, callbacks, service
instances, or arbitrary descriptor objects as parameters across that boundary.
Customization is registered through DI or described by serializable keys and
resolved inside the interactive subtree. Local wrapper components may expose
fragments when the wrapper itself is already interactive.

For prerendered Interactive WebAssembly, mapped components and all of their
client-executed dependencies live in the client project or a client-compatible
library. Services needed by a prerendered component must be registered in both
the server and client hosts, or the component must tolerate their absence
during prerender. The design must not assume browser APIs are available until
interactive rendering begins.

The adapter detects capabilities from Blazor runtime services and renderer
information. It does not expose a closed AppNav-specific `Server`/`WebAssembly`
render-mode enum that would make future framework render modes a breaking API
change.

## Third-party component libraries

General-purpose Blazor component libraries are supported inside AppNav
destinations and layouts. They do not need an AppNav-specific component model.
MudBlazor is the representative V1 compatibility target because it exercises
theme and overlay providers, JavaScript interop, dialogs, focus management,
layouts, and render-mode-sensitive setup. This is a compatibility test and
sample dependency only; no production AppNav package references MudBlazor or
exposes MudBlazor types.

MudBlazor's required theme and popover providers, plus its optional dialog and
snackbar providers, must be instantiated once inside the same interactive
subtree as the components that consume them. The sample places them in an
interactive application shell above the AppNav outlet. It does not pass theme
objects, provider component types, callbacks, or fragments across the static-
to-interactive root. Prerendered Interactive WebAssembly registers required
MudBlazor services in both server and client hosts. This follows MudBlazor's
[render-mode and provider guidance](https://www.mudblazor.com/getting-started/installation).

MudBlazor dialogs, menus, popovers, and snackbars are transient UI overlays,
not AppNav topology. They are not persisted, restored, policy evaluated, or
represented in browser history, and browser/logical Back does not promise to
dismiss them. An application that wants a destination to participate in those
semantics uses an AppNav modal route and may render MudBlazor content inside it.
A future overlay bridge may add coordinated dismissal behavior without changing
the core route or modal contracts.

The compatibility contract requires:

- ordinary canonical anchors and non-forced `NavigationManager` calls to enter
  AppNav navigation, while forced loads retain normal browser behavior;
- AppNav's default focus policy to yield when an active overlay or application
  code has already claimed focus after rendering;
- route-lifetime cancellation and disposal to leave no orphaned destination-
  owned overlays, callbacks, or JavaScript resources;
- AppNav modal focus containment to allow registered external overlay roots,
  because provider-rendered MudBlazor popovers are not DOM descendants of the
  modal, and to have testable Escape-key, backdrop, scroll-lock, and stacking
  behavior when nested;
- MudBlazor static assets and JavaScript to coexist with the AppNav Razor Class
  Library module under base-path hosting and the hosting mode's CSP; and
- covered or inactive destinations to retain no implicit component-local state
  merely because a MudBlazor component rendered it.

Third-party providers may observe `NavigationManager.LocationChanged` and
dismiss overlays before an AppNav transaction ultimately commits. V1 does not
promise that a dialog or other transient overlay survives a failed navigation
and recovery. The representative sample pins one MudBlazor version and records
that version's CSP requirements separately from AppNav's own script and asset
guarantees; dynamically generated inline styles required by MudBlazor are not
an AppNav CSP defect.

AppNav does not claim compatibility with third-party components that own a
`Router`/`RouteView`, require Blazor `RouteData`, write browser history directly,
or assume that every destination has `@page`. Those integrations require an
explicit bridge and remain outside V1.

## Navigation ownership

AppNav is the exclusive navigation authority for the surface owned by
`AppNavOutlet`. It replaces Blazor's `Router` for that surface. `NavigationManager`
and the browser History API are host transports, not competing route planners.

After its first interactive render, the outlet calls
`INavigationInterception.EnableNavigationInterceptionAsync`. That public Blazor
hook enables ordinary-anchor interception, programmatic client-side navigation,
and location-changing notification for browser traversal while causing
enhanced-navigation click handling to stand down. The call is guarded because
navigation interception is unavailable during static rendering and prerender.
No interactive `NavigateTo`, startup-entry adoption, or canonicalizing history
mutation may occur until interception has completed.

Interception is document-wide, while AppNav ownership is limited to its
configured base path. Eligible unmodified same-origin navigation beneath that
path enters AppNav. External origins, downloads, modified clicks, links with
another target, forced loads, and paths outside the owned path retain native
behavior; an intercepted path outside AppNav ownership is deliberately resumed
as a full document load. Unmatched owned paths enter the AppNav pipeline so the
configured fallback can produce a not-found route.

AppNav links always render real canonical `href` values. The package provides a
typed `AppNavLink`, including active state, route metadata, replacement options,
and an optional fragment, while ordinary eligible anchors work through the same
interception boundary.

Blazor's interception and `RegisterLocationChangingHandler` own ordinary links;
the adapter does not replace them with a second general click interceptor. Its
isolated JavaScript bridge handles only gaps such as stamped fragment entries,
scope-boundary fallback, storage, and focus/scroll capture. Self-issued history
egress is marked and classified before a location-changing callback can call
the router, preventing double handling and reentrancy. Before interactivity is
available, real `href` values retain progressive behavior: Server hosts use the
normal HTTP pipeline and standalone WebAssembly reloads and starts from the
requested URL. Standalone deployment must rewrite owned deep links to
`index.html`.

If the isolated bridge asset is blocked by CSP, unavailable, or fails to
initialize, Blazor-owned ordinary links and programmatic navigation continue to
work. Bridge-only capabilities degrade independently: fragment stamping may be
adopted after the fact, storage falls back to URL-only reconstruction, and
scope fallback uses a full document load. Each degradation emits a structural
diagnostic rather than failing an otherwise valid semantic navigation.

Mapped destination components do not require Blazor `@page` directives. AppNav
route definitions remain the single destination route table. The generator
warns when a mapped destination also declares `@page`.

The Server package initially uses an infrastructure component with a
`RouteAttribute`/`@page` catch-all as a route-pattern and endpoint-metadata
carrier. Razor Components endpoints render the consumer's configured root
component, not the component that contributed the route attribute. The
consumer root therefore owns prerender URL resolution, access evaluation, HTTP
outcomes, and the JSON-serializable approved handoff to interactive
`AppNavRoot`.

The host adds the Server assembly through `AddAdditionalAssemblies`. The route
is scoped beneath the configured base path, uses a `nonfile` catch-all, and is
restricted to GET/HEAD so static SSR form posts remain deferred. Endpoint
selection relies on route-template precedence at the framework's fixed Razor
Components endpoint order; an application fallback beneath the same owned path
is shadowed and must be documented. A convenience such as `MapAppNavCatchAll`
remains internal until the feasibility slice proves these mechanics or replaces
the route-carrier approach. Initial HTTP canonicalization uses an HTTP redirect
before response streaming starts; interactive canonicalization replaces the
current browser entry.

## Browser history model

Core `NavigationHistory` is an operation journal. It does not attempt to mirror
the browser's Back/Forward list, which may contain external documents, missing
AppNav state, and entries owned by other application versions.

The Blazor adapter owns the browser timeline and correlates its entries with
AppNav operations. The core presentation contract must carry explicit history
intent; the adapter must not infer browser behavior from planning dispositions
such as `Canonical` or `Auto`.

Normal URI and history-state changes use Blazor's supported navigation surface:
`NavigationManager.NavigateTo` with `NavigationOptions.ReplaceHistoryEntry` and
`HistoryEntryState`. Browser ingress is observed through
`RegisterLocationChangingHandler`, including its target location, entry state,
interception flag, cancellation token, and prevention mechanism after navigation
interception is enabled. Direct JavaScript is limited to capabilities Blazor
doesn't expose, such as session storage, atomic fragment stamping, scope fallback,
and scroll/focus capture. The adapter must not separately overwrite
`history.state` after a Blazor navigation unless the vertical slice proves that
doing so preserves framework state and behavior.

Location-changing callbacks may overlap, and multiple registered handlers have
no useful ordering contract. AppNav registers one coordinator, treats callback
state as request-local, honors cancellation, and never relies on another
handler running first. Blazor may temporarily revert and replay a navigation
while asynchronous handlers run; tests must verify that this does not create
extra AppNav journal entries or browser snapshots.

The coordinator classifies self-issued history egress before any router call and
returns from its location-changing handler without awaiting the in-flight router
operation. New browser ingress during presentation calls `PreventNavigation`,
signals supersession out of band, and replays only the latest intent after the
current operation releases. An exception from the handler is never used as a
decline mechanism.

| Router or host operation | Browser behavior |
| --- | --- |
| Normal `NavigateAsync` | Push a new entry |
| `ReplaceCurrent` navigation | Replace the current entry |
| Initial address-bar startup | Adopt and replace the existing entry; never push a duplicate |
| Browser Back/Forward restoration | Preserve the timeline; do not push or replace unless policy redirects or canonicalizes |
| Fragment-only navigation | Push a stamped browser presentation entry that references the unchanged topology |
| Logical `BackAsync` | Replace the current entry with the logical parent |

Logical Back therefore remains topology-based. Browser Back and Forward remain
chronological. V1 deliberately does not maintain a shadow browser timeline or
use conditional `history.go` traversal for logical Back. This can leave adjacent
entries with the same canonical route, but avoids claiming knowledge of browser
entries the platform does not expose. Correlated traversal may be added later as
an adapter capability without changing core logical-Back semantics.

Every successful semantic-route restoration is appended to the core operation
journal with history-traversal provenance. Fragment-only presentation entries
and traversal among them remain adapter history and diagnostics because they do
not change semantic route state. The journal remains bounded and chronological;
repeated semantic Back/Forward traversal is recorded and may evict older
operations. A browser traversal that supersedes an
uncommitted presentation cancels or fails that presentation before core commit,
then processes the latest browser location.

Browser-originated ingress is latest-intent-wins: rapid link or traversal
events cancel older uncommitted browser requests. Explicit application calls to
`IRouterNavigator` retain core's serialized behavior.

## Required core evolution

The feasibility slice should establish final names and shapes for these
concepts before public API approval:

1. **Explicit presentation history intent.** Presentation context must use
   host-neutral intents such as append, replace, preserve, and logical back.
   The semantics remain meaningful to non-browser adapters, which may ignore
   unsupported host-history behavior.
2. **Policy-aware restoration.** Add a restoration operation distinct from
   `ReconcileAsync`. Reconciliation remains a trusted report of already
   observed host state, such as a MAUI native pop. Restoration accepts a
   host-neutral request plus an optional untrusted candidate state, evaluates
   every candidate route through a side-effect-free access evaluator, validates
   the candidate, then restores or replans. It must not replay request policies
   that defer or enqueue requests. Core contracts must not mention browser
   schemas, storage keys, JavaScript, or `sessionStorage`.
3. **Policy-aware visibility transitions.** Before Back, modal dismissal,
   branch fallback, restoration, or any other topology-only operation exposes
   a route, core evaluates the same side-effect-free access invariant. It runs
   before the optional `IBackNavigationPolicy` chain. Access denial without a
   redirect leaves state unchanged and reports a canceled operation; access
   redirect becomes a separately planned navigation and does not run Back
   policies for the denied candidate. `IBackNavigationPolicy` remains an
   application guard over an already constructed Back plan, not authorization.
   `NavigationAccessDecision` therefore needs an explicit deny-without-redirect
   outcome.
4. **Observed-host reconciliation.** A native transition reported after the
   host has already exposed a route cannot be prevented by the visibility gate.
   Core accepts the observed state, evaluates access immediately, and replans or
   redirects without treating reconciliation as prior authorization. Server-
   side data authorization remains mandatory because presentation controls
   cannot erase an exposure that already occurred.
5. **Branch selection.** Add a host-neutral branch-selection operation that
   changes `SelectedBranchId` in the existing topology, preserves every branch
   stack, evaluates the newly visible route, and presents with explicit history
   intent. It must not simulate selection by navigating to the branch's top
   route through contextual planning.
6. **Navigation during presentation.** Define deferred/superseding navigation
   requested while a presenter callout is in flight. Lifecycle-initiated router
   calls are queued behind the current operation and may supersede it; the design
   does not suppress `ExecutionContext` to bypass reentrancy detection. Browser
   callbacks never await the locked router operation, and self-issued history
   callbacks bypass router ingress.
7. **Accurate request sources.** Add explicit address-bar and host-history-
   traversal sources, plus diagnostic/reconciliation vocabulary that does not
   misclassify Forward as Back. Retired numeric value 5 is never reused because
   request sources are persisted numerically; new values are appended at 8 or
   above. Existing host-Back journal entries must use their host provenance
   rather than the currently hard-coded `InAppCommand` source.
8. **Ambient query ownership.** Declared route parameters are AppNav-owned.
   Undeclared query parameters are ambient: inbound canonicalization and same-
   route browser updates preserve them, while typed navigation to another route
   drops them unless the Blazor caller explicitly requests preservation. An
   ambient-only change is browser presentation state, not a core journal entry.
9. **Journal semantics.** Document `NavigationHistory` explicitly as a bounded
   successful-operation journal rather than a host history cursor.
10. **Presentation completion semantics.** Define success as accepted host
   presentation through its observable transaction boundary, not a promise
   that a UI artifact can never fail later.

A restoration candidate wins over replanning only when all of the following
are true:

1. The entry URL resolves and passes current transformation and access policy.
2. Every route URI stored anywhere in the candidate resolves to its expected
   route type, is already canonical, and passes current access policy in a
   restoration-candidate context. This includes covered stack entries, inactive
   branches, modal owners and content, and nested routes.
3. The candidate's application/route-table compatibility fingerprint matches
   the current host. A mismatch is stale deployment state, not a policy failure
   or evidence of tampering.
4. The candidate state passes core structural validation.
5. The candidate's visibly presented route corresponds to the resolved,
   canonical URL.

Any failure, denial, redirect, transformation, or noncanonical hidden route
rejects the entire candidate. V1 does not prune or rewrite individual hidden
nodes because doing so could silently change stack, branch, or modal semantics;
the router instead creates a fresh canonical plan from the visible entry URL.
Stored browser state never bypasses current authorization.

Candidate-wide validation is not a permanent authorization grant. If policy
changes after restoration, the visibility-policy gate runs again before a
topology operation exposes a different route. A denial leaves the current
presentation and logical state unchanged. A configured redirect is planned as
a new policy-approved navigation and replaces the affected browser entry only
after successful presentation.

Candidate and visibility evaluation invoke the side-effect-free
`INavigationAccessEvaluator` surface directly. They never run
`AccessGateNavigationPolicy`'s defer-and-enqueue behavior, and rejecting hidden
routes cannot mutate the deferred-request store.

## Presentation transaction

After navigation interception is enabled, the provisional interactive order is:

1. classify the operation's self-issued history egress;
2. push, replace, or verify the already-traversed browser entry and its state;
3. request the target outlet render;
4. acknowledge the batch containing the outlet's own diff after the client has
   applied it; and
5. return presenter success so core may commit.

This order ensures destination components observe the target
`NavigationManager.Uri` while rendering. History verification and outlet
acknowledgment are bounded operations; Phase 0 must establish measured defaults
before they become public configuration. A refused mutation, timeout,
disconnect, or superseding ingress fails or cancels the presentation. Each
operation's acknowledgment token is single-shot and idempotent, so an old
Server render batch acknowledged after reconnect cannot complete an abandoned
operation.

After-render acknowledgment guarantees only that the batch containing the
outlet diff was applied. A destination awaiting `OnInitializedAsync` may have
contributed placeholder markup rather than its eventual content. Focus,
fragment scrolling, and scroll restoration are therefore decoupled from core
commit and may retry on later renders until they succeed, time out, or the route
is superseded. `OnAfterRender{Async}` does not run during prerender, which is why
prerender uses the separate boundary below.

A failure before that boundary leaves core state uncommitted and triggers
best-effort recovery of the previous browser entry and render state. Blazor may
have already disposed or mutated component instances, so V1 does not promise
that the exact prior UI instance or all transient component state remains
visible. The presenter reports whether recovery was complete or degraded. A
later component event failure or terminated Server circuit does not
retroactively roll back an already committed navigation. Recovery starts from
the current URL and stored candidate state.

Prerender has a separate completion boundary: route resolution, policy,
destination rendering, persisted handoff generation, and any HTTP status or
redirect decision must complete before the response is committed or streamed.
It does not perform or verify a browser-history mutation.

If a Server circuit disconnects during presentation, an operation is canceled
when its completion can no longer be verified. A normal transient reconnect is
allowed to preserve the framework circuit and its committed UI; the adapter
must not fight Blazor's reconnect or .NET 10 circuit-persistence behavior.
Reconnection to the same circuit compares committed and browser entry IDs and
resynchronizes when needed. A new circuit uses the normal policy-checked
startup restoration flow. AppNav never resumes a partially completed
presentation transaction merely from its own snapshot. Persisted component-state
keys distinguish prerender activation from circuit pause/resume through an
explicit `RestoreBehavior` or `RegisterOnRestoring` discriminator; a resume
never blindly adopts the last prerender handoff as a new startup entry.

## Component mapping and lifetime

The Blazor generator recognizes
`[BlazorRouteComponent(typeof(ProductRoute))]` and emits a component registry
module. A component may declare more than one mapping attribute, and manual
registry APIs remain available for libraries, base-route mappings, and advanced
composition. Conflicting mappings for the same route type fail during
registration. Runtime lookup selects the most-specific registered route type
in the route object's class hierarchy.

Lookup occurs through an asynchronous component-resolver abstraction. The V1
generated/manual registry is immutable after startup and normally completes
synchronously, but the contract can later accommodate lazy assemblies,
plug-ins, or remote descriptors without replacing the outlet API. Resolvers
return adapter-owned component descriptors rather than exposing mutable
dictionaries as the public contract. Component-type members on public
descriptors carry the same trimming annotation required by Blazor dynamic
component rendering.

Each mapped component exposes a strongly typed `[Parameter]` conventionally
named `Route` by default. The generated or manual descriptor records the actual
parameter binding, so a future convention or source-generated binding strategy
does not require changing the outlet contract. The outlet also supplies an
`AppNavRouteContext` cascading value containing the full `RouteEntry`, metadata,
structural identifiers, navigation services, and a route-lifetime cancellation
token.

Component construction and disposal remain renderer-owned. AppNav does not
create per-route DI scopes; components use ordinary Blazor scoped services and
may use `OwningComponentBase` when they need a component-owned scope.
Consequently, adapter scoped services are circuit-scoped in Interactive Server
and effectively application-lifetime in client-side WebAssembly. Their design
must not equate a DI scope with a route-entry lifetime.

Dynamic component rendering passes only descriptor-declared parameters. The
adapter does not use a catch-all component parameter to forward arbitrary
values, which would reserve unknown parameter names and make later framework or
adapter parameters a compatibility hazard.

Only visible route components are mounted. Covered stack entries and inactive
branches remain in logical state, but their components are disposed and
recreated when visible again. Dynamic hosts use an explicit render key composed
from the owning structural path (window, node, branch, and modal ownership),
`RouteEntry.Id`, and resolved component type. A component is reused only when
that complete identity is unchanged; its parameters and cascading context
update normally. A changed owner path, ID, or component type recreates it. The
route-lifetime token is canceled when the entry is replaced, hidden, removed,
or the outlet shuts down.

The generator or manual registry resolves standard Blazor `[Layout]` metadata
from the mapped destination and records it in the component descriptor, using
the registered default when no attribute is present. The renderer then uses
`LayoutView` to compose that resolved layout and its parent layout chain. A
wrapper that is already interactive may provide a local `DefaultLayout`
component type. There is no parallel AppNav layout abstraction and no non-
serializable layout type crosses a static-to-interactive boundary.

## Topology presentation

V1 rejects zero-window-invalid or multi-window plans before materializing
components. One browser tab/document corresponds to one logical window. The
adapter acquires a presentation-surface lease keyed by a surface ID, with one
default surface in V1; it does not enforce ownership with a process-wide global
singleton. This leaves room for named outlets or multi-window hosts without
weakening the one-owner rule.

- A stack renders its top entry.
- A branch host renders the selected branch's visible route and preserves every
  branch's logical stack without keeping inactive components mounted.
- A modal renders its visible modal route or nested top route above the owning
  root.
- The address bar always formats the topmost visibly presented route. Background
  stack and branch routes exist only in the stored topology candidate.

Every successfully presented active-window plan must resolve a real visible
application route. An operation that would leave only internal `BackRoute` or
`ReconciledRoute` sentinels is unhandled or canonically replanned; sentinels are
never formatted into the address bar.

The package ships accessible, unstyled default branch and modal hosts. Both are
replaceable through component descriptors registered in DI, or through local
templates declared wholly inside an already interactive wrapper. AppNav owns
selection/dismissal behavior, focus restoration, modal focus trapping, and
structural semantics, but not application styling.

Selecting a branch through the default host invokes the host-neutral branch-
selection operation, evaluates access to that branch's current visible route,
preserves every branch's existing stack, and pushes a browser entry after
successful presentation. Reselecting the current branch is a no-op. Dismissing
the top modal invokes logical Back.

## URL and fragment behavior

After accepting an inbound URL, AppNav formats the typed route back through the
route table. Differences in casing, escaping, optional syntax, or declared-query
normalization replace the current address without adding an entry. Query keys
declared by the matched route are AppNav-owned; undeclared keys are ambient and
survive inbound canonicalization and same-route browser updates unchanged.
Typed navigation to another route drops ambient query values unless the Blazor
caller explicitly requests preservation. Changing only ambient query values is
a browser presentation update and does not append to the core operation journal.

Fragments are browser presentation hints, not semantic route identity or
policy input. The anchor bridge intercepts an eligible fragment-only link on the
current route and creates a browser presentation entry with a fresh entry ID,
an AppNav history-state envelope, and a reference to the unchanged topology
payload. It then uses Blazor-equivalent `scrollIntoView` behavior without
invoking semantic route planning or appending to the core operation journal.
Scripted scrolling cannot reproduce native `:target` matching, ancestor
revealing, or every sequential-focus side effect. Repeating the already-current
fragment re-scrolls without adding an entry.

Browser Back/Forward among fragment entries preserves core topology and restores
fragment-specific focus and scroll state. Refresh on a fragment entry performs
normal policy-checked startup restoration from the full URL. Fragment detection
and adoption read `location.href` through JavaScript because
`NavigationManager.Uri` can be stale after same-document fragment navigation.
A native null-state or partially stamped fragment entry is adopted by replacing
that current entry with a completed envelope; it does not add another history
entry. Before replacement, the bridge atomically verifies that the current URL
and entry marker still match the observation so a delayed Server round trip
cannot stamp a newer entry.

Logical `BackAsync` remains topology-based rather than consuming one fragment
at a time and replaces the current entry with the logical parent. A link to
another AppNav route with a fragment navigates through AppNav and scrolls after
the destination renders. Canonicalization preserves the fragment.

## Restoration-state storage

Persistence is accessed through an asynchronous, replaceable restoration-state
provider. The default browser provider uses `history.state` plus
`sessionStorage`, but the public presenter and restoration contracts do not
depend on either. Future providers may use IndexedDB, protected server storage,
application storage, or no persistence at all.

Each AppNav browser entry contains a small versioned `history.state` envelope:

- adapter instance ID;
- browser entry ID;
- envelope and payload schema versions;
- application/route-table compatibility fingerprint;
- operation correlation data;
- a key for the corresponding `sessionStorage` payload.

The payload contains the logical topology encoded with structural IDs and
canonical route URIs. Route objects are not polymorphically serialized.
Restoration matches the URIs through `RouteTable`. Only metadata explicitly
registered as restorable in `RouteStateRegistry` is persisted; ephemeral
metadata, transport provenance, component state, services, and callbacks are
never stored. Applying `RouteStateRegistry` to topology `RouteEntry.Metadata`
and serializing its supported closed value set is new restoration work, not a
capability inherited from deferred-request persistence. An unsupported value
degrades that candidate according to the provider's failure rules.

Budgets are provider- and hosting-mode-specific, configurable, and enforced on
serialized byte counts. The feasibility slice must measure real payloads before
defaults are stabilized. Interactive Server starts with a conservative target
of 8-16 KiB for one payload because each storage operation crosses the network;
standalone WebAssembly may select a larger measured budget. Count and total
budgets are likewise selected from measurement rather than promised as public
constants.

Oldest snapshots are evicted first. Unknown, future, corrupt, oversized, schema-
incompatible, or route-table-incompatible snapshots are removed rather than
migrated in v1. Compatibility-fingerprint mismatch is diagnosed as stale
deployment state. A missing or evicted candidate reconstructs canonical
topology from its URL.

`sessionStorage` is unavailable during prerender and is always an optimization,
not a navigation prerequisite. The server prerender phase can persist only a
sanitized canonical handoff candidate through Blazor's prerendered-state
mechanism. It does not promise that the complete inactive topology survives
activation. Quota, privacy-mode, availability, network, or serialization
failures degrade safely to URL-only canonical reconstruction and emit
structural diagnostics. They do not fail an otherwise valid navigation.

## Prerender, authorization, and HTTP outcomes

When server hosting is available, `AppNavOutlet` renders the resolved
destination during prerender rather than a shell. It uses Blazor's persisted
component state to hand a sanitized canonical candidate to the interactive
instance so activation can adopt the existing entry without a duplicate push.
The handoff avoids avoidable flicker but does not guarantee preservation of
inactive topology or component instances. WebAssembly handoff data is visible
to the browser and must contain no secrets; Server handoff uses the framework's
protected mechanism but is still validated as untrusted restoration input.

Prerender and interactive scopes are separate trust boundaries. Current access
policy is re-evaluated when interactivity activates even when a persisted
candidate is present. Policies must therefore be deterministic and safe to run
during prerender and activation; side effects belong outside navigation policy.

An explicit opt-in such as `AddAppNavBlazorAuthorization` translates standard
`IAuthorizeData` metadata, including `[Authorize]` and `[AllowAnonymous]`, on
mapped components into a replaceable AppNav access-policy integration. It
participates in ordinary request resolution, candidate-wide restoration
validation, and the visibility-policy gate for topology-only operations. This
is AppNav compatibility behavior: Blazor only enforces these attributes
through `AuthorizeRouteView` during interactive router presentation. During
server endpoint execution, authorization middleware also evaluates attributes
copied to endpoint metadata. Attributes on the infrastructure catch-all are
therefore an all-or-nothing gate for the entire owned surface; per-destination
attributes are evaluated by AppNav because mapped destinations are not separate
endpoints.

The integration follows ASP.NET Core authorization policy combination semantics
and defines its authorization resource as an AppNav route/context object.
Authentication challenge, authorization forbid, denied-route navigation, and
HTTP redirect behavior are separate host-configurable outcomes rather than one
hard-coded redirect. Authorization is evaluated before presentation and logical
commit. Rich application access decisions remain ordinary AppNav policies, but
policies that protect route visibility must participate in restoration and the
visibility gate as well as initial requests. Client navigation policy is never
a substitute for server-side endpoint and data authorization.

Before a prerender response is committed or streamed, the Server package
supports real HTTP outcomes: configured not-found routes can produce `404`, and
eligible policy outcomes can produce `3xx`, challenge, or forbid responses.
Standalone WebAssembly has no server response to modify and retains client-side
behavior.

V1 subscribes to `NavigationManager.NotFound` behavior in both hosting phases.
During prerender, the Server integration uses the framework not-found path so a
rendered AppNav not-found destination is retained with the `404` response. After
activation, the outlet translates `NavigationManager.NotFound()` into navigation
to the configured AppNav not-found route instead of leaving the framework event
unhandled.

## User experience and accessibility

- New pushed destinations scroll to the top unless they specify a fragment.
- Replacement retains scroll unless the visible route materially changes.
- Back/Forward stores and restores scroll per browser entry after rendering.
- New visible routes focus a configurable selector (`h1` by default), falling
  back to the outlet's `tabindex="-1"` container.
- Restoration attempts to recover a recorded element ID before falling back to
  the heading.
- The default modal traps focus, permits focus in registered external overlay
  roots, and restores it to the triggering element when dismissed.
- Page titles remain component/application-owned through normal Blazor APIs.

Inside the interactive subtree, an AppNav shell may expose `Navigating` and
`NavigationError` fragments plus a cascading read-only status context. At the
static-to-interactive root, equivalent customization uses registered component
descriptors or serializable keys. Committed content remains visible where
possible while navigation is pending, but this is not guaranteed after a render
failure that has already disposed prior components. Explicit application calls
receive exceptions normally.

The default shell contains an AppNav-specific `ErrorBoundary` around destination
content. It converts render failures into sanitized presentation failure and
recovery signals where possible. An unhandled exception outside that boundary
can still terminate an Interactive Server circuit, so the public contract does
not promise universal recovery from arbitrary component failures.

The package provides stable DOM identity, state attributes, and lifecycle hooks
for application CSS and the browser View Transitions API. It does not own
animation timing, and animations are outside the presentation transaction.

## Security and diagnostics

Browser snapshots are untrusted input even though they originate from the same
application. Every candidate is schema checked, size bounded, structurally
validated, checked against the resolved visible route, and passed through
current policy before use.

The JavaScript integration ships as an isolated Razor Class Library static-web-
asset module. AppNav uses no inline script, `eval`, dynamically constructed
code, or unsafe HTML. "CSP compatible" applies to AppNav's own module and assets
under the hosting mode's documented Blazor policy: WebAssembly itself may
require `wasm-unsafe-eval`, and a selected third-party component library may
require inline styles even though AppNav does not.

Diagnostics remain structural and safe by default. They may report operation
kind, entry IDs, schema decisions, counts, byte sizes, eviction, degraded mode,
and sanitized route types. They do not log raw snapshot payloads, query values,
restorable metadata values, or provenance fields.

The Blazor generator reserves diagnostic IDs `APPNAV040` through `APPNAV059`.
Phase 3 extends the shared source-generator diagnostic reference rather than
creating a package-local numbering scheme.

## Compatibility and expansion constraints

V1 APIs are shaped so these later features remain additive:

- Lazy route assemblies or plug-ins can extend the asynchronous component
  resolver without changing outlet navigation contracts.
- IndexedDB, protected server state, or application persistence can replace the
  restoration-state provider without changing core restoration.
- Named outlets or multi-window hosts can add surface IDs and leases without
  converting a global singleton contract.
- Component retention can be introduced as a policy keyed by structural owner
  path, `RouteEntry.Id`, and component type; V1 disposal remains the default
  policy, not an assumption embedded in core.
- Transitions can observe presentation lifecycle hooks and contribute an
  adapter policy without changing core navigation transaction semantics.
- Enhanced navigation, automatic query-parameter binding, and form endpoint
  integration can be added as separate capability adapters if framework hooks
  prove sufficient.
- Authorization integrations remain optional policy adapters; core never
  depends on Blazor authorization metadata or services.
- Render-mode support is capability-driven rather than an exhaustive enum, and
  customization descriptors are resolved after the interactive boundary.

The first public release must avoid exposing browser storage keys, JavaScript
shapes, framework-internal route data, or renderer-specific completion objects
through core. Any V1 type that carries extensible policy or provider behavior
must prefer an interface or descriptor over a closed enumeration of anticipated
features.

## Verification matrix

Unit and component tests are necessary but not sufficient. Real-browser tests
are a release requirement.

| Area | Required coverage |
| --- | --- |
| Core | Host-neutral presenter intent, side-effect-free restoration/visibility access, Back-policy ordering, branch selection, deferred presentation-time navigation, ambient queries, journal semantics, request provenance, and MAUI compatibility |
| Generator | Multiple valid mappings, typed route parameter, layout descriptor, inheritance, conflicts, `@page` warning, reserved `APPNAV040-059`, trimming-safe output |
| Components | Interactive-root serialization boundary, composite render key, same-type/different-entry disposal, duplicate IDs under different owners, nested layouts, outlet acknowledgment, pending/error content |
| History | Navigation-interception enablement, no pre-enable `NavigateTo`, self-egress classification, push/replace, stamped fragments, logical-Back replacement, browser Back/Forward, ambient query updates, rapid traversal |
| Startup | Address-bar deep link, canonicalization, static-host fallback rewrite, refresh, duplicated tab, storage unavailable, schema and compatibility-fingerprint mismatch |
| Security | Unauthorized hidden routes in every topology shape, no deferred-store mutation, whole-candidate rejection, visibility changes, Back-policy ordering, endpoint authorization, mode-appropriate CSP |
| Hosting | Standalone WebAssembly, prerendered Interactive WebAssembly with dual service registration, Interactive Server, disconnect/late acknowledgment, pause/resume discrimination, new-circuit restoration |
| Accessibility | Deferred focus/scroll readiness, external overlay roots, modal focus trap, fragment limitations and restoration |
| HTTP | Route-carrier discovery, nonfile and GET/HEAD constraints, endpoint/fallback precedence, missing assets, unmatched POST, rendered 404, redirect, challenge, forbid, and streaming boundary |
| Packaging | Cold-cache consumers, static web assets, analyzer transitively included, no server dependency in standalone WASM |
| Compatibility | Trim analysis, WebAssembly AOT publish, warnings as errors, public API baselines |
| Deferred boundaries | No accidental reliance on `Router`, `FocusOnNavigate`, `SupplyParameterFromQuery`, enhanced/static forms, or conditional logical-Back traversal |
| MudBlazor | Providers in the interactive shell, controls/links, external overlay focus, dialog open during failed navigation, nested AppNav modal, prerender, base path, versioned CSP requirements, trim, and AOT |

Chromium runs on every pull request. Firefox and WebKit may run in the release-
confidence lane when execution cost requires it, but all three must pass before
a Blazor package release.

## Official framework references

The constraints above are based on the ASP.NET Core 10 documentation for:

- [Blazor routing](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/routing?view=aspnetcore-10.0)
  and [navigation/history integration](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/navigation?view=aspnetcore-10.0);
- [render modes and boundary serialization](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-modes?view=aspnetcore-10.0),
  [prerendering](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/prerender?view=aspnetcore-10.0),
  and [prerendered state persistence](https://learn.microsoft.com/en-us/aspnet/core/blazor/state-management/prerendered-state-persistence?view=aspnetcore-10.0);
- [dependency-injection lifetimes](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/dependency-injection?view=aspnetcore-10.0),
  [authorization](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0),
  [component lifecycle](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/lifecycle?view=aspnetcore-10.0),
  and [error handling](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/handle-errors?view=aspnetcore-10.0);
- [dynamic components](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/dynamiccomponent?view=aspnetcore-10.0),
  [layouts](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/layouts?view=aspnetcore-10.0),
  and [head content](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/control-head-content?view=aspnetcore-10.0);
- [browser storage](https://learn.microsoft.com/en-us/aspnet/core/blazor/state-management/protected-browser-storage?view=aspnetcore-10.0)
  and [Interactive Server circuit state](https://learn.microsoft.com/en-us/aspnet/core/blazor/state-management/server?view=aspnetcore-10.0);
- [Razor Class Library static assets](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/class-libraries?view=aspnetcore-10.0),
  [Content Security Policy](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/content-security-policy?view=aspnetcore-10.0),
  and [forms](https://learn.microsoft.com/en-us/aspnet/core/blazor/forms/binding?view=aspnetcore-10.0).

Fragment-entry state behavior is defined by the
[HTML fragment-navigation algorithm](https://html.spec.whatwg.org/multipage/browsing-the-web.html#navigate-fragid),
which does not carry classic History API state into native fragment-navigation
entries.

These links are evidence for current framework behavior, not dependencies in
the public AppNav contract. The feasibility slice must recheck them against the
target .NET 10 servicing release and verify behavior in real hosts.

## Implementation plan

### Phase 0: design and feasibility

- [x] Record the accepted scope and ownership decisions.
- [x] Audit the baseline against official ASP.NET Core 10 Blazor documentation
  and reserve extension points for deferred framework features.
- [ ] Specify proposed core API signatures and update the adapter contract.
- [ ] Add failing core tests for presentation-time lifecycle navigation and a
  browser Back intent arriving while presentation awaits self-issued history
  mutation; do not solve either by suppressing execution-context flow.
- [ ] Add failing core contract tests for history intent, branch selection,
  ambient-query ownership, policy-aware restoration, and visible-route
  requirements.
- [ ] Prove candidate-wide policy validation rejects unauthorized routes in
  covered stacks, inactive branches, modal owners/content, and nested topology
  without mutating the deferred-request store.
- [ ] Prove the visibility-policy gate prevents Back, modal dismissal, and
  branch fallback from exposing a route denied after state creation, runs before
  `IBackNavigationPolicy`, and preserves MAUI hardware-Back cancellation.
- [ ] Build an internal stack-only outlet and component registry without
  stabilizing public Blazor APIs.
- [ ] Prove the highest-interactive-root and JSON-serializable parameter
  boundary for Interactive Server and prerendered Interactive WebAssembly.
- [ ] Add MudBlazor providers above the internal outlet and prove a control,
  menu/popover, dialog, snackbar, form, and navigation link in the same vertical
  slice without adding a production package dependency.
- [ ] Prove push, replace, logical Back, browser Back/Forward, and refresh in
  minimal Interactive Server and WebAssembly hosts.
- [ ] Prove `EnableNavigationInterceptionAsync` enables ordinary links,
  programmatic navigation, and popstate in every supported host; assert that
  pre-enable `NavigateTo` is prohibited and self egress cannot re-enter core.
- [ ] Prove pre-interactive progressive navigation, full-load scope fallback,
  and degraded behavior when the JavaScript bridge is unavailable.
- [ ] Prove multiple fragment entries, Back/Forward between fragments, refresh
  on a fragment, repeated-fragment re-scroll, adoption of null/partial state,
  stale `NavigationManager.Uri`, and rapid successive clicks during adoption.
- [ ] Prove `NavigationManager` preserves AppNav history entry state and limit
  direct JavaScript to the documented gaps.
- [ ] Prove the provisional history-before-render order, outlet-diff
  acknowledgment, async-destination focus retry, bounded timeout, Server
  disconnect, and idempotent late acknowledgment after reconnect.
- [ ] Prove real destination prerender and interactive handoff.
- [ ] Prove `Blazor.pauseCircuit()`/`resumeCircuit()` cannot adopt stale
  prerender state or create a duplicate browser entry.
- [ ] Measure serialized restoration payload and circuit transfer costs before
  choosing provider-specific defaults.
- [ ] Prove the infrastructure route carrier, additional-assembly discovery,
  nonfile and GET/HEAD constraints, base paths, endpoint/fallback precedence,
  missing assets, unmatched POST, rendered `404`, redirect, and streaming.
- [ ] Prove `NavigationManager.NotFound()` renders the configured AppNav
  destination during prerender and after interactive activation.
- [ ] Run the vertical slice in Chromium and document any contract corrections.
- [ ] Stop for design review before publishing public Blazor API baselines.

### Phase 1: core contracts

- [ ] Implement approved host-neutral presentation-history intent.
- [ ] Implement policy-aware restoration, candidate-wide route validation, and
  whole-candidate rejection.
- [ ] Add side-effect-free deny semantics and implement the host-neutral
  visibility gate before existing Back policies; define after-the-fact
  reconciliation recovery.
- [ ] Implement branch selection without contextual replanning and preserve all
  branch stacks.
- [ ] Implement deferred/superseding navigation during presenter callouts and
  self-egress classification without weakening the reentrancy guard.
- [ ] Preserve ambient queries on inbound/same-route updates and drop them by
  default on typed cross-route navigation.
- [ ] Append address-bar, host-history, and host-Back request-source values at 8
  or above; never reuse retired value 5 and correct existing host-Back journal
  provenance.
- [ ] Require a real visible route for every successful active-window plan.
- [ ] Define operation-journal semantics in public documentation.
- [ ] Define presentation completion, bounded acknowledgment, cancellation, and
  late-arrival semantics in the public adapter contract.
- [ ] Update MAUI and public adapter-contract tests in the same change.
- [ ] Update core public API baselines and release notes.

### Phase 2: portable Blazor runtime

- [ ] Add `AdamE.AppNav.Blazor` as a Razor Class Library.
- [ ] Add scoped DI composition and surface-ID lease enforcement.
- [ ] Implement asynchronous route/component resolver, immutable V1 registry,
  descriptors, and typed/cascading context delivery.
- [ ] Implement composite structural render keys, descriptor-resolved layouts,
  and route-lifetime cancellation across every topology owner.
- [ ] Implement stack, branch-host, nested-content, and modal rendering.
- [ ] Implement renderer acknowledgment, cancellation, complete/degraded
  recovery reporting, error boundary, and shutdown.
- [ ] Implement typed and ordinary links, canonical URLs, fragments, layouts,
  pending state, and navigation error UI.
- [ ] Enable Blazor navigation interception and implement only the remaining
  JavaScript gaps: scope fallback, atomic fragment stamping, storage, and
  focus/scroll capture.
- [ ] Implement isolated browser history and replaceable restoration storage
  with measured host-specific bounds and URL-only degradation.
- [ ] Implement scroll, focus, modal accessibility, and duplicate-tab handling.
- [ ] Add focus-claim arbitration and overlay/disposal integration hooks without
  introducing a MudBlazor-specific runtime contract.

### Phase 3: generator and authorization

- [ ] Add `AdamE.AppNav.Blazor.Generators` and transitive packaging.
- [ ] Generate trimming-safe component modules and diagnostics.
- [ ] Generate layout descriptors, trimming annotations, and diagnostics in the
  reserved `APPNAV040-059` range; update the shared diagnostic reference.
- [ ] Add opt-in, replaceable standard authorization-metadata policy
  integration with defined resource and host outcomes.
- [ ] Add generator, component, public API, trim, and AOT tests.

### Phase 4: server hosting

- [ ] Add `AdamE.AppNav.Blazor.Server` without leaking server dependencies into
  the portable package.
- [ ] Add the infrastructure catch-all host beneath the configured base path;
  stabilize endpoint mapping helpers only after the feasibility gate.
- [ ] Implement prerender destination rendering and sanitized handoff.
- [ ] Implement policy revalidation at interactive activation.
- [ ] Implement `NavigationManager.NotFound` integration, rendered `404`, and
  eligible `3xx`, challenge, and forbid behavior.
- [ ] Verify transient reconnect, circuit persistence, cancellation when
  completion is unverifiable, prerender-vs-resume state discrimination, and
  new-circuit restoration.

### Phase 5: samples, release gates, and documentation

- [ ] Add one shared representative MudBlazor sample with thin Server,
  prerendered WebAssembly, and standalone WebAssembly hosts.
- [ ] Add Chromium PR coverage and Firefox/WebKit release-confidence coverage.
- [ ] Exercise MudBlazor provider, overlay, navigation, nested-modal, refresh,
  Back/Forward, dialog-open presentation failure, and prerender scenarios in the
  real-browser matrix.
- [ ] Add cold-cache package consumers and static-web-asset verification.
- [ ] Generalize version and asset verification from two hard-coded packages to
  the complete release artifact set before publishing Blazor packages.
- [ ] Add repeatable trim and WebAssembly AOT publish gates.
- [ ] Write consumer setup, topology, history, restoration, security,
  static-host deep-link rewriting, endpoint precedence, troubleshooting, and
  migration documentation.
- [ ] Add packages to the unified release artifact set only after all gates pass.

## Feasibility stop conditions

The vertical slice must return to design review instead of papering over any of
these findings:

- the presenter cannot reliably acknowledge render plus history completion;
- presentation-time component navigation cannot be deferred or superseded
  without reentrancy failure, lock deadlock, or weakened core serialization;
- browser traversal cannot supersede an uncommitted apply without corrupting
  router state;
- prerender handoff requires destination route ownership through `@page` rather
  than the single infrastructure host route;
- policy-aware restoration cannot preserve candidate topology without bypassing
  current access decisions;
- core cannot prevent a topology-only operation from exposing a route denied by
  current policy;
- standalone WebAssembly requires server-only dependencies;
- Blazor navigation interception cannot provide exactly-once ordinary-link,
  programmatic, and popstate ingress in every supported hosting mode;
- fragment entries cannot retain AppNav identity without corrupting native
  Back/Forward behavior;
- ambient query preservation or branch selection requires destructive topology
  reconstruction;
- the server route carrier cannot coexist safely with static assets, GET/HEAD
  ownership, rendered HTTP outcomes, and application endpoint precedence;
- representative MudBlazor providers or overlays require a competing router,
  an incompatible render-mode boundary, or nondeterministic focus ownership;
- a required behavior differs materially across Chromium, Firefox, and WebKit.

These are design feedback, not reasons to weaken transaction, ownership, or
security guarantees silently.

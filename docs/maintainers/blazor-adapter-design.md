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
- `NavigationManager.NotFound` interoperability for interactive routing;
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
The Blazor packages remain internal and unpacked during the feasibility phase;
they join release artifacts only after every required gate passes.

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
- AppNav modal hosts and MudBlazor overlays to have testable focus-trap,
  Escape-key, backdrop, scroll-lock, and stacking behavior when nested;
- MudBlazor static assets and JavaScript to coexist with the AppNav Razor Class
  Library module under base-path hosting and the hosting mode's CSP; and
- covered or inactive destinations to retain no implicit component-local state
  merely because a MudBlazor component rendered it.

AppNav does not claim compatibility with third-party components that own a
`Router`/`RouteView`, require Blazor `RouteData`, write browser history directly,
or assume that every destination has `@page`. Those integrations require an
explicit bridge and remain outside V1.

## Navigation ownership

AppNav is the exclusive navigation authority for the surface owned by
`AppNavOutlet`. It replaces Blazor's `Router` for that surface. `NavigationManager`
and the browser History API are host transports, not competing route planners.

V1 disables Blazor enhanced-navigation interception within the AppNav-owned
surface as an implementation choice, not as a permanent public contract.
AppNav intercepts unmodified same-origin navigation beneath the application
base path. It does not intercept external origins, downloads, modified clicks,
links with another target, forced loads, or paths outside the owned base path.
Unmatched owned paths still enter the AppNav pipeline so the configured
fallback can produce a not-found route.

AppNav links always render real canonical `href` values. The package provides a
typed `AppNavLink`, including active state, route metadata, replacement options,
and an optional fragment, while ordinary eligible anchors work through the same
interception boundary.

Mapped destination components do not require Blazor `@page` directives. AppNav
route definitions remain the single destination route table. The generator
warns when a mapped destination also declares `@page`.

The Server package initially uses one infrastructure catch-all host component
with a Blazor `RouteAttribute`/`@page` route so it participates in normal Razor
Components endpoint discovery and prerendering. The catch-all is beneath the
configured base path and ordered after static assets and explicitly mapped
non-AppNav endpoints. A convenience such as `MapAppNavCatchAll` must remain
internal until the feasibility slice proves base-path hosting, dotted and
encoded paths, static assets, API endpoints, direct requests, prerendering,
status codes, redirects, and authorization behavior. Initial HTTP
canonicalization uses an HTTP redirect before response streaming starts;
interactive canonicalization replaces the current browser entry.

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
interception flag, cancellation token, and prevention mechanism. Direct
JavaScript is limited to capabilities Blazor doesn't expose, such as bounded
`history.go`, session storage, scroll/focus capture, and any proven interception
bridge. The adapter must not separately overwrite `history.state` after a
Blazor navigation unless the vertical slice proves that doing so preserves
framework state and behavior.

Location-changing callbacks may overlap, and multiple registered handlers have
no useful ordering contract. AppNav registers one coordinator, treats callback
state as request-local, honors cancellation, and never relies on another
handler running first. Blazor may temporarily revert and replay a navigation
while asynchronous handlers run; tests must verify that this does not create
extra AppNav journal entries or browser snapshots.

| Router or host operation | Browser behavior |
| --- | --- |
| Normal `NavigateAsync` | Push a new entry |
| `ReplaceCurrent` navigation | Replace the current entry |
| Initial address-bar startup | Adopt and replace the existing entry; never push a duplicate |
| Browser Back/Forward restoration | Preserve the timeline; do not push or replace unless policy redirects or canonicalizes |
| Logical `BackAsync` with a known immediately preceding matching AppNav snapshot | Traverse to that preceding entry |
| Logical `BackAsync` without a matching predecessor | Replace the current entry with the logical parent |

Logical Back therefore remains topology-based. Browser Back and Forward remain
chronological. The conditional traversal optimization avoids producing
`Home -> Home` duplicates after an ordinary `Home -> Detail` push, while a
logical Back from a direct deep link can still reveal its canonical parent
instead of leaving the application.

Every successful browser restoration is appended to the core operation journal
with history-traversal provenance. A browser traversal that supersedes an
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
   Detection and execution of a conditional preceding-entry traversal belongs
   to the browser adapter; it is not a browser-shaped core intent. The semantics
   remain meaningful to non-browser adapters, which may ignore unsupported
   host-history behavior.
2. **Policy-aware restoration.** Add a restoration operation distinct from
   `ReconcileAsync`. Reconciliation remains a trusted report of already
   observed host state, such as a MAUI native pop. Restoration accepts a
   host-neutral request plus an optional untrusted candidate state, runs the
   normal request and policy pipeline, validates the candidate, then restores
   or replans. Core contracts must not mention browser schemas, storage keys,
   JavaScript, or `sessionStorage`.
3. **Accurate request sources.** Add explicit address-bar and host-history-
   traversal sources, plus diagnostic/reconciliation vocabulary that does not
   misclassify Forward as Back.
4. **Journal semantics.** Document `NavigationHistory` explicitly as a bounded
   successful-operation journal rather than a host history cursor.
5. **Presentation completion semantics.** Define success as accepted host
   presentation through its observable transaction boundary, not a promise
   that a UI artifact can never fail later.

A restoration candidate wins over replanning only when all of the following
are true:

1. The entry URL resolves and passes current transformation and access policy.
2. The candidate state passes core structural validation.
3. The candidate's visibly presented route corresponds to the resolved,
   canonical URL.

If any check fails, the router creates a fresh canonical plan. Stored browser
state never bypasses current authorization. Policy rejection or redirect after
a traversal replaces the traversed browser entry with the approved
destination.

## Presentation transaction

Interactive Blazor presentation succeeds only after:

1. the outlet completes the target render batch; and
2. the corresponding browser-history mutation completes and is verified.

Interactive render acknowledgment is emitted from an after-render lifecycle
point where the destination DOM is available. `OnAfterRender{Async}` does not
run during prerender, which is why prerender uses the separate boundary below.

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
presentation transaction merely from its own snapshot.

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
dictionaries as the public contract.

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
recreated when visible again. A currently visible component is reused when its
`RouteEntry.Id` and mapped component type are unchanged; its parameters and
cascading context update normally. A changed ID or component type recreates it.
The route-lifetime token is canceled when the entry is replaced, hidden,
removed, or the outlet shuts down.

Standard Blazor `[Layout]` metadata is honored, and the adapter provides a
default-layout registration resolved inside the interactive subtree through
normal `LayoutView` composition. A wrapper that is already interactive may
provide a local `DefaultLayout` component type. There is no parallel AppNav
layout abstraction and no non-serializable layout type crosses a static-to-
interactive boundary.

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

The package ships accessible, unstyled default branch and modal hosts. Both are
replaceable through component descriptors registered in DI, or through local
templates declared wholly inside an already interactive wrapper. AppNav owns
selection/dismissal behavior, focus restoration, modal focus trapping, and
structural semantics, but not application styling.

Selecting a branch through the default host performs a policy-checked
`InAppCommand` navigation to that branch's current top route and pushes a
browser entry. Reselecting the current branch is a no-op. Dismissing the top
modal invokes logical Back.

## URL and fragment behavior

After accepting an inbound URL, AppNav formats the typed route back through the
route table. Differences in casing, escaping, optional syntax, or query
normalization replace the current address without adding an entry.

Fragments are browser presentation hints, not semantic route identity or
policy input. A fragment-only link on the current route remains native browser
navigation. A link to another AppNav route with a fragment navigates through
AppNav and scrolls after the destination renders. Canonicalization preserves
the fragment.

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
- operation correlation data;
- a key for the corresponding `sessionStorage` payload.

The payload contains the logical topology encoded with structural IDs and
canonical route URIs. Route objects are not polymorphically serialized.
Restoration matches the URIs through `RouteTable`. Only metadata explicitly
registered as restorable in `RouteStateRegistry` is persisted; ephemeral
metadata, transport provenance, component state, services, and callbacks are
never stored.

Budgets are provider- and hosting-mode-specific, configurable, and enforced on
serialized byte counts. The feasibility slice must measure real payloads before
defaults are stabilized. Interactive Server starts with a conservative target
of 8-16 KiB for one payload because each storage operation crosses the network;
standalone WebAssembly may select a larger measured budget. Count and total
budgets are likewise selected from measurement rather than promised as public
constants.

Oldest snapshots are evicted first. Unknown, future, corrupt, oversized, or
incompatible schemas are removed rather than migrated in v1. A missing or
evicted candidate reconstructs canonical topology from its URL.

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
mapped components into a replaceable AppNav request-policy adapter. This is
AppNav compatibility behavior: Blazor only enforces these attributes
automatically for `@page` components reached through its `Router` and
`AuthorizeRouteView`.

The integration follows ASP.NET Core authorization policy combination semantics
and defines its authorization resource as an AppNav route/context object.
Authentication challenge, authorization forbid, denied-route navigation, and
HTTP redirect behavior are separate host-configurable outcomes rather than one
hard-coded redirect. Authorization is evaluated before presentation and logical
commit. Rich application access decisions remain ordinary AppNav policies.
Client navigation policy is never a substitute for server-side endpoint and
data authorization.

Before a prerender response is committed or streamed, the Server package
supports real HTTP outcomes: configured not-found routes can produce `404`, and
eligible policy outcomes can produce `3xx`, challenge, or forbid responses.
Standalone WebAssembly has no server response to modify and retains client-side
behavior.

## User experience and accessibility

- New pushed destinations scroll to the top unless they specify a fragment.
- Replacement retains scroll unless the visible route materially changes.
- Back/Forward stores and restores scroll per browser entry after rendering.
- New visible routes focus a configurable selector (`h1` by default), falling
  back to the outlet's `tabindex="-1"` container.
- Restoration attempts to recover a recorded element ID before falling back to
  the heading.
- The default modal traps focus and restores it to the triggering element when
  dismissed.
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
asset module. It uses no inline script, `eval`, dynamically constructed code,
or unsafe HTML. "CSP compatible" means compatible with the hosting mode's
documented Blazor policy: WebAssembly itself may require `wasm-unsafe-eval`
even though the AppNav module does not.

Diagnostics remain structural and safe by default. They may report operation
kind, entry IDs, schema decisions, counts, byte sizes, eviction, degraded mode,
and sanitized route types. They do not log raw snapshot payloads, query values,
restorable metadata values, or provenance fields.

## Compatibility and expansion constraints

V1 APIs are shaped so these later features remain additive:

- Lazy route assemblies or plug-ins can extend the asynchronous component
  resolver without changing outlet navigation contracts.
- IndexedDB, protected server state, or application persistence can replace the
  restoration-state provider without changing core restoration.
- Named outlets or multi-window hosts can add surface IDs and leases without
  converting a global singleton contract.
- Component retention can be introduced as a policy keyed by `RouteEntry.Id`;
  V1 disposal remains the default policy, not an assumption embedded in core.
- Transitions can observe presentation lifecycle hooks and contribute an
  adapter policy without changing core navigation transaction semantics.
- Enhanced navigation, query binding, not-found integration, and form endpoint
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
| Core | Host-neutral presenter intent, restoration policy, candidate validation, journal semantics, cancellation, and MAUI compatibility |
| Generator | Multiple valid mappings, typed route parameter, inheritance, conflicts, `@page` warning, trimming-safe output |
| Components | Interactive-root serialization boundary, outlet acknowledgment, reuse identity, disposal, layouts, branch/modal descriptors, pending/error content |
| History | `NavigationManager` integration, state preservation, push, replace, logical Back traversal/replacement, browser Back/Forward, rapid traversal, stale/missing candidate |
| Startup | Address-bar deep link, canonicalization, refresh, duplicated tab, storage unavailable, schema mismatch |
| Security | Tampered snapshots, route mismatch, policy changes after entry creation, opt-in authorization semantics, endpoint authorization, mode-appropriate CSP |
| Hosting | Standalone WebAssembly, prerendered Interactive WebAssembly with dual service registration, Interactive Server, reconnect and circuit persistence |
| Accessibility | Focus movement/restoration, modal focus trap, fragment and scroll restoration |
| HTTP | Infrastructure catch-all precedence, base paths, dotted/encoded paths, successful prerender, streaming boundary, not found, redirect, challenge, and forbid |
| Packaging | Cold-cache consumers, static web assets, analyzer transitively included, no server dependency in standalone WASM |
| Compatibility | Trim analysis, WebAssembly AOT publish, warnings as errors, public API baselines |
| Deferred boundaries | No accidental reliance on `Router`, `FocusOnNavigate`, `SupplyParameterFromQuery`, enhanced/static forms, or `NavigationManager.NotFound` |
| MudBlazor | Providers in the interactive shell, ordinary controls and links, dialogs/popovers, focus arbitration, disposal, nested AppNav modal, prerender, base path, CSP, trim, and AOT |

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

These links are evidence for current framework behavior, not dependencies in
the public AppNav contract. The feasibility slice must recheck them against the
target .NET 10 servicing release and verify behavior in real hosts.

## Implementation plan

### Phase 0: design and feasibility

- [x] Record the accepted scope and ownership decisions.
- [x] Audit the baseline against official ASP.NET Core 10 Blazor documentation
  and reserve extension points for deferred framework features.
- [ ] Specify proposed core API signatures and update the adapter contract.
- [ ] Add failing core contract tests for history intent and policy-aware
  restoration.
- [ ] Build an internal stack-only outlet and component registry without
  stabilizing public Blazor APIs.
- [ ] Prove the highest-interactive-root and JSON-serializable parameter
  boundary for Interactive Server and prerendered Interactive WebAssembly.
- [ ] Add MudBlazor providers above the internal outlet and prove a control,
  menu/popover, dialog, snackbar, form, and navigation link in the same vertical
  slice without adding a production package dependency.
- [ ] Prove push, replace, logical Back, browser Back/Forward, and refresh in
  minimal Interactive Server and WebAssembly hosts.
- [ ] Prove `NavigationManager` preserves AppNav history entry state and limit
  direct JavaScript to the documented gaps.
- [ ] Prove real destination prerender and interactive handoff.
- [ ] Measure serialized restoration payload and circuit transfer costs before
  choosing provider-specific defaults.
- [ ] Prove the infrastructure catch-all against base paths, endpoint ordering,
  dotted/encoded paths, HTTP outcomes, and response streaming.
- [ ] Run the vertical slice in Chromium and document any contract corrections.
- [ ] Stop for design review before publishing public Blazor API baselines.

### Phase 1: core contracts

- [ ] Implement approved host-neutral presentation-history intent.
- [ ] Implement policy-aware restoration and candidate validation.
- [ ] Add address-bar and host-history-traversal sources and diagnostics.
- [ ] Define operation-journal semantics in public documentation.
- [ ] Update MAUI and public adapter-contract tests in the same change.
- [ ] Update core public API baselines and release notes.

### Phase 2: portable Blazor runtime

- [ ] Add `AdamE.AppNav.Blazor` as a Razor Class Library.
- [ ] Add scoped DI composition and surface-ID lease enforcement.
- [ ] Implement asynchronous route/component resolver, immutable V1 registry,
  descriptors, and typed/cascading context delivery.
- [ ] Implement stack, branch-host, nested-content, and modal rendering.
- [ ] Implement renderer acknowledgment, cancellation, complete/degraded
  recovery reporting, error boundary, and shutdown.
- [ ] Implement typed and ordinary links, canonical URLs, fragments, layouts,
  pending state, and navigation error UI.
- [ ] Implement isolated browser history and replaceable restoration storage
  with measured host-specific bounds and URL-only degradation.
- [ ] Implement scroll, focus, modal accessibility, and duplicate-tab handling.
- [ ] Add focus-claim arbitration and overlay/disposal integration hooks without
  introducing a MudBlazor-specific runtime contract.

### Phase 3: generator and authorization

- [ ] Add `AdamE.AppNav.Blazor.Generators` and transitive packaging.
- [ ] Generate trimming-safe component modules and diagnostics.
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
- [ ] Implement `404` and eligible `3xx` response behavior.
- [ ] Verify transient reconnect, circuit persistence, cancellation when
  completion is unverifiable, and new-circuit restoration.

### Phase 5: samples, release gates, and documentation

- [ ] Add one shared representative MudBlazor sample with thin Server,
  prerendered WebAssembly, and standalone WebAssembly hosts.
- [ ] Add Chromium PR coverage and Firefox/WebKit release-confidence coverage.
- [ ] Exercise MudBlazor provider, overlay, navigation, nested-modal, refresh,
  Back/Forward, and prerender scenarios in the real-browser matrix.
- [ ] Add cold-cache package consumers and static-web-asset verification.
- [ ] Add repeatable trim and WebAssembly AOT publish gates.
- [ ] Write consumer setup, topology, history, restoration, security,
  troubleshooting, and migration documentation.
- [ ] Add packages to the unified release artifact set only after all gates pass.

## Feasibility stop conditions

The vertical slice must return to design review instead of papering over any of
these findings:

- the presenter cannot reliably acknowledge render plus history completion;
- browser traversal cannot supersede an uncommitted apply without corrupting
  router state;
- prerender handoff requires destination route ownership through `@page` rather
  than the single infrastructure host route;
- policy-aware restoration cannot preserve candidate topology without bypassing
  current access decisions;
- standalone WebAssembly requires server-only dependencies;
- conditional logical-Back traversal cannot be correlated safely;
- representative MudBlazor providers or overlays require a competing router,
  an incompatible render-mode boundary, or nondeterministic focus ownership;
- a required behavior differs materially across Chromium, Firefox, and WebKit.

These are design feedback, not reasons to weaken transaction, ownership, or
security guarantees silently.

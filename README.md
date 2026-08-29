# AdamE.AppNav

AdamE.AppNav is a route-first application navigation framework for .NET.
Its core owns semantic routes, request policy, topology planning, logical state,
history, diagnostics, and orchestration without depending on a UI framework.
`AdamE.AppNav.Maui` is the first production adapter and materializes that state
with native .NET MAUI windows, navigation stacks, tabs, pages, and modals.

The public preview is intentionally MAUI-first. The core/presenter boundary is
kept host-independent so a later adapter, such as Blazor, can reuse route and
policy behavior without inheriting MAUI pages, lifecycle, storage, or DI choices.

```text
URI or typed route
       |
       v
transform -> match -> policy -> plan logical topology -> present native UI
                                      ^                       |
                                      +---- reconcile Back ---+
```

The durable contract is a destination, not a page operation:

```text
https://example.com/stores/northwind/products/123

Route      ProductDetailRoute("northwind", 123)
Topology   store tabs -> catalog branch -> catalog stack -> detail
MAUI       TabbedPage -> NavigationPage -> ProductDetailPage
```

## Preview status

The target release is `0.1.0-preview.1`.

- .NET SDK: `10.0.400`
- MAUI workload set: `10.0.302.1`
- Android: API 28+
- iOS: 15+
- Mac Catalyst: 15+
- Local package version: `0.1.0-preview.local`

The preview includes:

- typed route generation and formatting;
- route matching, redirects, transforms, and access policy;
- standard stack and branch-host navigation models;
- a public custom-planner escape hatch;
- logical window, stack, branch-host, and modal state;
- native MAUI presentation and reconciliation;
- trusted-origin external navigation;
- bounded retrying ingress queues;
- schema-3 deferred navigation persistence;
- safe structural diagnostics;
- trimming and AOT-oriented implementation and gates.

The preview deliberately excludes:

- Windows MAUI support;
- a production Blazor adapter;
- true multi-window MAUI orchestration;
- a transition or shared-element system;
- Shell and Prism integration;
- NuGet.org publication;
- stable `1.0` compatibility guarantees.

Packages will be attached to a GitHub prerelease as `.nupkg` and `.snupkg`
files. The preview workflow does not push to NuGet.org.

## Packages

| Package | Responsibility |
| --- | --- |
| `AdamE.AppNav` | Routes, matching, policy, planning, state, history, diagnostics, orchestration |
| `AdamE.AppNav.Maui` | MAUI pages, native presentation, lifecycle ingress, startup, file persistence |

The core package carries only the route generator. The MAUI package carries
only the MAUI page generator. Consumers receive both transitively by referencing
`AdamE.AppNav.Maui`.

## Why route-first navigation

An application can receive the same destination from many places:

- a button or command;
- an Android App Link;
- an iOS Universal Link;
- a custom URI scheme;
- a push-notification interaction;
- a QR scan;
- deferred replay after sign-in;
- process restoration;
- an automated test.

Treating each as separate page manipulation creates duplicated trust checks,
auth rules, stack behavior, and analytics. AppNav converts them to one request
pipeline and one semantic route contract before presentation begins.

This design gives the app one place to answer:

- Is the origin trusted?
- Does a legacy URL need normalization?
- Is the destination authorized right now?
- Should it be deferred until authentication?
- Does in-app intent push contextually?
- Does external intent rebuild canonical topology?
- Which branch and stack own the destination?
- What must be persisted, and what must never reach disk?

## Core concepts

### `AppRoute`

An `AppRoute` is stable semantic destination identity. Use domain values in
route records. Do not put pages, view models, services, callbacks, native
handles, or transport provenance in a route.

### `AppRouteRequest`

An `AppRouteRequest` combines an `AppRoute` with app-owned route metadata.
It is the richer typed request used by app code when canonical, restorable,
or ephemeral metadata accompanies a destination.

### `RouterNavigationRequest`

`RouterNavigationRequest` is the complete runtime envelope. It contains one
URI or typed route target plus source, timestamp, window, metadata, disposition,
and provenance. External boundaries construct this full envelope explicitly.

### `INavigationModel<TRoute>`

A navigation model declares canonical topology and contextual stack mutations.
`StackNavigationModel<TRoute>` and `BranchHostNavigationModel<TRoute>` implement
the interface.

### `NavigationModelPlanner<TRoute>`

The standard planner translates request disposition into model behavior.
Applications with cross-model rules or domain-specific topology can continue
to implement `IAppNavigationPlanner` directly.

### `IRouterNavigator`

There is one navigator interface. It exposes current state, history, a full
`RouterNavigationRequest` operation, logical Back, host reconciliation, and
disposal. The four app-facing typed methods live on `RouterNavigatorExtensions`;
there are no URI/source convenience overloads on the public navigation surface.

### `INavigationPresenter`

The presenter is the adapter seam. Core plans a logical target state, asks the
presenter to apply it, and commits history only after successful presentation.
Failure or cancellation before commit leaves logical state unchanged.

## Getting started with MAUI

The complete buildable onboarding app is
[`samples/GettingStarted.Sample`](samples/GettingStarted.Sample/README.md).
It has Home -> Detail -> native Back and intentionally contains no external
navigation or persistence.

### 1. Define typed routes

The following snippet is sourced from the buildable sample region
`getting-started-routes`.

```csharp
[AppNavRoute("/home")]
public sealed record HomeRoute : AppRoute;

[AppNavRoute("/details/{itemId:int}")]
public sealed record DetailRoute(int ItemId) : AppRoute;
```

The route generator emits `AppNavRoutes.g.cs`, including the route-table module
used by the sample composition root.

### 2. Declare stack topology

The following snippet is sourced from `getting-started-model`.

```csharp
public static StackNavigationModel<AppRoute> Create()
{
    return StackNavigationModel<AppRoute>.Create(builder =>
    {
        builder.CanonicalSurface("main", "main-stack");
        builder.Map<HomeRoute>(recipe => recipe
            .EntryId(_ => "home")
            .ScopeKey(_ => "main"));
        builder.Map<DetailRoute>(recipe => recipe
            .EntryId(route => $"detail-{route.ItemId}")
            .ScopeKey(_ => "main")
            .Canonical((route, metadata) =>
            [
                new StackRouteStep<AppRoute>(new HomeRoute()),
                new StackRouteStep<AppRoute>(route, metadata)
            ]));
    });
}
```

Canonical detail navigation creates Home -> Detail. Contextual detail
navigation can push onto the current compatible stack.

### 3. Map routes to MAUI pages

Add `[MauiRoutePage(typeof(HomeRoute))]` and
`[MauiRoutePage(typeof(DetailRoute))]` to the page classes. The MAUI generator
emits `AppNavMauiPages.g.cs` and its `MauiPageModule`.

A button or view model navigates with the typed extension:

```csharp
await navigator.NavigateAsync(new DetailRoute(42));
```

The operation is an in-app request with `Auto` disposition. Native Back on the
resulting `NavigationPage` reconciles into the same logical state and history.

### 4. Register the model and generated modules

The following snippet is sourced from the buildable sample region
`getting-started-registration-services`:

```csharp
builder.Services.AddAppNavStartup(options =>
{
    options.AppLinkGracePeriod = TimeSpan.Zero;
    options.FallbackRouteFactory = static (_, _) =>
        ValueTask.FromResult<AppRoute?>(new HomeRoute());
});
builder.Services.AddAppNav(
    AppNavGenerated.CreateRouteTable(),
    GettingStartedNavigationModel.Create(),
    pages => pages.AddModule(AppNavGenerated.MauiPageModule));
```

The fallback route is wrapped as an in-app canonical request for the startup
window. `FallbackRequestFactory` remains available for advanced consumers that
need to construct the complete envelope; the two factories are mutually
exclusive.

### 5. Start from the MAUI window

The following snippet is sourced from the buildable sample region
`getting-started-window-start`:

```csharp
protected override Window CreateWindow(IActivationState? activationState)
{
    var window = new Window(new ContentPage());
    startup.Start(window, "main");
    return window;
}
```

`Start` schedules and observes startup internally. `StartAsync` remains public
for tests and advanced coordination.

## Navigation dispositions

The standard planner has one documented behavior table:

| Disposition | Standard behavior |
| --- | --- |
| `Auto` | Contextual push for `InAppCommand` and `Test`; canonical for external sources |
| `Contextual` | Contextual push, with canonical fallback |
| `ReplaceCurrent` | Contextual replace-top, with canonical fallback |
| `Canonical` | Rebuild the model's declared canonical topology |

Typed app code can use:

```csharp
await navigator.NavigateAsync(route);
await navigator.NavigateAsync(route, RouterNavigationDisposition.Contextual);
await navigator.NavigateAsync(routeRequest);
await navigator.NavigateAsync(routeRequest, RouterNavigationDisposition.ReplaceCurrent);
```

There are no URI/source convenience overloads on `IRouterNavigator`. Push,
QR, app-link, restore, and other boundaries must construct a full
`RouterNavigationRequest` so source and provenance are never implicit.

See [Topology and planning](docs/topology.md) for stack, branch-host, modal,
Back, canonical fallback, and custom-planner details.

## External navigation security

External lifecycle handling is opt-in:

```csharp
builder.UseAppNavExternalNavigation(options =>
{
    options.AllowOrigin(new Uri("https://example.com"));
    options.AllowOrigin(new Uri("myapp://open"));
});
```

At least one trusted origin is required. Allowed values must be absolute root
origins without credentials, query, or fragment. AppNav compares normalized
scheme, IDN host, and effective port.

Before route matching, persistence, analytics, or `ShouldDispatch`, ingress
rejects:

- relative URIs;
- oversized URIs;
- user-info credentials;
- untrusted schemes or hosts;
- wrong explicit ports;
- an empty allowlist.

Bootstrap and runtime queues are bounded. Overflow drops the oldest request so
newer explicit user intent survives. Retryable failures move to the tail, which
prevents one poison request from starving later navigation. Cancellation caused
by lifecycle changes preserves the request without consuming an attempt.

The default limits are:

| Option | Default |
| --- | --- |
| URI length | 2,048 characters |
| Pending requests | 32 |
| Dispatch attempts | 3 |
| Retry delay | 250 ms |
| Request age | 5 minutes |

See [External navigation security](docs/external-security.md) for origin rules,
classification, push/QR integration, diagnostics, and testing.

## Deferred navigation

Deferred persistence is separate from external ingress. Register it only when
the app has a real defer-and-replay policy:

```csharp
services.AddAppNavFileDeferredNavigationRequests(options =>
{
    options.BaseUri = new Uri("https://example.com/");
    options.RouteStateRegistry = routeStateRegistry;
});
```

`BaseUri` is explicit and required. Schema 3 stores only:

- canonical route URI;
- request source and disposition;
- timestamp and window ID;
- restorable route metadata;
- provenance provider.

It does not store the raw transport URI, original or referrer URI, correlation
ID, arbitrary provenance attributes, or cold-start state. Restore produces a
route-backed request rather than replaying untrusted transport input.

Default file-store limits are 32 requests, 64 KiB, and seven days. Old entries
are pruned and overflow drops the oldest. Schema-2 preview files are reset once.
Future schemas are quarantined byte-for-byte so a downgrade cannot destroy data.

See [Deferred navigation](docs/deferred-navigation.md).

## Logical topology and MAUI limits

Core supports these v1 node shapes:

- `WindowNode`;
- `StackNode`;
- `BranchHostNode`;
- `ModalNode`.

`NavigationNode` is externally non-derivable. Unknown shapes are rejected before
presentation. A non-null `ActiveWindowId` must identify an existing window.

Core retains multi-window state types for future adapters. The MAUI presenter
accepts at most one window per plan. Before a window is attached, startup may
present that window's state. After attachment, every plan must target the same
window ID. Validation happens before native mutation.

## Ownership boundary

| Core owns | Adapter owns |
| --- | --- |
| Routes and formatting | UI artifacts and page factories |
| Request transforms and policy | Host lifecycle and thread affinity |
| Canonical/contextual planning | Native container mutation |
| Logical state and history | Native-to-logical reconciliation |
| Diagnostics contracts | DI lifetimes and scope release |
| Navigation orchestration | Ingress and storage integration |

This boundary is the extension seam for a future Blazor adapter. Such an adapter
would map the same routes and logical plans to components and browser history,
while owning its own lifecycle, storage, URI ingress, and mapping rules.

See [Adapter contract](docs/adapter-contract.md).

## Diagnostics

Safe structural diagnostics are the default. They report event kind, phase,
types, counts, decisions, retry/overflow/expiry state, and sanitized origins.
They do not emit raw query values or provenance fields.

Use `AddAppNavDiagnostics` to configure data mode. Full mode is an explicit app
decision. Applications can register `INavigationDiagnosticRedactor` for domain
specific values; redactor failure falls back to safe output and cannot break
navigation.

See [Diagnostics](docs/diagnostics.md).

## Testing and release gates

The canonical deterministic gate is:

```bash
eng/verify.sh release
```

Run it twice from a clean worktree for a release candidate. Focused modes are:

```bash
eng/verify.sh contracts
eng/verify.sh packages
eng/verify.sh native
```

The release gate covers unit/API/generator/adapter contracts, allocation budgets,
supported-target builds, trim/AOT analysis, representative native publishes,
package content, and an isolated cold-cache MAUI package consumer.

Android builds require JDK 21. CI uses SDK `10.0.400`, workload set
`10.0.302.1`, and the root `global.json`.

Runtime tests attach AppNav to a real MAUI `Window` and execute on the main
thread. A runtime lane fails on zero tests, unexpected skips, missing result
files, crash markers, consistency faults, or retained scopes.

See [Testing](docs/testing.md) and
[Public preview release checklist](docs/release-checklist.md).

## Samples

### Getting Started

[`samples/GettingStarted.Sample`](samples/GettingStarted.Sample/README.md) is the
minimal onboarding path: stack model, generated routes/pages, typed fallback,
Home -> Detail, and native Back.

### Commerce

[`samples/Commerce.Sample`](samples/Commerce.Sample/README.md) is the advanced
sample. It uses `BranchHostNavigationModel`, independent tab stacks, every
standard disposition, trusted HTTPS and custom-scheme origins, compatibility
transforms, generated mappings, and native reconciliation. It intentionally does
not register a deferred store because it does not implement a real auth flow.

### Scavos dogfood

Scavos is the advanced source-level dogfood application during preview hardening.
It exercises five independent stacks, custom planning, auth defer/replay, Branch,
push, QR, direct links, startup races, and native Back/tab behavior. Scavos keeps
source overrides so AppNav can be validated before package publication.

## Design constraints

- AppNav is not a replacement for domain state, view models, DI, or auth.
- Routes describe semantic destinations, not concrete pages.
- Primary navigation structure belongs in topology, not query parameters.
- App-owned external providers must attach their own provenance and trust rules.
- A presenter commit is transactional: logical state advances only after apply.
- Native user actions reconcile through host-neutral `HostBack`,
  `BranchChanged`, and `HostReconciliation` vocabulary.
- Page scopes must be released after pop, replace, rollback, and shutdown.
- MAUI apps should not let another framework simultaneously own the same stacks.

## Documentation

- [Getting started](docs/getting-started.md)
- [Topology and planning](docs/topology.md)
- [External navigation security](docs/external-security.md)
- [Deferred navigation](docs/deferred-navigation.md)
- [Testing](docs/testing.md)
- [Diagnostics](docs/diagnostics.md)
- [Adapter contract](docs/adapter-contract.md)
- [Provenance](docs/provenance.md)
- [Public preview release checklist](docs/release-checklist.md)

## Versioning and publication

Local builds pack as `0.1.0-preview.local`. A stable version is rejected unless
`AppNavStableRelease=true` is explicit. A validated `v0.1.0-preview.1` tag
overrides the package version.

The tag workflow consumes already-validated package artifacts, calculates
SHA-256 hashes, and creates a GitHub prerelease. It does not rebuild packages
and does not publish to NuGet.org.

The repository and package metadata target:

```text
https://github.com/AdamEssenmacher/AdamE.AppNav
```

Stable `1.0` qualification, prolonged dogfood metrics, NuGet.org publication,
and a bounded Blazor feasibility probe are later phases.

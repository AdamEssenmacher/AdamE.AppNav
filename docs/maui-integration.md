# MAUI Integration

This guide walks through one common MAUI integration shape for AppNav. It is a complete example, not the only public way to use the library.

## Common Flow

1. Define durable semantic destinations as `AppRoute`.
2. Use `AppRoute`, `AppRouteRequest`, `Uri`, or `RouterNavigationRequest` based on how much route metadata or runtime transport context the caller needs.
3. Keep route-owned metadata app-defined in a `RouteStateRegistry` when formatting, persistence, or downstream app behavior depend on it.
4. Register `AddAppNav(...)`, optional persistence or deferred-request services, and `AddAppNavStartup(...)` as needed by the app.
5. Use `IAppNavStartupService`, `IMauiExternalNavigationDispatcher`, or direct `IRouterNavigator` calls where MAUI lifecycle or transport-aware boundaries need explicit control.

```text
App code / host code     Boundary / lifecycle        Router runtime
---------------------    ---------------------       --------------
AppRoute / AppRouteRequest / Uri / RouterNavigationRequest
                                              ->    transformers -> match -> policies -> planner -> presenter
```

## App-Authored Navigation

`AppRouteRequest` is useful in:

- route factories
- button handlers and view models
- URL formatting and sharing
- compatibility normalization that is still app-authored

Typical app code:

```csharp
await navigator.NavigateAsync(
    AppRouteRequest
        .For(new ProductDetailRoute("northwind", 123, "blue", "spring"))
        .WithMetadata(new RouteMetadataKey<string>("campaign"), "spring-sale"),
    NavigationRequestSource.InAppCommand);
```

Use plain `AppRoute` when no route-owned metadata is involved and the simpler overload is clearer.

## Route-Owned Metadata

Keep route-owned metadata app-defined in a `RouteStateRegistry`.

- canonical metadata belongs in the URI shape
- restorable metadata belongs in persistence
- ephemeral metadata belongs only in live in-memory requests and state

Register that registry with navigation persistence when route-owned metadata participates in formatting or restore.

MAUI page constructors still receive `AppRoute`, not route-entry metadata. If a page needs route-entry metadata, use `IMauiRoutePageLifecycleHook` or app-specific mapping.

## Runtime And External Boundaries

`RouterNavigationRequest` is useful when URI, source, provenance, disposition, request metadata, or window targeting matter explicitly.

Common runtime uses:

- app-link ingress
- app-owned push and QR boundaries
- startup fallback
- restore
- request-policy pipelines
- pre-match request transformation
- deferred request replay
- tests

`RouterNavigationRequest` can carry `NavigationRequestProvenance`, which is runtime request context: provider, original URI, referrer URI, correlation id, cold-start flag when known, and string attributes. Keep provenance out of `AppRoute`, `AppRouteRequest`, route formatting, and `RouteStateRegistry`. AppNav sets transport provenance it owns; apps set provider/business provenance they own. For field ownership, see [provenance.md](provenance.md).

A request always has exactly one target. Create it with `FromUri`, `FromRoute`, or `FromRouteRequest`, and use
`WithTarget(Uri)` or `WithTarget(AppRoute)` when a transformer or policy replaces that target.

Typical boundary code:

```csharp
var branchUri = new Uri("https://example.com/stores/northwind/products/123?variant=blue&promo=spring&campaign=spring-launch");
var request = RouterNavigationRequest.FromUri(
    branchUri,
    NavigationRequestSource.AppLink,
    provenance: new NavigationRequestProvenance(
        provider: "branch",
        originalUri: branchUri,
        correlationId: branchCorrelationId));
```

## MAUI Wiring And Startup

One common MAUI setup uses:

- `AddAppNav(...)`
- optional persistence or deferred-request registration
- `AddAppNavPages(...)` when page mappings live outside the composition root
- `AddAppNavStartup(...)`
- `IAppNavStartupService.StartAsync(window)` from `CreateWindow`

A common cold-start sequence is:

1. buffered app links
2. deferred request detection
3. fallback request
4. window attachment

App-link lifecycle ingress targets one active AppNav MAUI host per process. The most recently created host receives
platform callbacks. Disposing that host cancels its in-flight external navigation and drops its queued requests;
callbacks arriving after disposal remain buffered until a replacement host is created. Disposing an older host does
not unregister a newer one.

`AddAppNav(...)` discovers `INavigationRequestTransformer` and `INavigationRequestPolicy` registrations in order.
Transformers run before route matching, including for unmatched and redirected targets. Policies run after matching and
use `NavigationRequestPolicyContext.Route` for the resolved route and `RouteMetadata` for metadata produced by the
current match. `Request.Metadata` contains only explicit request-envelope metadata. Returning `WithTarget(...)`
preserves the request envelope; returning a newly constructed request authoritatively replaces it. Route metadata from
an old target never crosses a redirect.

The MAUI `AppNavRuntime` is the sole shutdown owner for its factory-created navigator and presenter. Both disposal
forms stop admission and cancel accepted work; asynchronous disposal additionally waits for rollback, native cleanup,
page release, and async scope disposal. Work past the presentation commit point completes successfully. Public
presentation surfaces force runtime creation so DI cannot dispose presenter dependencies independently.

Deferred replay is lease-based and at-least-once. Acquiring a lease does not remove persisted requests; successful
navigation is removed only after durable acknowledgement. A crash between presentation and acknowledgement may replay
once more but cannot lose the request. Persisted deferred data accepts schema 2 exactly. MAUI startup clears unsupported
schema-1 or future preview data and continues through fallback startup. Malformed JSON and invalid schema-2 content are
atomically renamed to an adjacent `*.invalid-{utc}-{guid}` file and replaced with an empty queue. A quarantine failure
preserves the original and fails startup; unexpected custom-store exceptions are never cleared automatically.

Diagnostics use `NavigationDiagnosticDataMode.Safe` by default for observers, logging, and activities. Call
`AddAppNavDiagnostics(...)` to opt into Full mode, and register `INavigationDiagnosticRedactor` for app-specific
redaction. Redactor failures fall back to built-in Safe output without affecting navigation.

## Other Public Runtime Seams

`IRouterNavigator` also exposes URI navigation overloads and `ReconcileAsync(...)`. Those are useful for host-owned orchestration, testing, and explicit runtime control.

## Route-Owned Presentation Pages

Some workflows are one semantic destination but need several native pages. A setup wizard, checkout flow, or editor can remain one `AppRoute` while its steps participate in iOS swipe-back and Android system back.

Register each presentation page with DI, then inject `IMauiRoutePresentationNavigator` into the route page or its presentation model:

```csharp
services.AddTransient<ShippingPage>();
services.AddTransient<ReviewPage>();

await presentationNavigator.PushAsync<ShippingPage>("shipping");
await presentationNavigator.PushAsync<ReviewPage>("review");
```

Each logical route entry owns a native segment consisting of its route page followed by its presentation pages. AppNav preserves that segment while the route entry remains in the logical stack, even when another logical route temporarily covers it. Removing the owner route releases the entire segment.

Presentation pages:

- do not add routes, route entries, or logical history
- inherit the owning route page binding context by default
- are resolved in independent DI scopes and released when popped
- require a nonblank key unique within their owner segment
- are transient and are not restored after process recreation

Route pages can implement scoped, asynchronous lifecycle behavior through constructor-injected
`IMauiRoutePageLifecycleHook` services. Its `OnPageCreatedAsync`, `OnPageUpdatedAsync`, and `OnPageReleasedAsync`
methods receive cancellation where appropriate and no `IServiceProvider`. A rollback may issue a compensating update
with the prior route entry, so update hooks must support that call.

Logical presentation is transactional. AppNav stages replacements, retains removed pages until commit, suppresses
transient reconciliation, verifies the target, and only then releases retired pages. Before commit, failure or
cancellation restores the prior native/logical state. A failed rollback triggers a verified full rebuild; failure of
both recovery paths faults the presenter closed with `MauiPresentationConsistencyException`.

Native back automatically pops the presentation page without reconciling away the logical route. For an explicit presentation-only back command, call `IMauiRoutePresentationNavigator.PopAsync()`. `IRouterNavigator.BackAsync()` remains deliberately logical and can remove the owning route.

The active route must be hosted by a router-owned `NavigationPage`. Route-only modals without a navigation stack cannot push route-owned pages.

## Notes

- Built-in MAUI app-link ingress sets provenance automatically.
- App-owned external sources should resolve `IMauiExternalNavigationDispatcher`, attach explicit `NavigationRequestProvenance`, and call `Dispatch(...)`.
- Interactive foreground boundaries may call `IRouterNavigator.NavigateAsync(RouterNavigationRequest)` directly when the caller must observe navigation failure to recover UI state.
- AppNav does not ship Branch, push, QR scanner, or auth-provider SDK integrations.
- Raw auth callbacks belong to the auth subsystem; the router should usually see deferred replay or an app-authored post-auth request.

# MAUI integration

[Documentation home](../index.md)

This guide walks through one common MAUI integration shape for AppNav. It is a complete example, not the only public way to use the library.

## Common Flow

1. Define durable semantic destinations as `AppRoute`.
2. Use `AppRoute` or `AppRouteRequest` in app code; construct `RouterNavigationRequest` at runtime or transport boundaries.
3. Keep route-owned metadata app-defined in a `RouteStateRegistry` when formatting, persistence, or downstream app behavior depend on it.
4. Register `AddAppNav(...)`, optional persistence or deferred-request services, and `AddAppNavStartup(...)` as needed by the app.
5. Use `IAppNavStartupService`, `IMauiExternalNavigationDispatcher`, or direct `IRouterNavigator` calls where MAUI lifecycle or transport-aware boundaries need explicit control.

```text
App code / host code     Boundary / lifecycle        Router runtime
---------------------    ---------------------       --------------
AppRoute / AppRouteRequest       RouterNavigationRequest
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
    RouterNavigationDisposition.Contextual);
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

`RouterNavigationRequest` can carry `NavigationRequestProvenance`, which is runtime request context: provider, original URI, referrer URI, correlation id, cold-start flag when known, and string attributes. Keep provenance out of `AppRoute`, `AppRouteRequest`, route formatting, and `RouteStateRegistry`. AppNav sets transport provenance it owns; apps set provider/business provenance they own. For field ownership, see [Requests and provenance](../concepts/requests-and-provenance.md).

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
- `IAppNavStartupService.Start(window, windowId)` from `CreateWindow`

Startup ordering depends on the request source:

```text
buffered app link -> attach window -> await that dispatch epoch
                                       | success: startup completes
                                       + terminal: continue

no successful app link -> detect deferred work -> create fallback if configured
                                                   |
                                                   v
                                      present fallback -> attach window
```

If no fallback is configured, startup still attaches the window. Deferred
request detection reports pending protected work; the owning auth flow decides
when to invoke replay.

App-link lifecycle ingress targets one active AppNav MAUI host per process. The most recently created host receives
platform callbacks. Disposing that host cancels its in-flight external navigation and drops its queued requests;
callbacks arriving after disposal remain buffered until a replacement host is created. Disposing an older host does
not unregister a newer one.

`AddAppNav(...)` owns the unkeyed `IRouterNavigator` registration because that navigator must share the runtime's
built-in MAUI presenter. Do not register or replace an unkeyed navigator when using `AddAppNav(...)`; keyed navigators
may coexist for unrelated flows. Advanced hosts that need a custom navigator must instead own the complete
navigator/presenter pair through `RouterNavigatorFactory` and skip the MAUI `AddAppNav(...)` composition helper.

The final `AddAppNav(...)` callback configures the three app-owned router settings. Its
`FallbackRouteFactory` runs only when a URI has no matching route; `MaxRedirects` and
`MaxHistoryEntries` default to 16 and 128. Startup fallback remains a separate concern configured through
`AddAppNavStartup(...)`:

```csharp
services.AddAppNav(
    routes,
    model,
    pages => pages.AddModule(AppNavGenerated.MauiPageModule),
    navigator =>
    {
        navigator.FallbackRouteFactory = context => new NotFoundRoute(context.Request.Uri!);
        navigator.MaxRedirects = 16;
        navigator.MaxHistoryEntries = 128;
    });
```

Diagnostics, logging, request transformers, request policies, back navigation, presenter ownership, and initial state
remain owned by the MAUI composition root and are not replaceable through this callback.

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
once more but cannot lose the request. Schema 3 persists canonical route-backed requests and safe provider provenance.
Schema-2 preview data is reset once; future schema data is quarantined byte-for-byte for downgrade safety. Malformed or
oversized data is quarantined. A quarantine failure preserves the original and fails safely.

Diagnostics use `NavigationDiagnosticDataMode.Safe` by default for observers, logging, and activities. Safe mode emits
structural types, templates, codes, counts, and timings; it reduces absolute URIs to their origin and omits raw paths,
application-defined navigation ids, and presentation mismatch values. Call `AddAppNavDiagnostics(...)` to opt into Full
mode, and register `INavigationDiagnosticRedactor` for app-specific redaction. Redactor failures fall back to built-in
Safe output without affecting navigation.

## Other Public Runtime Seams

`IRouterNavigator` exposes full-envelope navigation, Back, and `ReconcileAsync(...)`. Four typed extension methods cover app-authored routes. Host-owned boundaries construct a complete `RouterNavigationRequest`.

## Route-owned presentation pages

Workflows that use several native pages for one semantic route are documented
separately in [Route-owned presentation pages](../advanced/route-owned-presentation-pages.md).
That advanced guide covers DI scopes, lifecycle hooks, reentrancy, native Back,
transactional push/pop, rollback, and consistency faults.

## Notes

- Built-in MAUI app-link ingress sets provenance automatically.
- App-owned external sources should resolve `IMauiExternalNavigationDispatcher`, attach explicit `NavigationRequestProvenance`, and call `TryDispatch(...)`.
- Interactive foreground boundaries may call `IRouterNavigator.NavigateAsync(RouterNavigationRequest)` directly when the caller must observe navigation failure to recover UI state.
- AppNav does not ship Branch, push, QR scanner, or auth-provider SDK integrations.
- Raw auth callbacks belong to the auth subsystem; the router should usually see deferred replay or an app-authored post-auth request.

## Next steps

- Define [routing and metadata](../concepts/routing-and-metadata.md).
- Configure [external navigation](external-navigation.md) only after defining trusted origins.
- Add [deferred navigation](deferred-navigation.md) only for a real auth defer/replay flow.
- Diagnose integration failures with [Troubleshooting](troubleshooting.md).

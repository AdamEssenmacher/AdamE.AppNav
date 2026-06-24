# MAUI Integration

This guide walks through one common MAUI integration shape for MauiRouter. It is a complete example, not the only public way to use the library.

## Common Flow

1. Define durable semantic destinations as `AppRoute`.
2. Use `AppRoute`, `AppRouteRequest`, `Uri`, or `RouterNavigationRequest` based on how much route metadata or runtime transport context the caller needs.
3. Keep route-owned metadata app-defined in a `RouteStateRegistry` when formatting, persistence, or downstream app behavior depend on it.
4. Register `AddMauiRouter(...)`, optional persistence or deferred-request services, and `AddMauiRouterStartup(...)` as needed by the app.
5. Use `IMauiRouterStartupService`, `IMauiExternalNavigationDispatcher`, or direct `IRouterNavigator` calls where MAUI lifecycle or transport-aware boundaries need explicit control.

```text
App code / host code     Boundary / lifecycle        Router runtime
---------------------    ---------------------       --------------
AppRoute / AppRouteRequest / Uri / RouterNavigationRequest
                                              ->    policies -> planner -> presenter
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
- deferred request replay
- tests

`RouterNavigationRequest` can carry `NavigationRequestProvenance`, which is runtime request context: provider, original URI, referrer URI, correlation id, cold-start flag when known, and string attributes. Keep provenance out of `AppRoute`, `AppRouteRequest`, route formatting, and `RouteStateRegistry`. MauiRouter sets transport provenance it owns; apps set provider/business provenance they own. For field ownership, see [provenance.md](provenance.md).

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

- `AddMauiRouter(...)`
- optional persistence or deferred-request registration
- `AddMauiRouterPages(...)` when page mappings live outside the composition root
- `AddMauiRouterStartup(...)`
- `IMauiRouterStartupService.StartAsync(window)` from `CreateWindow`

A common cold-start sequence is:

1. buffered app links
2. snapshot restore
3. fallback request
4. window attachment

## Other Public Runtime Seams

`IRouterNavigator` also exposes URI navigation overloads and advanced public operations such as `ReconcileAsync(...)`, `RestoreAsync(...)`, `RestoreFromStoreAsync(...)`, and `WhenReconciliationIdleAsync()`. Those are useful for host-owned orchestration, testing, and explicit runtime control.

## Notes

- Built-in MAUI app-link ingress sets provenance automatically.
- App-owned external sources should resolve `IMauiExternalNavigationDispatcher`, attach explicit `NavigationRequestProvenance`, and call `Dispatch(...)`.
- Interactive foreground boundaries may call `IRouterNavigator.NavigateAsync(RouterNavigationRequest)` directly when the caller must observe navigation failure to recover UI state.
- MauiRouter does not ship Branch, push, QR scanner, or auth-provider SDK integrations.
- Raw auth callbacks belong to the auth subsystem; the router should usually see deferred replay or an app-authored post-auth request.

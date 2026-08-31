# Requests and provenance

[Documentation home](../index.md)

This guide continues the
[Glyphmere pause-menu example](01-routing-and-metadata.md#meet-glyphmere).

Most Glyphmere code can navigate with a typed route: tapping an item creates an
`InventoryItemRoute(itemId)`. A complete `RouterNavigationRequest` is needed
when navigation arrives with additional runtime context, such as a shared map
link or a cloud-save notification.

Navigation provenance is the part of that runtime context that records how the
request entered the router: which provider produced it, its original URI, a
correlation ID, and similar facts. It is not route identity, URL formatting
state, `AppRouteRequest` metadata, or `RouteStateRegistry` state.

The ownership rule is:

```text
AppNav records context for platform links it handles; apps record context from providers they integrate.
```

## Choose the narrowest request type

| Type | Use it for |
| --- | --- |
| `AppRoute` | An app-authored destination with no route-owned metadata, such as `InventoryItemRoute(itemId)` |
| `AppRouteRequest` | An app-authored destination plus canonical, restorable, or ephemeral route metadata, such as an item opened in comparison mode |
| `RouterNavigationRequest` | A complete runtime request that must state URI/route target, source, disposition, timestamp, window, request metadata, or provenance |

The four `RouterNavigatorExtensions` overloads are the normal app-facing path:

```csharp
await navigator.NavigateAsync(new InventoryItemRoute(itemId));
```

App links, push notifications, QR scans, restore operations, and provider
callbacks construct the complete runtime request. A request has exactly one URI
or route target; `WithTarget(Uri)` and `WithTarget(AppRoute)` replace that
target while preserving its runtime context.

## Request pipeline

```text
request -> transformers -> route match -> policies -> planner -> presenter -> commit
```

For a Glyphmere map link, that means:

1. A transformer can replace an old `/pause/map/...` URI with the canonical
   `/pause/world-map/regions/...` URI.
2. Route matching creates `MapRegionRoute(regionId)`.
3. A policy can redirect a locked region or reject navigation that is not
   currently allowed.
4. The planner selects the World Map branch and constructs its canonical stack.
5. The presenter updates native MAUI controls.
6. Only successful presentation commits logical state and history.

Transformers run before matching, including for redirected targets. Use
`WithTarget(...)` to preserve source, disposition, metadata, window, timestamp,
and provenance. Constructing a new request intentionally replaces that runtime
context.

Policies run after a successful match. `NavigationRequestPolicyContext.Route`
is the resolved route; `RouteMetadata` comes from that match;
`Request.Metadata` is explicit request-envelope metadata. `WithTarget(...)`
preserves `Request.Metadata`, including metadata copied from an
`AppRouteRequest`, while match-produced `RouteMetadata` is recomputed for the
redirected target. A policy that must clear or replace request metadata should
construct a new envelope instead. Presentation must succeed before logical
state and history commit.

## Field ownership

| Field | AppNav automatically sets | App should set |
| --- | --- | --- |
| `Provider` | Yes, only for built-in MAUI app-link ingress using `MauiAppLinkProvenanceProviders`. | Yes for Branch, push, QR, provider SDK callbacks, etc. |
| `OriginalUri` | Yes, incoming platform app-link URI. | Yes when the app-owned source has an original URI or SDK-resolved URI. |
| `ReferrerUri` | No. | Only when the provider supplies reliable referrer context. |
| `CorrelationId` | No. | Notification ID, Branch click ID, QR scan ID, deferred request ID, or another stable request correlation ID when available. |
| `IsColdStart` | No. | Only when the app boundary can determine it without guessing. |
| `Attributes` | Empty. | Provider-specific stable string context; not route state and not secrets. |

## Built-in MAUI app links

When `UseAppNavExternalNavigation()` handles a platform app link, AppNav creates
the request and attaches provenance automatically. App code does not need to
add provenance for this path.

Built-in provider names are exposed as constants:

```csharp
MauiAppLinkProvenanceProviders.AndroidIntent
MauiAppLinkProvenanceProviders.IosOpenUrl
MauiAppLinkProvenanceProviders.IosUserActivity
MauiAppLinkProvenanceProviders.MauiAppLink
```

For these built-in paths, AppNav sets:

- `Provider`
- `OriginalUri`

AppNav does not set `ReferrerUri`, `CorrelationId`, `IsColdStart`, or
`Attributes`.

## App-owned external sources

App-owned sources should create a `RouterNavigationRequest` with explicit
`NavigationRequestProvenance` before dispatching it through
`IMauiExternalNavigationDispatcher`.

AppNav does not include provider SDK integrations for Branch, push
notifications, QR scanning, or auth callbacks. App code owns those integrations
and dispatches only the navigation request that the provider produced.

A shared Glyphmere map link resolved by Branch or another app-owned link SDK:

```csharp
var request = RouterNavigationRequest.FromUri(
    resolvedUri,
    NavigationRequestSource.AppLink,
    provenance: new NavigationRequestProvenance(
        provider: "branch",
        originalUri: originalBranchUri,
        correlationId: branchClickId));

if (!externalNavigationDispatcher.TryDispatch(request))
{
    // Recover provider UI or record a structural rejection.
}
```

A Glyphmere cloud-save notification that opens save slot 3:

```csharp
var saveUri = new Uri("https://links.glyphmere.example/pause/saves/3");
var request = RouterNavigationRequest.FromUri(
    saveUri,
    NavigationRequestSource.Push,
    provenance: new NavigationRequestProvenance(
        provider: "glyphmere-cloud-save",
        correlationId: notificationId,
        attributes: new Dictionary<string, string?>
        {
            ["saveRevision"] = saveRevision
        }));

_ = externalNavigationDispatcher.TryDispatch(request);
```

A QR code that shares a Glyphmere map region:

```csharp
var mapUri = new Uri(
    "https://links.glyphmere.example/pause/world-map/regions/ashen-coast");
var request = RouterNavigationRequest.FromUri(
    mapUri,
    NavigationRequestSource.QrCode,
    provenance: new NavigationRequestProvenance(
        provider: "qr-scanner",
        originalUri: mapUri,
        correlationId: scanId));

_ = externalNavigationDispatcher.TryDispatch(request);
```

If a QR scanner is an interactive foreground surface, it may call
`IRouterNavigator.NavigateAsync(RouterNavigationRequest)` directly instead of
the fire-and-forget dispatcher so it can observe navigation failure and recover
scanner UI state. The request should still use
`NavigationRequestSource.QrCode` and explicit QR provenance.

Set `IsColdStart` only when the app boundary can determine it deterministically.
Do not use provenance attributes for secrets or route state.

## Auth callbacks

`NavigationRequestSource` deliberately has no `AuthCallback` member. A raw auth
provider callback should terminate in the app's auth subsystem rather than
entering the router as a fabricated navigation source.

The router should usually see one of these instead:

- deferred replay after the auth subsystem completes sign-in;
- an app-authored post-auth `AppRouteRequest`;
- an app-authored `RouterNavigationRequest` only when the completed auth flow
  resolves to a real navigation intent.

## Next steps

- Define [routing and metadata](01-routing-and-metadata.md).
- See how routes become [topology and plans](02-topology-and-planning.md).
- Handle [navigation outcomes and failures](../guides/04-navigation-outcomes-and-failure-handling.md).
- Configure [external navigation](../guides/05-external-navigation.md).
- Persist only safe fields with
  [deferred navigation](../guides/06-deferred-navigation.md).

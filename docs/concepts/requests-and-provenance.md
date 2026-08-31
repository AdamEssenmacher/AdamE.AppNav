# Requests and provenance

[Documentation home](../index.md)

Navigation provenance is runtime request context. It describes how a `RouterNavigationRequest` entered the router. It is not route identity, URL formatting state, `AppRouteRequest` metadata, or `RouteStateRegistry` state.

The ownership rule is:

```text
AppNav sets transport provenance it owns; apps set provider/business provenance they own.
```

## Choose the narrowest request type

| Type | Use it for |
| --- | --- |
| `AppRoute` | An app-authored destination with no route-owned metadata |
| `AppRouteRequest` | An app-authored destination plus canonical, restorable, or ephemeral route metadata |
| `RouterNavigationRequest` | A boundary that must state URI/route target, source, disposition, timestamp, window, envelope metadata, or provenance |

The four `RouterNavigatorExtensions` overloads are the normal app-facing path.
App links, push, QR, restore, and provider boundaries construct the full runtime
envelope. A request has exactly one URI or route target; `WithTarget(Uri)` and
`WithTarget(AppRoute)` replace that target while preserving the envelope.

## Request pipeline

```text
request -> transformers -> route match -> policies -> planner -> presenter -> commit
```

Transformers run before matching, including for redirected targets. Use
`WithTarget(...)` to preserve source, disposition, metadata, window, timestamp,
and provenance. Constructing a new request intentionally replaces the envelope.

Policies run after a successful match. `NavigationRequestPolicyContext.Route`
is the resolved route; `RouteMetadata` comes from that match;
`Request.Metadata` is explicit envelope metadata. Metadata from an old target
does not cross a redirect. Presentation must succeed before logical state and
history commit.

## Field ownership

| Field | AppNav Automatically Sets | App Should Set |
| --- | --- | --- |
| `Provider` | Yes, only for built-in MAUI app-link ingress using `MauiAppLinkProvenanceProviders`. | Yes for Branch, push, QR, provider SDK callbacks, etc. |
| `OriginalUri` | Yes, incoming platform app-link URI. | Yes when the app-owned source has an original URI or SDK-resolved URI. |
| `ReferrerUri` | No. | Only when the provider supplies reliable referrer context. |
| `CorrelationId` | No. | Notification id, Branch click id, QR scan id, deferred request id, or other stable request correlation id when available. |
| `IsColdStart` | No. | Only when the app boundary can determine it without guessing. |
| `Attributes` | Empty. | Provider-specific stable string context; not route state, not secrets. |

## Built-in MAUI app links

When `UseAppNavExternalNavigation()` handles a platform app link, AppNav creates the request and attaches provenance automatically. App code does not need to add provenance for this path.

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

AppNav does not set `ReferrerUri`, `CorrelationId`, `IsColdStart`, or `Attributes`.

## App-owned external sources

App-owned sources should create a `RouterNavigationRequest` with explicit `NavigationRequestProvenance` before dispatching it through `IMauiExternalNavigationDispatcher`.

AppNav does not include provider SDK integrations for Branch, push notifications, QR scanning, or auth callbacks. App code owns those bridges and dispatches only the navigation request that the provider produced.

Branch or another app-owned link SDK:

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

Push notification:

```csharp
var request = RouterNavigationRequest.FromUri(
    notificationUri,
    NavigationRequestSource.Push,
    provenance: new NavigationRequestProvenance(
        provider: "firebase-push",
        correlationId: notificationId,
        attributes: new Dictionary<string, string?>
        {
            ["campaignId"] = campaignId,
            ["messageId"] = messageId
        }));

_ = externalNavigationDispatcher.TryDispatch(request);
```

QR scan:

```csharp
var request = RouterNavigationRequest.FromUri(
    scannedUri,
    NavigationRequestSource.QrCode,
    provenance: new NavigationRequestProvenance(
        provider: "qr-scanner",
        originalUri: scannedUri,
        correlationId: scanId));

_ = externalNavigationDispatcher.TryDispatch(request);
```

If a QR scanner is an interactive foreground surface, it may call `IRouterNavigator.NavigateAsync(RouterNavigationRequest)` directly instead of the fire-and-forget dispatcher so it can observe navigation failure and recover scanner UI state. The request should still use `NavigationRequestSource.QrCode` and explicit QR provenance.

Set `IsColdStart` only when the app boundary can determine it deterministically. Do not use provenance attributes for secrets or route state.

## Auth callbacks

Do not add or simulate a `NavigationRequestSource.AuthCallback` for raw auth provider callbacks. The auth callback should terminate in the app's auth subsystem.

The router should usually see one of these instead:

- deferred replay after the auth subsystem completes sign-in
- an app-authored post-auth `AppRouteRequest`
- an app-authored `RouterNavigationRequest` only when the completed auth flow resolves to a real navigation intent

## Next steps

- Define [routing and metadata](routing-and-metadata.md).
- Configure [external navigation](../guides/external-navigation.md).
- Persist only safe fields with [deferred navigation](../guides/deferred-navigation.md).

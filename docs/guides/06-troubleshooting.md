# Troubleshooting

[Documentation home](../index.md)

Start with the exception or diagnostic event, then check the relevant item
below. Safe diagnostics intentionally omit raw paths, query values, and
provenance values. Use [Logging, tracing, and
diagnostics](../reference/diagnostics.md) to configure the standard logger
category, capture one operation by `OperationId`, and interpret phases and
structured data.

The concrete cases below reuse the fictional RPG
[Glyphmere](../concepts/01-routing-and-metadata.md#meet-glyphmere) where a domain
example helps identify the failure.

## A route does not match

- Confirm the URI path matches the attributed template and constraint.
- Confirm path values can be converted by a built-in or registered codec.
- Remember that query parameters do not satisfy path parameters.
- For example, Glyphmere's
  `/pause/inventory/items/{itemId:guid}` route rejects
  `/pause/inventory/items/iron-sword`; the path must contain the item's GUID.
- Inspect `RouteNotMatchedException.Diagnostics` in trusted development output.
- If the URI is legacy input, use an `INavigationRequestTransformer` before
  matching rather than weakening the canonical route.

## Generated routes or pages are missing

- Ensure the route is a concrete, accessible, non-generic `AppRoute` with
  `AppNavRoute`.
- Ensure a mapped MAUI type derives from `Page` and its selected constructor
  accepts the mapped route.
- If `InventoryItemRoute` is generated but cannot be presented, confirm an
  `InventoryItemPage` mapping is generated or registered with the active page
  module.
- Register `AppNavGenerated.CreateRouteTable()` and
  `AppNavGenerated.MauiPageModule` with `AddAppNav`.
- Resolve every `APPNAV` compiler diagnostic; use the
  [generator diagnostic table](../reference/source-generator-diagnostics.md).

## Navigation fails after window attachment

The v1 MAUI presenter accepts one logical window. After attachment, every plan
must target the attached window ID. Use the same nonblank ID in the navigation
model, request, startup configuration, and `Start(window, windowId)` call.
For Glyphmere, a plan using `main` cannot be presented by a host attached as
`game`; those IDs describe different logical windows.

## External ingress is rejected

- External handling must be enabled with `UseAppNavExternalNavigation`.
- At least one trusted root origin must be configured.
- Compare scheme, normalized host, and effective port; paths, credentials,
  queries, and fragments are invalid in configured origins.
- Check request age, URI length, queue capacity, and duplicate suppression.
- A Glyphmere link from `links.glyphmere.example` is still rejected if the app
  trusts a different host or port.
- Use structural external-navigation diagnostics rather than logging the raw
  incoming URI.

## Navigation from a lifecycle hook throws

`IMauiRoutePageLifecycleHook` executes inside the router operation that owns the
page. It cannot re-enter `NavigateAsync`, `BackAsync`, or `ReconcileAsync` on
the same navigator. Schedule follow-up navigation only after the callback and
its owning operation have completed.

For example, a save-slot lifecycle hook must not navigate to
`LoadSaveConfirmationRoute(slot)` synchronously from inside that hook.

## A presentation consistency exception occurs

`MauiPresentationConsistencyException` means apply, rollback, and verified
recovery could not restore a trustworthy native tree. The presenter faults
closed. Record safe diagnostics, dispose the failed host, and recreate the
complete AppNav runtime rather than continuing to mutate the same presenter.

## Deferred data is reset or quarantined

- Schema-2 preview data is deliberately reset during schema-3 migration.
- A future schema is quarantined intact for downgrade safety.
- Corrupt or oversized data is quarantined.
- If quarantine itself fails, AppNav preserves the original and fails safely.

A quarantined Glyphmere save-navigation record is not a quarantined game save;
the deferred store contains navigation requests, not the game's save data.

Use persistence diagnostics to distinguish reset, prune, overflow, corruption,
and future-schema cases. Never inspect or log a raw store in production unless
the application has established an appropriate data-handling policy.

## The release or package gate fails

Run the focused mode reported by the failure. `eng/verify.sh contracts` covers
unit and API contracts; `packages` covers supported builds, package contents,
generators, and the isolated consumer; `native` covers representative publish
modes. Repository maintainers should use the full
[testing guide](../maintainers/testing.md).

## Next steps

- Configure [diagnostics](../reference/diagnostics.md).
- Recheck [MAUI integration](02-maui-integration.md).
- Review [external navigation](04-external-navigation.md) or
  [deferred navigation](05-deferred-navigation.md) for boundary-specific failures.

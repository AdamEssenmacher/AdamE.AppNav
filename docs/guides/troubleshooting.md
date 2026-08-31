# Troubleshooting

[Documentation home](../index.md)

Start with the exception or diagnostic event, then check the relevant item
below. Safe diagnostics intentionally omit raw paths, query values, and
provenance values.

## A route does not match

- Confirm the URI path matches the attributed template and constraint.
- Confirm path values can be converted by a built-in or registered codec.
- Remember that query parameters do not satisfy path parameters.
- Inspect `RouteNotMatchedException.Diagnostics` in trusted development output.
- If the URI is legacy input, use an `INavigationRequestTransformer` before
  matching rather than weakening the canonical route.

## Generated routes or pages are missing

- Ensure the route is a concrete, accessible, non-generic `AppRoute` with
  `AppNavRoute`.
- Ensure a mapped MAUI type derives from `Page` and its selected constructor
  accepts the mapped route.
- Register `AppNavGenerated.CreateRouteTable()` and
  `AppNavGenerated.MauiPageModule` with `AddAppNav`.
- Resolve every `APPNAV` compiler diagnostic; use the
  [generator diagnostic table](../reference/source-generator-diagnostics.md).

## Navigation fails after window attachment

The v1 MAUI presenter accepts one logical window. After attachment, every plan
must target the attached window ID. Use the same nonblank ID in the navigation
model, request, startup configuration, and `Start(window, windowId)` call.

## External ingress is rejected

- External handling must be enabled with `UseAppNavExternalNavigation`.
- At least one trusted root origin must be configured.
- Compare scheme, normalized host, and effective port; paths, credentials,
  queries, and fragments are invalid in configured origins.
- Check request age, URI length, queue capacity, and duplicate suppression.
- Use structural external-navigation diagnostics rather than logging the raw
  incoming URI.

## Navigation from a lifecycle hook throws

`IMauiRoutePageLifecycleHook` executes inside the router operation that owns the
page. It cannot re-enter `NavigateAsync`, `BackAsync`, or `ReconcileAsync` on
the same navigator. Schedule follow-up navigation only after the callback and
its owning operation have completed.

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
- Recheck [MAUI integration](maui-integration.md).
- Review [external navigation](external-navigation.md) or
  [deferred navigation](deferred-navigation.md) for boundary-specific failures.

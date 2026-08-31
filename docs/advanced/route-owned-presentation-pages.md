# Route-owned presentation pages

[Documentation home](../README.md)

This is an advanced MAUI feature. A setup wizard, checkout flow, or editor can
remain one semantic `AppRoute` while several native pages participate in iOS
swipe-back and Android system Back.

## Push and pop presentation pages

Register each presentation page with DI, then inject
`IMauiRoutePresentationNavigator` into the route page or its presentation
model:

```csharp
services.AddTransient<ShippingPage>();
services.AddTransient<ReviewPage>();

await presentationNavigator.PushAsync<ShippingPage>("shipping");
await presentationNavigator.PushAsync<ReviewPage>("review");
```

Each logical route entry owns a native segment: its route page followed by its
presentation pages. AppNav preserves the segment while the owner remains in the
logical stack and releases the whole segment when the owner is removed.

Presentation pages:

- do not add routes, route entries, or logical history;
- inherit the owner page binding context by default;
- are resolved in independent DI scopes and released when popped;
- require a nonblank key unique within the owner segment;
- are transient and are not restored after process recreation.

The active route must be hosted by a router-owned `NavigationPage`. A route-only
modal without a navigation stack cannot push presentation pages.

## Lifecycle hooks

Route pages can receive scoped `IMauiRoutePageLifecycleHook` services.
`OnPageCreatedAsync`, `OnPageUpdatedAsync`, and `OnPageReleasedAsync` run on the
MAUI main thread. A rollback may issue a compensating update with the previous
route entry, so update hooks must support that call.

A hook cannot synchronously or asynchronously re-enter `NavigateAsync`,
`BackAsync`, or `ReconcileAsync` on the same `IRouterNavigator`. Begin any
follow-up navigation after the hook and its owning router operation complete.

## Transaction and Back behavior

AppNav stages replacements, retains removed pages until commit, suppresses
transient reconciliation, verifies the target, then releases retired pages.
Failure or cancellation before commit restores the prior native and logical
state. A failed rollback triggers verified full recovery; failure of both paths
faults the presenter with `MauiPresentationConsistencyException`.

Native Back pops the top presentation page without removing the logical route.
Call `IMauiRoutePresentationNavigator.PopAsync()` for an explicit
presentation-only Back command. `IRouterNavigator.BackAsync()` remains logical
and may remove the owning route.

## Next steps

- Return to [MAUI integration](../guides/maui-integration.md).
- Diagnose failures with [Troubleshooting](../guides/troubleshooting.md).
- Read the [adapter contract](adapter-contract.md) for transaction ownership.

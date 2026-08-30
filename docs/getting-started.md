# Getting started

Use [`samples/GettingStarted.Sample`](../samples/GettingStarted.Sample/README.md) as the canonical onboarding path.
It is intentionally limited to Home -> Detail -> native Back.

## Requirements

- .NET SDK `10.0.400`
- MAUI workload set `10.0.302.1`
- JDK 21 for Android
- Android API 28+, iOS 15+, or Mac Catalyst 15+

The root `global.json` pins the SDK and workload set.

## Integration sequence

1. Reference `AdamE.AppNav.Maui`.
2. Declare semantic route records with `[AppNavRoute]`.
3. Declare MAUI page classes with `[MauiRoutePage]`.
4. Create a `StackNavigationModel` or `BranchHostNavigationModel`.
5. Register the generated route and page modules with `AddAppNav`.
6. Configure a typed startup `FallbackRouteFactory`.
7. Call `IAppNavStartupService.Start(window, windowId)` from `CreateWindow`.

In-app code navigates with one of four typed extensions:

```csharp
await navigator.NavigateAsync(route);
await navigator.NavigateAsync(route, RouterNavigationDisposition.Canonical);
await navigator.NavigateAsync(routeRequest);
await navigator.NavigateAsync(routeRequest, RouterNavigationDisposition.ReplaceCurrent);
```

URI, push, QR, app-link, and other transport boundaries construct a full `RouterNavigationRequest` instead.

Do not add external navigation or persistence until the app has explicit trusted origins and a real defer/replay flow.
The minimal sample demonstrates the smaller default surface.

# Getting Started sample

This is the smallest supported MAUI onboarding path for AdamE.AppNav: two
generated typed routes, two generated pages, one stack navigation model, a
typed startup fallback, and native Back reconciliation. It intentionally has no
external ingress, deferred persistence, tabs, auth, Shell, or Prism.

Routes, the navigation model, pages, and MAUI registration live together here
so a new reader can see the complete minimum setup. In a larger application,
routes, policies, topology, and navigation-aware view models can move into a
plain `net10.0` project while the MAUI project keeps pages, lifecycle, native
presentation, and platform services. This sample's single-project shape is an
onboarding convenience, not an architectural requirement.

Read the full [Getting started guide](../../docs/guides/01-getting-started.md) or
return to the [repository README](../../README.md).

## Requirements

- .NET SDK `10.0.400`
- MAUI workload set `10.0.302.1`
- JDK 21 for Android
- Android API 28+, iOS 15+, or Mac Catalyst 15+

The repository `global.json` pins the SDK and workload set.

## Build and run

From the repository root:

```sh
dotnet build samples/GettingStarted.Sample/GettingStarted.Sample.csproj \
  -c Debug \
  -f net10.0-maccatalyst \
  -warnaserror
```

Substitute `net10.0-android` or `net10.0-ios` for another supported target.
Launch the selected target through Rider or the normal MAUI run workflow.

## Expected behavior

1. The app starts at Home through a typed canonical fallback.
2. Select **Open detail**.
3. AppNav performs typed in-app navigation to `DetailRoute(42)`.
4. The Detail page displays item `42`.
5. Native Back returns to Home and reconciles the logical state and history.

## Project map

| File | What it demonstrates |
| --- | --- |
| `Routes.cs` | `AppNavRoute` attributes and generated typed route registration |
| `NavigationModel.cs` | Canonical Home and Home -> Detail stack shapes |
| `Pages.cs` | `MauiRoutePage` mappings and typed navigation from app code |
| `MauiProgram.cs` | Startup, generated modules, model, and DI registration |
| `App.cs` | `Window` creation and observed AppNav startup |

The core generator emits `AppNavRoutes.g.cs` and the MAUI generator emits
`AppNavMauiPages.g.cs` beneath `obj/`. Generated files are build output and
should not be edited.

## Next steps

- Learn [routing and metadata](../../docs/concepts/01-routing-and-metadata.md).
- Read the complete [MAUI integration guide](../../docs/guides/02-maui-integration.md).
- Learn how to separate and test navigation decisions in
  [Application architecture and testing](../../docs/guides/03-application-architecture-and-testing.md).
- Explore tabs, dispositions, transforms, and external ingress in the
  [Commerce sample](../Commerce.Sample/README.md).

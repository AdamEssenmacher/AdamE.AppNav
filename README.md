# AdamE.AppNav

Route-first navigation for .NET MAUI.

Define destinations once, then use the same typed routes for in-app navigation,
deep links, native stacks and tabs, Back behavior, restoration, and testing.
AppNav owns the navigation model while MAUI keeps rendering native controls.

## Preview status

The target release is `0.1.0-preview.1`.

- .NET SDK: `10.0.400`
- MAUI workload set: `10.0.302.1`
- Android: API 28+
- iOS: 15+
- Mac Catalyst: 15+
- Local package version: `0.1.0-preview.local`

The preview includes typed route generation and formatting, request transforms
and policy, standard stack and branch-host models, logical history and Back,
native MAUI presentation and reconciliation, trusted external ingress, bounded
retry queues, deferred navigation persistence, safe diagnostics, and
trimming/AOT-oriented validation.

The preview deliberately excludes Windows MAUI, Shell and Prism integration, a
production Blazor adapter, true multi-window MAUI orchestration, a transition or shared-element system,
NuGet.org publication, and stable `1.0` compatibility
guarantees.

Packages will be attached to a GitHub prerelease as `.nupkg` and `.snupkg`
files. A consumer installation workflow will be documented when the publication
path is ready; this preview does not publish to NuGet.org.

## Packages

| Package | Responsibility |
| --- | --- |
| `AdamE.AppNav` | Routes, matching, policy, planning, state, history, diagnostics, orchestration |
| `AdamE.AppNav.Maui` | MAUI pages, native presentation, lifecycle ingress, startup, file persistence |

Referencing `AdamE.AppNav.Maui` brings both source generators transitively: the
core route generator and the MAUI page generator.

## Quickstart

The complete buildable onboarding app is
[`samples/GettingStarted.Sample`](samples/GettingStarted.Sample/README.md). It
has Home -> Detail -> native Back and intentionally contains no external
navigation or persistence.

### 1. Define typed routes

```csharp
[AppNavRoute("/home")]
public sealed record HomeRoute : AppRoute;

[AppNavRoute("/details/{itemId:int}")]
public sealed record DetailRoute(int ItemId) : AppRoute;
```

The route generator emits `AppNavRoutes.g.cs` and a generated route-table
module.

### 2. Declare stack topology

```csharp
public static StackNavigationModel<AppRoute> Create()
{
    return StackNavigationModel<AppRoute>.Create(builder =>
    {
        // This model owns one logical window and one native navigation stack.
        builder.CanonicalSurface("main", "main-stack");

        // Home has one stable entry on the shared "main" stack scope.
        builder.Map<HomeRoute>(recipe => recipe
            .EntryId(_ => "home")
            .ScopeKey(_ => "main"));

        // Each item gets a distinct entry. Canonical navigation rebuilds
        // the complete Home -> Detail stack from any previous state.
        builder.Map<DetailRoute>(recipe => recipe
            .EntryId(route => $"detail-{route.ItemId}")
            .ScopeKey(_ => "main")
            .Canonical((route, metadata) =>
            [
                new StackRouteStep<AppRoute>(new HomeRoute()),
                new StackRouteStep<AppRoute>(route, metadata)
            ]));
    });
}
```

Canonical detail navigation creates Home -> Detail. Contextual detail
navigation can push onto the current compatible stack.

### 3. Map routes to pages and navigate

Annotate the page classes with `[MauiRoutePage(typeof(HomeRoute))]` and
`[MauiRoutePage(typeof(DetailRoute))]`. The MAUI generator emits
`AppNavMauiPages.g.cs` and its `MauiPageModule`. App code then uses a typed
navigation extension:

```csharp
await navigator.NavigateAsync(new DetailRoute(42));
```

The operation is an in-app request with `Auto` disposition. Native Back on the
resulting `NavigationPage` reconciles into the same logical state and history.

### 4. Register AppNav

```csharp
builder.Services.AddAppNavStartup(options =>
{
    options.AppLinkGracePeriod = TimeSpan.Zero;
    options.FallbackRouteFactory = static (_, _) =>
        ValueTask.FromResult<AppRoute?>(new HomeRoute());
});
builder.Services.AddAppNav(
    AppNavGenerated.CreateRouteTable(),
    GettingStartedNavigationModel.Create(),
    pages => pages.AddModule(AppNavGenerated.MauiPageModule));
```

`FallbackRouteFactory` creates the typed startup destination.
`FallbackRequestFactory` remains available for advanced callers that need the
complete request envelope; the factories are mutually exclusive.

### 5. Start from the MAUI window

```csharp
protected override Window CreateWindow(IActivationState? activationState)
{
    var window = new Window(new ContentPage());
    startup.Start(window, "main");
    return window;
}
```

`Start` schedules and observes startup internally. `StartAsync` remains public
for tests and advanced coordination.

Continue with the [Getting started guide](docs/guides/getting-started.md) for
commands, a sample walkthrough, and next steps.

## Core request model

- `AppRoute` is durable semantic destination identity.
- `AppRouteRequest` adds route-owned metadata to a typed route.
- `RouterNavigationRequest` is the complete runtime envelope used at transport
  and host boundaries.
- `IRouterNavigator` exposes full-envelope navigation, logical Back,
  reconciliation, current state, and history.
- `RouterNavigatorExtensions` supplies the four typed app-facing operations.

There are no URI/source convenience overloads on `IRouterNavigator`. App links,
push, QR, restore, and other boundaries construct a complete
`RouterNavigationRequest` so source, disposition, window, and provenance remain
explicit.

The standard planner supports:

| Disposition | Behavior |
| --- | --- |
| `Auto` | Contextual for in-app/test requests; canonical for external sources |
| `Contextual` | Contextual push, then canonical fallback |
| `ReplaceCurrent` | Contextual replace-top, then canonical fallback |
| `Canonical` | Rebuild the model's declared canonical topology |

Native user actions reconcile through host-neutral `HostBack`,
`BranchChanged`, and `HostReconciliation` vocabulary.

## Documentation

Start at the [documentation home](docs/index.md).

- [Getting started](docs/guides/getting-started.md)
- [MAUI integration](docs/guides/maui-integration.md)
- [Routing and metadata](docs/concepts/routing-and-metadata.md)
- [Topology and planning](docs/concepts/topology-and-planning.md)
- [External navigation](docs/guides/external-navigation.md)
- [Deferred navigation](docs/guides/deferred-navigation.md)
- [Troubleshooting](docs/guides/troubleshooting.md)

## Samples

[`GettingStarted.Sample`](samples/GettingStarted.Sample/README.md) is the minimal
onboarding path. [`Commerce.Sample`](samples/Commerce.Sample/README.md) is the
advanced sample with native tabs, independent stacks, all standard
dispositions, trusted origins, compatibility transforms, and external ingress.

Scavos is the source-level dogfood application used during preview hardening. It
is not part of this repository's onboarding path.

## Versioning and publication

Local builds pack as `0.1.0-preview.local`. A stable version is rejected unless
`AppNavStableRelease=true` is explicit. A validated `v0.1.0-preview.1` tag
overrides the package version.

The tag workflow consumes already validated package artifacts, calculates
SHA-256 hashes, and creates a GitHub prerelease. It does not rebuild packages or
publish to NuGet.org.

The project is licensed under the [MIT License](LICENSE).

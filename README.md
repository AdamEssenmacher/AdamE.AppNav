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

## Learn the model first

Powerful application navigation requires a learning investment. AppNav is not
a page-push helper; it asks an application to model three things explicitly:

1. **Destinations**: typed routes describe where the user wants to go without
   naming the page, view model, or UI operation that gets there. A route can be
   presented by one page in a particular state, several native pages, a custom
   control, or another host artifact chosen by the application and adapter.
2. **Topology**: a navigation model declares the valid windows, stacks,
   independent branches, entries, and modals for those destinations.
3. **Request context**: app code uses the narrow typed route API, while external
   and host boundaries preserve source, policy, disposition, and provenance in
   a complete request.

That model requires more learning and configuration than calling `PushAsync`
directly. The investment is deliberate: buttons, deep links, notifications,
startup, restoration, native Back, and tests can all target the same semantic
destinations without each caller reconstructing the surrounding UI.

The investment also creates a useful application boundary. Routes, topology,
policy, and view models that request typed destinations can live in a plain
`net10.0` inner project instead of depending on MAUI pages or native target
frameworks. That can support more than one renderer, but its immediate value is
often simpler, cheaper testing: most navigation decisions can run without MAUI,
a simulator, a device, or a UI thread. The outer host remains responsible for
native presentation, lifecycle ingress, platform configuration, and storage
implementations.

```text
intent -> typed route or complete request -> transform/match -> policy
       -> logical plan -> native presentation -> commit/reconciliation
```

Before copying the setup code, follow the concepts in order:

1. [Why AppNav?](docs/concepts/00-why-appnav.md) explains the design tradeoffs
   and when the model is worth adopting.
2. [Routing and metadata](docs/concepts/01-routing-and-metadata.md) defines
   typed semantic destinations and route-owned state.
3. [Topology and planning](docs/concepts/02-topology-and-planning.md) shows how
   destinations become native navigation shapes.
4. [Requests and provenance](docs/concepts/03-requests-and-provenance.md)
   separates ordinary app navigation from complete runtime requests.

Then use [Application architecture and
testing](docs/guides/03-application-architecture-and-testing.md) to decide which
navigation concerns belong in a render-independent project and which remain in
the MAUI host.

If a small application is adequately served by a few direct page operations,
AppNav may not be the right tradeoff. If navigation must remain coherent across
several entry paths and native UI structures, the explicit model is the source
of that coherence.

## Buildable quickstart

The complete buildable onboarding app is
[`samples/GettingStarted.Sample`](samples/GettingStarted.Sample/README.md). It
has Home -> Detail -> native Back and intentionally contains no external
navigation or persistence. It is the smallest executable application of the
concepts above, not a substitute for them.

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
        // These app-defined stable IDs identify the canonical surface.
        // "main" becomes WindowNode.Id and matches startup.Start(window, "main").
        // "main-stack" becomes StackNode.Id for the NavigationPage-backed stack.
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

The sample uses a convenient one-route-to-one-page mapping. That is its MAUI
presentation choice, not route identity: routes are neither pages nor view
models. A richer route can own additional native presentation pages or be
rendered by an anchor page in route-specific state.

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

Continue with the [Getting started guide](docs/guides/01-getting-started.md) for
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

- [Why AppNav?](docs/concepts/00-why-appnav.md)
- [Getting started](docs/guides/01-getting-started.md)
- [MAUI integration](docs/guides/02-maui-integration.md)
- [Application architecture and testing](docs/guides/03-application-architecture-and-testing.md)
- [Routing and metadata](docs/concepts/01-routing-and-metadata.md)
- [Topology and planning](docs/concepts/02-topology-and-planning.md)
- [External navigation](docs/guides/04-external-navigation.md)
- [Deferred navigation](docs/guides/05-deferred-navigation.md)
- [Troubleshooting](docs/guides/06-troubleshooting.md)

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

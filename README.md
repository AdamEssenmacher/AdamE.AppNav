# AdamE.AppNav

Route-first navigation for .NET MAUI.

Define destinations once, then use the same typed routes for in-app navigation,
deep links, native stacks and tabs, Back behavior, restoration, and testing.
AppNav owns the navigation model while MAUI keeps rendering native controls.

## Try the source sample

The smallest runnable example is Home -> Detail -> native Back. From the
repository root, build it for a supported target:

```sh
dotnet build samples/GettingStarted.Sample/GettingStarted.Sample.csproj \
  -c Debug \
  -f net10.0-maccatalyst \
  -warnaserror
```

Substitute `net10.0-android` or `net10.0-ios` as appropriate, then launch it
through Rider or the normal MAUI run workflow. The [Getting started
guide](docs/guides/01-getting-started.md) explains the files and concepts as
they appear in the sample.

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
native MAUI presentation and reconciliation, platform-native motion for
singular visible stack and modal operations, trusted external ingress, bounded
retry queues, deferred navigation persistence, safe diagnostics, and
trimming/AOT-oriented validation.

The preview deliberately excludes Windows MAUI, Shell and Prism integration, a
production Blazor adapter, true multi-window MAUI orchestration, a transition
or shared-element system, NuGet.org publication, and stable `1.0`
compatibility guarantees.

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

## Navigation ownership: no Shell or Prism navigation

**AppNav does not support `Microsoft.Maui.Controls.Shell`, Shell routing, or
Prism navigation.** It is not an add-on router that can share one window's
navigation authority with another navigation framework.

For an AppNav-managed window, `AdamE.AppNav` owns the semantic routes, logical
topology, planning, Back behavior, state, and history. `AdamE.AppNav.Maui` owns
the corresponding native stacks, branches, modals, lifecycle reconciliation,
and presentation transactions. Do not also use `AppShell`,
`Shell.Current.GoToAsync`, Shell route registration, Prism's navigation
service, or another library that independently owns those same concerns.

Two navigation owners would create competing versions of the current route,
stack, and Back behavior. AppNav cannot make transactional or reconciliation
guarantees around native changes issued behind its presenter.

This boundary does not prescribe the rest of the application architecture.
MVVM libraries, dependency-injection containers, messaging libraries, and
other non-navigation features may coexist when they do not mutate the
AppNav-managed navigation surface. Moving an existing Shell- or Prism-navigated
window to AppNav is a replacement migration, not an interoperability switch in
this preview. See [MAUI
integration](docs/guides/02-maui-integration.md#one-navigation-owner-no-shell-or-prism-navigation).

## Why the model is explicit

AppNav treats navigation as an application model with three explicit parts:

1. **Destinations**: typed routes describe where the user wants to go without
   naming the page, view model, or UI operation that gets there. A route can be
   presented by one page in a particular state, several native pages, a custom
   control, or another host artifact chosen by the application and adapter.
2. **Topology**: a navigation model declares the valid windows, stacks,
   independent branches, entries, and modals for those destinations.
3. **Request context**: app code uses the narrow typed route API, while external
   and host boundaries preserve the target, source, disposition, timestamp,
   window, metadata, and provenance in a complete request.

This model takes more setup than a direct page push, and it gives every entry
path the same vocabulary. Buttons, deep links, notifications, startup,
restoration, native Back, and tests can all target the same semantic
destinations without reconstructing the surrounding UI.

The same model creates a useful application boundary. Routes, topology, policy,
and view models that request typed destinations can live in a plain
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

The concept guides develop this model in order: [Why
AppNav?](docs/concepts/00-why-appnav.md), [Routing and
metadata](docs/concepts/01-routing-and-metadata.md), [Topology and
planning](docs/concepts/02-topology-and-planning.md), and [Requests and
provenance](docs/concepts/03-requests-and-provenance.md). Read them as the
sample introduces unfamiliar terms or before designing a larger topology. Use
[Application architecture and
testing](docs/guides/03-application-architecture-and-testing.md) when deciding
which concerns belong in a render-independent project and which remain in the
MAUI host.

If a small application is adequately served by a few direct page operations,
AppNav may not be the right tradeoff. If navigation must remain coherent across
several entry paths and native UI structures, the explicit model is the source
of that coherence.

## How the sample works

The complete buildable onboarding app is
[`samples/GettingStarted.Sample`](samples/GettingStarted.Sample/README.md). It
has Home -> Detail -> native Back and intentionally contains no external
navigation or persistence. The five steps below are sourced directly from that
sample.

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

        // "main" makes Home and Detail eligible for contextual navigation
        // within the current stack; it is not the "main-stack" structural ID.
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

The sample deliberately uses the simplest route-to-page mapping. See [A route
is neither a page nor a view
model](docs/concepts/00-why-appnav.md#a-route-is-neither-a-page-nor-a-view-model)
for the more flexible presentation boundary.

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

## Documentation

Start at the [documentation home](docs/index.md).

- [Why AppNav?](docs/concepts/00-why-appnav.md)
- [Glossary](docs/reference/glossary.md)
- [Getting started](docs/guides/01-getting-started.md)
- [MAUI integration](docs/guides/02-maui-integration.md)
- [Application architecture and testing](docs/guides/03-application-architecture-and-testing.md)
- [Navigation outcomes and failure handling](docs/guides/04-navigation-outcomes-and-failure-handling.md)
- [Routing and metadata](docs/concepts/01-routing-and-metadata.md)
- [Topology and planning](docs/concepts/02-topology-and-planning.md)
- [External navigation](docs/guides/05-external-navigation.md)
- [Deferred navigation](docs/guides/06-deferred-navigation.md)
- [Troubleshooting](docs/guides/07-troubleshooting.md)
- [Logging, tracing, and diagnostics](docs/reference/diagnostics.md)

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

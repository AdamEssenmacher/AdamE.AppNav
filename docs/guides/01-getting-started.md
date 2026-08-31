# Getting started

[Documentation home](../index.md)

This guide uses the buildable
[`GettingStarted.Sample`](../../samples/GettingStarted.Sample/README.md). It is
deliberately limited to Home -> Detail -> native Back so the core AppNav model
is visible without external ingress, persistence, tabs, or auth. The concept
guides use the richer fictional RPG [Glyphmere](../concepts/01-routing-and-metadata.md#meet-glyphmere)
to explain independent branches and nested destinations; Home -> Detail is the
smallest executable version of the same route -> plan -> presentation flow.

## Read the core concepts first

Before copying the setup code, understand the three boundaries introduced by
[routing and metadata](../concepts/01-routing-and-metadata.md), [topology and
planning](../concepts/02-topology-and-planning.md), and [requests and
provenance](../concepts/03-requests-and-provenance.md). Use the
[Glossary](../reference/glossary.md) whenever a term such as route, entry, plan,
presentation, or provenance is unfamiliar.

The minimum mental model is: app code requests a semantic destination, a
navigation model plans the logical topology for it, and the MAUI presenter
materializes that topology with native controls. The walkthrough below puts
those concepts together in the smallest supported application.

The sample intentionally keeps routes, the navigation model, pages, and MAUI
composition in one project. That makes the minimum setup visible; it is not a
recommendation that a larger application put navigation decisions in its
rendering layer. A layered application can move its routes, route metadata,
navigation model, policies, and view models that request typed destinations
into a plain `net10.0` project while the MAUI project retains pages, lifecycle,
native presentation, and platform services. See [Application architecture and
testing](03-application-architecture-and-testing.md).

## Requirements

- .NET SDK `10.0.400`
- MAUI workload set `10.0.302.1`
- JDK 21 for Android
- Android API 28+, iOS 15+, or Mac Catalyst 15+

The repository `global.json` pins the SDK and workload set. Preview packages are
planned as GitHub prerelease assets and are not published to NuGet.org. This
guide intentionally does not prescribe a consumer package-source workflow yet;
run the source sample to evaluate the current preview.

## Run the source sample

From the repository root, build a supported target:

```sh
dotnet build samples/GettingStarted.Sample/GettingStarted.Sample.csproj \
  -c Debug \
  -f net10.0-maccatalyst \
  -warnaserror
```

Substitute `net10.0-android` or `net10.0-ios` as appropriate. Launch from Rider
or use the normal MAUI target run workflow for the selected platform.

When the app opens:

1. Home is the initial route.
2. Select **Open detail**.
3. Detail displays item `42`.
4. Use the native Back button or gesture.
5. AppNav reconciles the native pop and Home becomes current logically and
   natively.

## 1. Define semantic routes

The following block is sourced from `Routes.cs` region
`getting-started-routes`:

```csharp
[AppNavRoute("/home")]
public sealed record HomeRoute : AppRoute;

[AppNavRoute("/details/{itemId:int}")]
public sealed record DetailRoute(int ItemId) : AppRoute;
```

`HomeRoute` and `DetailRoute` describe destinations. They do not refer to MAUI
pages. The route generator emits `AppNavRoutes.g.cs` and
`AppNavGenerated.CreateRouteTable()`.

## 2. Declare logical topology

The following block is sourced from `NavigationModel.cs` region
`getting-started-model`:

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

The canonical detail shape is Home -> Detail. Contextual detail navigation can
push onto a compatible current stack; canonical navigation can rebuild the
declared shape from any prior state.

## 3. Map routes to MAUI pages

`Pages.cs` applies `[MauiRoutePage(typeof(HomeRoute))]` and
`[MauiRoutePage(typeof(DetailRoute))]`. The MAUI generator emits
`AppNavMauiPages.g.cs` and its `MauiPageModule`. Page constructors receive the
typed route and any services supplied by DI.

This minimal sample uses one page per route because that is the clearest
onboarding shape, not because a route and page are the same concept. The route
identifies Home or Detail; the MAUI adapter decides which page presents it.
More advanced routes can own several native presentation pages, and a page can
render a route through custom controls or route-specific state. View models may
request or consume routes, but their types are not routes either.

The Home button uses the buildable `getting-started-typed-navigation` region:

```csharp
await navigator.NavigateAsync(new DetailRoute(42));
```

This is an in-app `Auto` request. The standard planner treats it contextually
when possible and falls back to the route's canonical topology.

## 4. Register generated modules and startup

The following block is sourced from `MauiProgram.cs` region
`getting-started-registration-services`:

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

The startup fallback becomes an in-app canonical request for the startup
window. `FallbackRequestFactory` is the mutually exclusive advanced option for
constructing the complete request envelope.

## 5. Start from `CreateWindow`

The following block is sourced from `App.cs` region
`getting-started-window-start`:

```csharp
protected override Window CreateWindow(IActivationState? activationState)
{
    var window = new Window(new ContentPage());
    startup.Start(window, "main");
    return window;
}
```

`Start` schedules and observes startup internally. `StartAsync` exists for tests
and explicit coordination. Keep the window ID consistent with the model and any
targeted requests.

## Sample file map

| File | Responsibility |
| --- | --- |
| `Routes.cs` | Typed semantic routes and route-generation input |
| `NavigationModel.cs` | Canonical and contextual stack behavior |
| `Pages.cs` | Generated route-to-page mappings and typed navigation |
| `MauiProgram.cs` | DI, generated modules, and startup fallback |
| `App.cs` | Window creation and AppNav startup |

## Generated code

Generated files live below `obj/` and should not be edited. If
`AppNavGenerated` is missing, inspect compiler diagnostics and verify that the
route and page projects reference the expected packages or project references.
Use the [source-generator diagnostics](../reference/source-generator-diagnostics.md)
for every `APPNAV` code.

## Next steps

- Revisit the [core concepts](../concepts/01-routing-and-metadata.md) as the sample's
  route and topology choices become concrete.
- Add application wiring with [MAUI integration](02-maui-integration.md).
- Separate inner navigation decisions from the renderer with
  [Application architecture and testing](03-application-architecture-and-testing.md).
- Learn how awaited navigation, Back, and failures complete in
  [Navigation outcomes and failure handling](04-navigation-outcomes-and-failure-handling.md).
- Explore native tabs and external ingress in the
  [Commerce sample](../../samples/Commerce.Sample/README.md).

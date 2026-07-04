# AdamE.MauiRouter

AdamE.MauiRouter is a mobile-first, URL-native routing and navigation library for .NET MAUI apps.

MAUI's default navigation model inherits a page-first design from Xamarin.Forms, from a time before Universal Links and App Links became the normal contract between apps, the OS, and the web. Back then it was more common to treat "mobile navigation" and "URL navigation" as separate systems.

That distinction holds up less well now. Modern mobile apps receive navigation intent from everywhere: in-app taps, Universal Links, App Links, push notifications, QR codes, restore flows, and deferred replay after sign-in. Those should not require seven different navigation architectures.

AdamE.MauiRouter starts from a different assumption: the durable navigation contract should be a route/URL, not a page push. A URL identifies where the user should be semantically. The app decides how that intent maps to authenticated state, tabs, stacks, modals, and the active MAUI presentation structure. The MAUI adapter then materializes native containers and reconciles native user gestures back into logical state and history.

```text
https://example.com/stores/northwind/products/123?variant=blue&promo=spring

Route matching  -> ProductDetailRoute("northwind", 123, "blue", "spring")
App planning    -> store tabs, catalog selected, catalog stack, product detail
Presentation    -> real MAUI TabbedPage, NavigationPage stack, ProductDetailPage
```

The core idea:

```text
URLs identify durable application state, not concrete MAUI pages.
```

The core state model includes window targeting and window nodes, but the v1 MAUI adapter is intentionally single-active-window oriented rather than a full multi-window orchestration system.

## Route Identity And Route-Owned Metadata

MauiRouter deliberately separates route identity, route-owned metadata, and runtime transport context.

- `AppRoute` is the durable semantic destination.
- `AppRouteRequest` is route identity plus app-owned route metadata.
- `RouteStateRegistry` classifies metadata as canonical, restorable, or ephemeral.

That split matters because it keeps route constructors focused on durable identity while still letting the app attach URL-affecting, persistence-affecting, or policy/planning-visible metadata when needed.

Typical examples:

- campaign metadata is canonical and may shape or share the durable URL
- return-path metadata is restorable and may survive navigation persistence
- trace or correlation metadata is ephemeral and stays runtime-only

Provenance is different. It is runtime request context carried on `RouterNavigationRequest`, not route identity and not route-owned metadata.

See the quick start section below for a longer `RouteStateRegistry` example and how those lifetimes plug into route formatting and persistence.

## Why This Exists

MAUI apps often start navigation from pages, Shell route names, view model names, or platform-specific deep-link callbacks. Those approaches work for simple flows, but they usually make URLs an afterthought.

That becomes increasingly painful when navigation needs to answer questions like:

- Where did this link come from?
- Is this origin trusted?
- Is this a cold-start navigation or a warm in-app navigation?
- Should the route be allowed right now, normalized, redirected, deferred, or rejected?
- If the user is signed out, should we bounce to login and replay later?
- Given current app state, which tab, stack, modal, or active host should represent this route?

MauiRouter keeps those concerns out of page constructors and ad hoc platform glue. It centralizes them in route matching, request policy, app planning, and presentation.

## Why URL-First Navigation

A URL-first navigation model is stronger than a page-first model because it gives the app one durable contract for:

- in-app navigation
- Universal Links / App Links
- push notifications
- QR scans
- post-auth redirects and deferred replay
- restore and persistence
- deferred replay
- diagnostics and analytics
- automated tests

The route contract is not just a deep-link format. It is the canonical expression of destination and intent.

That has practical benefits:

- Navigation can be shareable, restorable, testable, and analytics-friendly.
- Cold-start and warm-start navigations can use the same ingress model.
- The app can keep durable route identity separate from route-owned metadata and runtime transport context.
- Link provenance and trust can be evaluated before UI code runs.
- Auth redirection, compatibility rewrites, and deferred navigation can be centralized instead of scattered across pages and platform hooks.
- The app planner becomes the one place where route meaning meets current application state.

## Why Centralize Planning And Policy

The navigation planner is where semantic routes meet real app state.

That is the place to answer questions such as:

- Is the user authenticated?
- Is a requested game, workspace, tenant, or store currently selected?
- Should this route focus an existing branch, replace a stack, push onto a stack, or open a modal?
- Should the app land directly on the requested destination, or route through onboarding, login, or a compatibility flow first?

Request policy and planning are where cross-cutting navigation concerns belong. They are easier to reason about there than in page code because they run before presentation and apply consistently to every ingress path.

Example:

1. A signed-out user taps an invite link from email on a cold start.
2. The link enters as a `RouterNavigationRequest` with URI, source, and provenance.
3. A request policy validates origin and rewrites any legacy URL shape.
4. An access policy sees that auth is required, redirects to login, and stores the original request for deferred replay.
5. After sign-in, app code calls `ReplayAsync()` at the auth boundary.
6. The planner maps the semantic route into the correct tab, stack, modal, and active presentation structure.
7. The MAUI presenter materializes native pages and preserves platform back behavior.

That is a navigation flow, not just a deep-link flow. MauiRouter is designed to treat it as one system.

## Where It Fits In MAUI

AdamE.MauiRouter is not a full app framework. It does not replace your DI container, view models, domain state, or page implementations.

It does take a strong position on the navigation contract.

In practice that means MauiRouter usually should replace, not sit alongside, other opinionated MAUI navigation stacks such as Shell, Prism, or similar frameworks. If another framework already owns route semantics, deep-link ingress, redirects, and stack/tab orchestration, MauiRouter is probably the wrong fit.

## Influence From `go_router`

The closest mainstream influence is Flutter's [`go_router`](https://pub.dev/packages/go_router).

`go_router` became popular because it made routes/URLs the primary contract for in-app navigation, deep links, redirects, and nested navigation. It showed that app navigation does not need one mental model for "normal navigation" and a second mental model for "deep links."

MauiRouter borrows that route-first mindset and the idea that boundary concerns such as redirects, provenance, and policy belong near navigation ingress, not buried inside page code.

It does not copy `go_router` directly. MAUI still has native container and platform-lifecycle concerns that Flutter solves differently. MauiRouter therefore separates:

- semantic route identity with `AppRoute`
- app-authored navigation with `AppRouteRequest`
- runtime ingress and boundary transport with `RouterNavigationRequest`
- app-owned planning with `IAppNavigationPlanner`
- native MAUI presentation and bidirectional reconciliation

That extra split is deliberate. It lets the app stay URL-native without pretending that native stacks, tabs, modals, app-link lifecycles, and swipe-back reconciliation do not exist.

## Design Rules

- Routes are semantic, not page names.
- Hosts are structural, not routes.
- Navigation structure is hierarchical and branch-aware, not a single global stack.
- Query parameters may modify a route but must not define primary navigation structure.
- Navigation provenance, normalization, authorization, and deferred replay belong at ingress/policy boundaries, not in page constructors.
- Provenance is runtime request context, not route identity or route metadata.
- App planning is where route meaning meets current application state.
- MAUI pages are adapter artifacts, never core state.
- Presenters should preserve native containers and prefer incremental native mutations over wholesale container replacement whenever possible.
- Presenters are bidirectional: native stack pops, modal dismissals, tab changes, flyout selection, and other native user-driven events may reconcile back into logical state and history.

For a MAUI integration walkthrough, see [docs/maui-integration.md](docs/maui-integration.md). For provenance field ownership, see [docs/provenance.md](docs/provenance.md).

## Status

This repository currently contains the v1 source implementation and a sample app. The intended package IDs are:

- `AdamE.MauiRouter`
- `AdamE.MauiRouter.Maui`
- `AdamE.MauiRouter.Testing`

Until packages are published, consume the library with project references.

```xml
<ItemGroup>
  <ProjectReference Include="..\src\AdamE.MauiRouter\AdamE.MauiRouter.csproj" />
  <ProjectReference Include="..\src\AdamE.MauiRouter.Maui\AdamE.MauiRouter.Maui.csproj" />
</ItemGroup>
```

## Support Matrix

| Area | v1 Support |
| --- | --- |
| Core target frameworks | `net9.0`, `net10.0` |
| MAUI adapter targets | Android, iOS, Mac Catalyst |
| MAUI containers | `NavigationPage`, `TabbedPage`, `FlyoutPage`, modal navigation |
| Page creation | DI-backed page factories |
| URL matching | Fluent runtime route table |
| Route formatting | Fluent runtime formatters |
| Navigation runtime | Serialized operations, bounded logical history, native reconciliation |
| Platform links | Android intents, iOS/Mac Catalyst URL and user-activity lifecycle hooks |
| Android predictive back | Not implemented in v1; state/planning model is intended to support it later |
| Shell | Not supported; usually mutually exclusive |
| Prism | Not supported; usually mutually exclusive |
| Other opinionated MAUI navigation stacks | Generally not intended to be combined |
| Windows MAUI adapter | Not targeted in v1 |
| `netstandard` | Not targeted in v1 |
| Source generators | Not included in v1 |
| Attribute routing | Not included in v1 |
| Navigation transitions | Typed stock, fade, slide, and shared-element descriptors |
| Full multi-window orchestration | State model seam only in v1 |

## Projects

```text
src/AdamE.MauiRouter
  Core abstractions: routes, matching, formatting, requests, policies,
  state, plans, history, diagnostics, back planning, presenter contracts.

src/AdamE.MauiRouter.Maui
  MAUI adapter: DI page creation, host-aware presenter, native container
  materialization, app-link lifecycle hooks.

src/AdamE.MauiRouter.Testing
  Framework-neutral route, planner, navigator, diagnostics, state
  assertion, and snapshot serializer helpers for consuming app tests.

samples/Commerce.Sample
  A no-Shell MAUI sample using store/catalog/product/cart/order routes.

tests/AdamE.MauiRouter.Tests
  Unit and contract tests for route matching, planning, back behavior,
  diagnostics, platform targeting, and sample constraints.

tests/AdamE.MauiRouter.Maui.Tests
  Platform-targeted MAUI adapter tests for presenter lifecycle,
  reconciliation, persistence storage, and transitions.
```

## Mental Model

Navigation is split into three explicit phases.

### 1. Route Matching

Route matching turns a URI and route template into a typed `AppRoute`.

```text
/stores/{storeId}/products/{productId:int}
```

becomes:

```csharp
public sealed record ProductDetailRoute(
    string StoreId,
    int ProductId,
    string? Variant = null,
    string? Promo = null) : AppRoute;
```

Route matching does not decide tabs, stacks, flyouts, modals, pages, or windows.

### 2. App Planning

App planning turns a typed route into a `NavigationPlan`.

This is app-owned because the app knows how route meaning should map to structure:

```text
ProductDetailRoute
  -> WindowNode("main")
  -> TabsNode("store-tabs")
  -> selected tab: catalog
  -> StackNode("catalog-stack")
  -> StoreCatalogRoute
  -> ProductDetailRoute
```

### 3. Presentation

Presentation applies the `NavigationPlan` to real UI.

The MAUI presenter maps structural nodes to native MAUI containers:

| Navigation node | MAUI surface |
| --- | --- |
| `StackNode` | `NavigationPage` |
| `TabsNode` | `TabbedPage` |
| `FlyoutNode` | `FlyoutPage` |
| `ModalNode` | Modal presentation |

Presenters are also bidirectional. Native user events can reconcile back into logical navigation state.

## Quick Start

This section builds a minimal MAUI integration similar to the commerce sample. It keeps the first setup small: typed routes, route matching, app planning, MAUI page mapping, in-app `AppRouteRequest` navigation, runtime-boundary `RouterNavigationRequest`, and startup.

### 1. Define Typed Routes

Routes are normal C# types. They describe semantic destinations, not pages.

```csharp
using AdamE.MauiRouter;

public sealed record StoreHomeRoute(string StoreId) : AppRoute;

public sealed record StoreCatalogRoute(string StoreId) : AppRoute;

public sealed record ProductDetailRoute(
    string StoreId,
    int ProductId,
    string? Variant = null,
    string? Promo = null) : AppRoute;

public sealed record CartRoute(string StoreId) : AppRoute;

public sealed record OrdersRoute(string StoreId) : AppRoute;
```

### 2. Build A Route Table

Use `RouteTable.Create` and map each route template to a typed route.
The product route also maps the `CommerceRouteMetadata.Campaign` key defined in the next step so campaign data can round-trip through `AppRouteRequest` formatting and matching without becoming a constructor parameter.

```csharp
using AdamE.MauiRouter.Routing;

public static class SampleRouteTable
{
    public static RouteTable Create()
    {
        return RouteTable.Create(routes => routes
            .Map(
                "/stores/{storeId}",
                match => new StoreHomeRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))
            .Map(
                "/stores/{storeId}/catalog",
                match => new StoreCatalogRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))
            .Map(
                "/stores/{storeId}/products/{productId:int}",
                match =>
                {
                    match.QueryMetadata(CommerceRouteMetadata.Campaign);
                    return new ProductDetailRoute(
                        match.Path("storeId"),
                        match.Path<int>("productId"),
                        match.Query("variant"),
                        match.Query("promo"));
                },
                format => format
                    .PathParam("storeId", route => route.StoreId)
                    .PathParam("productId", route => route.ProductId)
                    .QueryParam("variant", route => route.Variant)
                    .QueryParam("promo", route => route.Promo)
                    .QueryMetadata(CommerceRouteMetadata.Campaign))
            .Map(
                "/stores/{storeId}/cart",
                match => new CartRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId))
            .Map(
                "/stores/{storeId}/orders",
                match => new OrdersRoute(match.Path("storeId")),
                format => format.PathParam("storeId", route => route.StoreId)));
    }
}
```

Route table responsibilities:

- Match URLs to typed routes.
- Convert path params and query params.
- Format typed routes back into canonical URL paths.
- Validate templates and reject ambiguous overlaps at build time.
- Stay independent from MAUI pages and native containers.

At this point the app has a URL contract, but no tabs, stacks, pages, auth rules, or platform behavior. Those are deliberately separate.

### 3. Define Route Metadata

Route-owned metadata is app-defined state attached to an `AppRouteRequest`. Use it for values that should participate in route formatting, persistence, or downstream app behavior without becoming constructor parameters on the route type.

```csharp
using AdamE.MauiRouter;
using AdamE.MauiRouter.Routing;

public static class CommerceRouteMetadata
{
    public static readonly RouteMetadataKey<string> Campaign = new("campaign");
    public static readonly RouteMetadataKey<string> ReturnTo = new("returnTo");
    public static readonly RouteMetadataKey<string> TraceId = new("traceId");

    public static readonly RouteStateRegistry RouteStateRegistry =
        RouteStateRegistry.Create(state => state
            .Canonical(Campaign)
            .Restorable(ReturnTo)
            .Ephemeral(TraceId));
}
```

Canonical metadata can affect the durable URL, restorable metadata can survive navigation persistence, and ephemeral metadata stays runtime-only.

### 4. Plan Navigation State

Implement `IAppNavigationPlanner` to turn a typed route into logical navigation state. The planner receives the matched route, the runtime request, current navigation state, and operation id, so app-specific structure decisions stay centralized.

The commerce quick start uses one central planner because several route types share the same tab structure:

```csharp
using AdamE.MauiRouter;
using AdamE.MauiRouter.Plans;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.State;

public sealed class CommerceNavigationPlanner : IAppNavigationPlanner
{
    public ValueTask<NavigationPlan> CreatePlanAsync(
        NavigationPlanningContext context,
        CancellationToken cancellationToken = default)
    {
        var storeId = GetStoreId(context.Route);

        var root = new TabsNode(
            "store-tabs",
            new[]
            {
                new NavigationBranch(
                    "home",
                    "Home",
                    new StackNode("home-stack", new[]
                    {
                        Entry("home", new StoreHomeRoute(storeId))
                    })),
                new NavigationBranch(
                    "catalog",
                    "Catalog",
                    new StackNode("catalog-stack", CreateCatalogEntries(context.Route, storeId))),
                new NavigationBranch(
                    "cart",
                    "Cart",
                    new StackNode("cart-stack", new[]
                    {
                        Entry("cart", new CartRoute(storeId))
                    })),
                new NavigationBranch(
                    "orders",
                    "Orders",
                    new StackNode("orders-stack", new[]
                    {
                        Entry("orders", new OrdersRoute(storeId))
                    }))
            },
            SelectedTabId: GetSelectedTab(context.Route),
            DefaultTabId: "home");

        var state = new NavigationState(
            new[]
            {
                new WindowNode("main", root)
            },
            ActiveWindowId: "main");

        return ValueTask.FromResult(new NavigationPlan(
            state,
            NavigationPlanKind.Navigate,
            "Route planned by the app."));
    }

    private static IReadOnlyList<RouteEntry> CreateCatalogEntries(AppRoute route, string storeId)
    {
        var entries = new List<RouteEntry>
        {
            Entry("catalog", new StoreCatalogRoute(storeId))
        };

        if (route is ProductDetailRoute detail)
        {
            entries.Add(Entry($"product-{detail.ProductId}", detail));
        }

        return entries;
    }

    private static RouteEntry Entry(string id, AppRoute route)
    {
        return new RouteEntry(id, route);
    }

    private static string GetStoreId(AppRoute route)
    {
        return route switch
        {
            StoreHomeRoute home => home.StoreId,
            StoreCatalogRoute catalog => catalog.StoreId,
            ProductDetailRoute detail => detail.StoreId,
            CartRoute cart => cart.StoreId,
            OrdersRoute orders => orders.StoreId,
            _ => throw new NotSupportedException($"Route '{route.GetType().Name}' is not supported.")
        };
    }

    private static string GetSelectedTab(AppRoute route)
    {
        return route switch
        {
            StoreCatalogRoute or ProductDetailRoute => "catalog",
            CartRoute => "cart",
            OrdersRoute => "orders",
            _ => "home"
        };
    }
}
```

The planner is where app-specific decisions belong:

- Which tab is selected.
- Which stack contains a detail route.
- Which branches exist.
- Whether a route opens a modal.
- Which window receives a request.
- Whether a request should replace or extend existing state.

The planner owns structure. It does not create MAUI pages; presentation remains an adapter concern.

### 5. Register MAUI Services

Register the route table, planner, diagnostics, persistence, page mappings, app-link lifecycle hooks, startup, and navigator.

```csharp
using AdamE.MauiRouter.Diagnostics;
using AdamE.MauiRouter.Maui.AppLinks;
using AdamE.MauiRouter.Maui.DependencyInjection;
using AdamE.MauiRouter.Policies;
using AdamE.MauiRouter.Requests;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseMauiRouterAppLinks();

        builder.Services.AddSingleton<NavigationDiagnostics>();
        builder.Services.AddMauiRouterFileNavigationPersistence(options =>
        {
            options.BaseUri = new Uri("https://example.com/");
            options.RouteStateRegistry = CommerceRouteMetadata.RouteStateRegistry;
        });
        builder.Services.AddSingleton<INavigationRequestPolicy>(_ =>
            new AllowedUriOriginPolicy(new[] { new Uri("https://example.com") }));
        builder.Services.AddMauiRouter<CommerceNavigationPlanner>(
            SampleRouteTable.Create(),
            pages => pages
                .MapPage<StoreHomeRoute, StoreHomePage>()
                .MapPage<StoreCatalogRoute, StoreCatalogPage>()
                .MapPage<ProductDetailRoute, ProductDetailPage>()
                .MapPage<CartRoute, CartPage>()
                .MapPage<OrdersRoute, OrdersPage>());
        builder.Services.AddMauiRouterStartup(options =>
        {
            options.FallbackRequestFactory = (_, _) =>
                ValueTask.FromResult<RouterNavigationRequest?>(
                    RouterNavigationRequest.FromUri(
                        new Uri("https://example.com/stores/northwind/products/123?variant=blue&promo=spring&campaign=spring-launch"),
                        NavigationRequestSource.Restore));
        });

        return builder.Build();
    }
}
```

`AllowedUriOriginPolicy` is optional, but recommended for external sources such as app links, push links, and QR links. Register it as an `INavigationRequestPolicy` so `AddMauiRouter` discovers it automatically.
`AddMauiRouterStartup` is optional, but recommended for MAUI apps that want the standard cold-start sequence: app links first, then snapshot restore, then fallback navigation, then window attachment.
If route-owned metadata participates in URL formatting or persistence, keep those keys app-owned in a `RouteStateRegistry` and register that registry with persistence services.

### 6. Create Pages With Route Constructor Parameters

The MAUI adapter creates pages through DI. A page mapped with `MapPage<TRoute, TPage>()` can accept the route in its constructor.

```csharp
using AdamE.MauiRouter.Navigation;
using AdamE.MauiRouter.Requests;

public sealed class ProductDetailPage : ContentPage
{
    private readonly ProductDetailRoute _route;
    private readonly IRouterNavigator _navigator;

    public ProductDetailPage(ProductDetailRoute route, IRouterNavigator navigator)
    {
        _route = route;
        _navigator = navigator;

        Title = $"Product {_route.ProductId}";

        var button = new Button { Text = "Back to catalog" };
        button.Clicked += async (_, _) =>
        {
            await _navigator.NavigateAsync(
                AppRouteRequest.For(new StoreCatalogRoute(_route.StoreId)),
                NavigationRequestSource.InAppCommand);
        };

        Content = new VerticalStackLayout
        {
            Padding = 24,
            Children =
            {
                new Label { Text = $"Store {_route.StoreId}" },
                new Label { Text = $"Variant: {_route.Variant ?? "default"}" },
                new Label { Text = $"Promo: {_route.Promo ?? "none"}" },
                button
            }
        };
    }
}
```

Page constructors receive the typed `AppRoute`, not route-entry metadata. If a page needs route-entry metadata, use `IMauiRoutePageLifecycleHook` or app-specific mapping logic instead of expecting an `AppRouteRequest` constructor parameter.

### 7. Navigate

Most app-authored navigation should use `AppRouteRequest`:

```csharp
await navigator.NavigateAsync(
    AppRouteRequest
        .For(new ProductDetailRoute("northwind", 123, "blue", "spring"))
        .WithMetadata(CommerceRouteMetadata.Campaign, "spring-sale"),
    NavigationRequestSource.InAppCommand,
    RouterNavigationDisposition.Auto);
```

Navigate by URL at a runtime boundary:

```csharp
await navigator.NavigateAsync(
    RouterNavigationRequest.FromUri(
        new Uri("https://example.com/stores/northwind/products/123?variant=blue&promo=spring"),
        NavigationRequestSource.AppLink));
```

Navigate by typed route when no route-owned metadata is involved:

```csharp
await navigator.NavigateAsync(
    new ProductDetailRoute("northwind", 123, "blue", "spring"),
    NavigationRequestSource.InAppCommand);
```

Navigate with a full runtime request only when you need transport concerns such as boundary ingress, source, window targeting, or policy handoff:

```csharp
var request = new RouterNavigationRequest(
    uri: new Uri("https://example.com/stores/northwind/cart"),
    route: null,
    source: NavigationRequestSource.Restore,
    windowId: "main",
    metadata: new Dictionary<string, object?>
    {
        ["restoreReason"] = "startup"
    });

await navigator.NavigateAsync(request);
```

### 8. Start The MAUI Router

The MAUI startup service owns the recommended cold-start sequence. It waits briefly for buffered app links, restores a saved navigation snapshot when configured, falls back to an app-provided request when needed, and attaches the presenter to the `Window`.

```csharp
public partial class App : Application
{
    private readonly IMauiRouterStartupService _startup;

    public App(IMauiRouterStartupService startup)
    {
        _startup = startup;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new ContentPage());

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            await _startup.StartAsync(window);
        });

        return window;
    }
}
```

`IMauiPresentationState` remains available for diagnostics and modal/window hosting concerns, but `IMauiRouterStartupService` is the recommended app integration point.

## Putting The Architecture To Work

The quick start wires the minimal path. The same pieces also handle a more realistic navigation flow: a signed-out user opens a product link on cold start, the app validates the link, normalizes any legacy URL shape, redirects to login, defers the original request, and replays it after sign-in.

### Boundary Ingress Carries Runtime Context

Built-in MAUI app-link hooks create `RouterNavigationRequest` values internally from Android intents and iOS/Mac Catalyst URL or user-activity callbacks. App-owned external sources such as Branch, push, and QR bridges should dispatch equivalent runtime requests through `IMauiExternalNavigationDispatcher`.

MauiRouter does not ship Branch, push notification, QR scanner, or auth-provider SDK integrations. Provider-specific bridges stay in app code; MauiRouter owns the common routing contract, buffering, dedupe, policy, planning, and presentation pipeline.

```csharp
using AdamE.MauiRouter.Maui.AppLinks;
using AdamE.MauiRouter.Requests;

public sealed class BranchLinkBridge(IMauiExternalNavigationDispatcher dispatcher)
{
    public void Open(Uri uri, string correlationId, bool isColdStart)
    {
        dispatcher.Dispatch(RouterNavigationRequest.FromUri(
            uri,
            NavigationRequestSource.AppLink,
            provenance: new NavigationRequestProvenance(
                provider: "branch",
                originalUri: uri,
                correlationId: correlationId,
                isColdStart: isColdStart)));
    }
}
```

That request carries transport context: source, provenance, timestamp, optional window targeting, and request metadata. It is intentionally separate from the durable `AppRoute`.

Auth callbacks are different. A raw auth callback should usually terminate in the app's auth subsystem, not become a router source. After sign-in, app code can replay a deferred request or dispatch an app-authored post-auth navigation request when the auth flow resolves to an actual navigation intent.

### Route Metadata Stays App-Owned

In-app navigation still uses `AppRouteRequest` when the app wants route-owned metadata:

```csharp
var request = AppRouteRequest
    .For(new ProductDetailRoute("northwind", 123, "blue", "spring"))
    .WithMetadata(CommerceRouteMetadata.Campaign, "spring-launch")
    .WithMetadata(CommerceRouteMetadata.ReturnTo, "/stores/northwind/catalog")
    .WithMetadata(CommerceRouteMetadata.TraceId, Activity.Current?.TraceId.ToString());
```

The `RouteStateRegistry` decides which metadata is canonical, restorable, or ephemeral. That keeps campaign, return-path, and trace-style data out of page constructors while still making it available to route formatting, persistence, policies, and planning when the app chooses.

### Policies Centralize Trust, Compatibility, And Auth

Register request policies as services. `AddMauiRouter` discovers them and applies them before planning and presentation.

```csharp
builder.Services.AddMauiRouterFileDeferredNavigationRequests();
builder.Services.AddSingleton<INavigationRequestPolicy>(_ =>
    new AllowedUriOriginPolicy(new[] { new Uri("https://example.com") }));
builder.Services.AddSingleton<INavigationRequestPolicy, LegacyProductUrlPolicy>();
builder.Services.AddSingleton<INavigationAccessEvaluator, CommerceAccessEvaluator>();
builder.Services.AddSingleton<INavigationRequestPolicy, AccessGateNavigationPolicy>();
```

`AddMauiRouterFileDeferredNavigationRequests()` registers both `IDeferredNavigationRequestStore` and `IDeferredNavigationRequestReplayer`. The app still chooses when to call replay.

A compatibility policy can normalize old URLs before the planner sees them:

```csharp
public sealed class LegacyProductUrlPolicy : INavigationRequestPolicy
{
    public ValueTask<RouterNavigationRequest> ApplyAsync(
        NavigationRequestPolicyContext context,
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Uri?.AbsolutePath is not { } path ||
            !path.StartsWith("/p/", StringComparison.Ordinal))
        {
            return ValueTask.FromResult(request);
        }

        var productId = path["/p/".Length..];
        var normalized = new Uri($"https://example.com/stores/northwind/products/{productId}");

        return ValueTask.FromResult(request with
        {
            Uri = normalized,
            Route = null
        });
    }
}
```

An access evaluator can defer the original protected request and redirect to login:

```csharp
public sealed class CommerceAccessEvaluator(IAuthState auth) : INavigationAccessEvaluator
{
    public ValueTask<NavigationAccessDecision> EvaluateAsync(
        NavigationRequestPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        if (auth.IsSignedIn || context.Route is LoginRoute)
        {
            return ValueTask.FromResult(NavigationAccessDecision.Allow());
        }

        var login = RouterNavigationRequest.FromRoute(
            new LoginRoute(),
            context.Request.Source,
            context.Request.WindowId,
            provenance: context.Request.Provenance);

        return ValueTask.FromResult(NavigationAccessDecision.DeferAndRedirect(login));
    }
}
```

Deferred replay is provided by the library, but the app chooses when to invoke it. A common place is the auth boundary after sign-in completes:

```csharp
public sealed class SignInCompletionHandler(
    IAuthState auth,
    IDeferredNavigationRequestReplayer replayer)
{
    public async Task CompleteSignInAsync(CancellationToken cancellationToken = default)
    {
        await auth.CompleteSignInAsync(cancellationToken);

        var result = await replayer.ReplayAsync(cancellationToken);
        if (result.FailedCount > 0)
        {
            // Failed requests remain queued for a later replay attempt.
            // Log or surface an app-owned fallback.
        }
    }
}
```

### The Planner Sees Route Meaning And App State

After matching and request policy, the planner receives the typed route plus request context and current navigation state. That is where route meaning becomes tabs, stacks, modals, and windows.

```csharp
public ValueTask<NavigationPlan> CreatePlanAsync(
    NavigationPlanningContext context,
    CancellationToken cancellationToken = default)
{
    var windowId =
        context.Request.WindowId ??
        context.CurrentState.ActiveWindowId ??
        "main";

    var coldStart = context.Request.Provenance?.IsColdStart == true;
    var cameFromPush = context.Request.Source == NavigationRequestSource.Push;
    var campaign = context.Request.Metadata.TryGetValue(
        CommerceRouteMetadata.Campaign.Name,
        out var rawCampaign)
            ? rawCampaign as string
            : null;

    var root = BuildStoreTabs(context.Route, campaign);
    var existingWindow = context.CurrentState.FindWindow(windowId);
    var modals = cameFromPush && context.Route is ProductDetailRoute detail
        ? new[] { new ModalNode("product-alert", Entry("product-alert", detail)) }
        : existingWindow?.Modals ?? Array.Empty<ModalNode>();

    var state = context.CurrentState.ReplaceWindow(new WindowNode(windowId, root, modals));
    var reason = coldStart
        ? "Cold-start route planned by the app."
        : "Warm navigation planned by the app.";

    return ValueTask.FromResult(new NavigationPlan(
        state,
        NavigationPlanKind.Navigate,
        reason));
}
```

This is the core split in practice: URLs and boundary requests describe intent, policies decide whether that intent is acceptable right now, the planner maps accepted intent into logical app state, and the MAUI presenter turns that state into native UI.

## MAUI Integration

One common MauiRouter integration looks like this:

1. Define durable semantic destinations as `AppRoute`.
2. Use `AppRoute`, `AppRouteRequest`, `Uri`, or `RouterNavigationRequest` based on how much route metadata or runtime transport context the caller needs.
3. Keep route-owned metadata app-defined in a `RouteStateRegistry` when formatting, persistence, or downstream app behavior depend on it.
4. Register `AddMauiRouter(...)`, optional persistence or deferred-request services, and `AddMauiRouterStartup(...)` as needed by the app.
5. Use `IMauiExternalNavigationDispatcher`, `IMauiRouterStartupService`, or direct `IRouterNavigator` calls where external lifecycle or transport-aware boundaries need explicit control.

For one complete MAUI integration walkthrough, see [docs/maui-integration.md](docs/maui-integration.md).

## Core API Guide

### `AppRoute`

`AppRoute` is the base type for semantic destinations.

```csharp
public abstract record AppRoute;
```

Routes should be durable and meaningful. Prefer domain language:

```csharp
ProductDetailRoute("northwind", 123)
```

over implementation language:

```csharp
ProductPageRoute("ProductPage")
```

### `AppRouteRequest`

`AppRouteRequest` represents `AppRoute + route-owned metadata`.

Important properties:

- `Route`: the typed route being requested.
- `Metadata`: route-owned metadata that participates in formatting or downstream app behavior.

Common helpers:

```csharp
var routeRequest = AppRouteRequest
    .For(route)
    .WithMetadata(new RouteMetadataKey<string>("campaign"), "spring-sale");
```

Use `AppRouteRequest` for route factories, metadata-bearing in-app navigation, URL formatting, and compatibility normalization. If runtime transport concerns such as URI ingress, source, window targeting, or policy/persistence handoff matter, convert it to `RouterNavigationRequest` at the call site or boundary:

```csharp
var runtimeRequest = RouterNavigationRequest.FromRouteRequest(
    routeRequest,
    NavigationRequestSource.InAppCommand);
```

### `RouterNavigationRequest`

`RouterNavigationRequest` carries runtime transport context around a navigation intent. It is useful when the caller needs explicit URI ingress, source, window targeting, request metadata, provenance, or disposition.

Important properties:

- `Uri`: incoming URL, if the request starts from a URL.
- `Route`: typed route, if already known.
- `Source`: app link, push, QR, in-app command, restore, test, or native reconciliation.
- `WindowId`: optional target window.
- `Metadata`: app-owned request metadata.
- `Provenance`: runtime request context such as provider, original URI, referrer URI, correlation id, cold-start flag when known, and string attributes.
- `Timestamp`: request creation time.

Factory helpers:

```csharp
RouterNavigationRequest.FromUri(uri, NavigationRequestSource.AppLink);
RouterNavigationRequest.FromRoute(route, NavigationRequestSource.InAppCommand);
RouterNavigationRequest.FromRouteRequest(routeRequest, NavigationRequestSource.InAppCommand);
```

Common uses include app-link ingress, app-owned external bridges, startup fallback, restore, request-policy pipelines, deferred replay, tests, and any navigation flow that needs explicit transport metadata.

`NavigationRequestProvenance` is not part of `AppRoute`, `AppRouteRequest`, URL formatting, or `RouteStateRegistry`. Built-in MAUI app-link ingress sets provenance automatically. App-owned external sources such as Branch, push, QR, and provider SDK bridges should set it explicitly before dispatch. MauiRouter sets transport provenance it owns; apps set provider/business provenance they own. See [docs/provenance.md](docs/provenance.md) for the field ownership table.

```csharp
var branchUri = new Uri("https://example.com/stores/northwind");
var request = RouterNavigationRequest.FromUri(
    branchUri,
    NavigationRequestSource.AppLink,
    provenance: new NavigationRequestProvenance(
        provider: "branch",
        originalUri: branchUri,
        referrerUri: referrerUri,
        correlationId: branchCorrelationId));

externalNavigationDispatcher.Dispatch(request);
```

### `NavigationRequestSource`

Built-in request sources:

```csharp
Unknown
AppLink
Push
QrCode
InAppCommand
Restore
Test
NativeReconciliation
```

Use the source to drive policies. For example, app-link requests might require authentication, push requests might carry notification metadata, and restore requests might skip animation.

### `RouteTable`

`RouteTable` owns route matching and formatting.

```csharp
var result = routeTable.Match(uri);

if (result.IsSuccess)
{
    AppRoute route = result.Route!;
}
```

Formatting:

```csharp
var path = routeTable.Format(
    new ProductDetailRoute("northwind", 123, "blue", "spring"));

// /stores/northwind/products/123?variant=blue&promo=spring
```

Formatting from an app-facing route request:

```csharp
var routeRequest = AppRouteRequest.For(
    new ProductDetailRoute("northwind", 123, "blue", "spring"));

var path = routeTable.Format(routeRequest);
var uri = routeTable.FormatUri(routeRequest, new Uri("https://example.com"));
```

These overloads are most useful when a route definition also formats metadata into the URI.

Absolute URI formatting:

```csharp
var uri = routeTable.FormatUri(
    new ProductDetailRoute("northwind", 123),
    new Uri("https://example.com"));
```

### Route Template Syntax

Route templates are path-driven and support pragmatic inline syntax:

```text
/stores/{storeId}
/stores/{storeId:alpha}/products/{productId:int}
/stores/{storeId}/catalog/{category?}
/products/{productId:int?}
/docs/{*path}
```

Built-in constraints:

| Constraint | Example | Notes |
| --- | --- | --- |
| `int` | `{productId:int}` | 32-bit integer, invariant culture |
| `long` | `{orderId:long}` | 64-bit integer, invariant culture |
| `guid` | `{id:guid}` | GUID formats accepted by `Guid.TryParse` |
| `bool` | `{active:bool}` | Boolean values accepted by `bool.TryParse` |
| `decimal` | `{price:decimal}` | Decimal number, invariant culture |
| `alpha` | `{storeId:alpha}` | Letters only |

Custom constraints are registered on `RouteTableBuilder` and then used with the same inline syntax:

```csharp
public sealed record ProductRoute(string StoreId, string Sku) : AppRoute;

var routes = RouteTable.Create(routes => routes
    .AddConstraint(
        "slug",
        value => value.Length is > 0 and <= 80 &&
                 value.All(ch => char.IsLower(ch) || char.IsDigit(ch) || ch == '-'),
        disjointWith: new[] { "guid", "bool" })
    .Map(
        "/stores/{storeId:slug}/products/{sku}",
        match => new ProductRoute(match.Path("storeId"), match.Path("sku")),
        format => format
            .PathParam("storeId", route => route.StoreId)
            .PathParam("sku", route => route.Sku)));
```

Custom constraints are builder-scoped, case-insensitive, and must be registered before mapping templates that reference them. They cannot redefine built-in constraint names. Matching and formatting use the same predicate, so an invalid formatted path value throws instead of producing an invalid URL.

Optional path parameters must be complete trailing segments. Catch-all parameters must be final. Duplicate parameter names, unknown constraints, exact duplicate templates, and ambiguous overlapping templates are rejected when the route table is built.

Overlapping constrained templates are allowed only when the built-in constraints are known to be disjoint. For example, `/values/{value:int}` and `/values/{value:alpha}` can coexist, while overlapping pairs such as `int`/`long`, `int`/`decimal`, `bool`/`alpha`, and `alpha`/`guid` are rejected.

Custom constraints are treated conservatively during ambiguity validation. Equal-specificity custom constrained templates are assumed to overlap unless either constraint declares the other name in `disjointWith`. This declaration is symmetric for validation purposes.

Route matching uses deterministic precedence:

```text
literal > constrained parameter (including constrained optional parameter) > unconstrained parameter > unconstrained optional parameter > catch-all
```

Exact shorter routes win over broader optional or catch-all routes when both can match the same URL. For example, `/docs` wins over `/docs/{*path}` for `/docs`, and `/stores/{storeId}` wins over `/stores/{storeId}/{section?}` for `/stores/northwind`. Registration order is only a final tie-breaker for non-ambiguous routes.

### `RouteMatchContext`

Use `Path` and `Query` to read matched values.

```csharp
match.Path("storeId");              // string
match.Path<int>("productId");       // typed conversion
match.PathOptional("category");     // nullable string
match.PathOptional<int>("page");    // nullable typed conversion
match.Query("variant");             // last value, nullable string
match.Query<int?>("page");          // last value, nullable typed conversion
match.QueryAll("tag");              // all repeated values
match.QueryAll<Guid>("ids");        // typed repeated values
```

`Query(name)` returns the last value for compatibility with single-value query usage. Use `QueryAll(name)` when repeated query keys are meaningful.

Path values are required unless the template marks the segment optional. Query values are always optional.

Typed conversion uses invariant culture and type converters where available.

### `RouteFormatBuilder`

Use `PathParam` for path segments and `QueryParam` for modifiers.

```csharp
format => format
    .PathParam("storeId", route => route.StoreId)
    .PathParam("productId", route => route.ProductId)
    .QueryParam("variant", route => route.Variant)
    .QueryParam("promo", route => route.Promo)
```

By default, `QueryParam` omits null values. To emit empty values, pass `omitWhenNull: false`.

If the formatter returns an enumerable value, `QueryParam` formats repeated keys. Strings are treated as scalar values.

### Convention Routes

`MapRoute<TRoute>` binds path parameters by member name and can bind query values with `Query(...)`. Because query values are always optional, any constructor parameter reached through a convention query binding must be nullable or provide a default value.

```csharp
public sealed record SearchRoute(string StoreId, IReadOnlyList<string> Tags) : AppRoute;

routes.Map(
    "/stores/{storeId}/search",
    match => new SearchRoute(
        match.Path("storeId"),
        match.QueryAll("tag")),
    format => format
        .PathParam("storeId", route => route.StoreId)
        .QueryParam("tag", route => route.Tags));

// /stores/northwind/search?tag=blue&tag=green
```

Optional path parameters and catch-all parameters are omitted when their formatter returns `null` or an empty string.

### `NavigationState`

`NavigationState` is the canonical logical navigation state.

```csharp
public sealed record NavigationState(
    IReadOnlyList<WindowNode> Windows,
    string? ActiveWindowId = null);
```

Useful members:

- `NavigationState.Empty`
- `ActiveWindow`
- `FindWindow(id)`
- `ReplaceWindow(window)`

The state model can represent multiple windows, even though complete multi-window orchestration is not part of v1.

### Navigation Nodes

Nodes describe structure, not pages.

```csharp
WindowNode
FlyoutNode
TabsNode
StackNode
ModalNode
```

#### `WindowNode`

Represents one logical app window.

```csharp
new WindowNode(
    "main",
    root: tabsNode,
    modals: modalNodes);
```

#### `TabsNode`

Represents branch-aware tab navigation.

```csharp
new TabsNode(
    "store-tabs",
    branches,
    SelectedTabId: "catalog",
    DefaultTabId: "home");
```

Each branch contains another `NavigationNode`, usually a `StackNode`.

#### `StackNode`

Represents a native navigation stack.

```csharp
new StackNode("catalog-stack", new[]
{
    new RouteEntry("catalog", new StoreCatalogRoute("northwind")),
    new RouteEntry("product-123", new ProductDetailRoute("northwind", 123))
});
```

#### `FlyoutNode`

Represents flyout navigation.

```csharp
new FlyoutNode(
    "main-flyout",
    branches,
    SelectedItemId: "store",
    DefaultItemId: "store");
```

#### `ModalNode`

Represents modal presentation.

```csharp
new ModalNode(
    "cart-modal",
    new RouteEntry("cart", new CartRoute("northwind")));
```

### `RouteEntry`

`RouteEntry` places a semantic route into structural state.

```csharp
new RouteEntry(
    Id: "product-123",
    Route: new ProductDetailRoute("northwind", 123),
    Transition: null,
    Metadata: null);
```

The `Id` should be stable within the host. The MAUI presenter uses entry ids to decide whether pages can be reused or whether stack changes are needed.

### `NavigationPlan`

`NavigationPlan` describes the intended state mutation.

```csharp
new NavigationPlan(
    TargetState: state,
    Kind: NavigationPlanKind.Navigate,
    Reason: "Product link opened",
    Transition: null);
```

Plan kinds:

```csharp
Navigate
Replace
Back
Restore
Reconcile
```

### `NavigationTransition`

`NavigationTransition` is built-in presentation metadata. It belongs on a `NavigationPlan` when the whole operation should use a default transition, or on a `RouteEntry` when a specific stack/modal entry should override that default.

```csharp
new RouteEntry(
    Id: "product-123",
    Route: new ProductDetailRoute("northwind", 123),
    Transition: new SharedElementNavigationTransition(
        new[] { SharedElementPair.SameId("product-123-image") },
        Fallback: new FadeNavigationTransition(TimeSpan.FromMilliseconds(180)),
        Duration: TimeSpan.FromMilliseconds(260)));
```

Built-in descriptors include `NoNavigationTransition`, `PlatformDefaultNavigationTransition`, `FadeNavigationTransition`, `SlideNavigationTransition`, and `SharedElementNavigationTransition`. Custom transition subtypes are not supported.

Transition precedence is deterministic: stack push uses the incoming entry transition, stack pop uses the outgoing entry transition, modal push/pop use the modal route entry transition, and each falls back to `NavigationPlan.Transition`. Initial materialization, restore, and bulk structural reconciliation do not animate by default.

### `IRouterNavigator`

`IRouterNavigator` is the public navigation runtime. Resolve it from DI for app, host, or test code.

Pipeline:

```text
NavigateAsync(Uri|AppRoute|AppRouteRequest|RouterNavigationRequest)
  -> route matching
  -> request policies
  -> app planning
  -> plan policies
  -> presentation
  -> state/history update
```

Navigation operations are serialized. In-app navigation, back navigation, restore, and native reconciliation all run through the same internal queue so state, history, and presentation stay ordered.

Public state:

```csharp
navigator.CurrentState
navigator.History
```

Common navigation entry points:

- `NavigateAsync(Uri...)` starts from a URL directly.
- `NavigateAsync(AppRoute...)` starts from a typed semantic route.
- `NavigateAsync(AppRouteRequest...)` starts from a typed route plus route-owned metadata.
- `NavigateAsync(RouterNavigationRequest...)` starts from an explicit runtime request.

When a fallback route is selected, the route continues through request policies, app planning, plan policies, presentation, and history like any other semantic route.

Back navigation:

```csharp
var result = await navigator.BackAsync();

if (!result.Handled)
{
    // Let the platform close the window or exit the app.
}
```

Additional public operations:

- `ReconcileAsync(...)` accepts an explicit `NavigationReconciliation`; this is useful for host-owned or test-driven reconciliation.
- `RestoreAsync(...)` and `RestoreFromStoreAsync(...)` drive snapshot restore directly.

### Policies

Policies let you keep app-specific navigation decisions outside route matching and presentation.

#### Request Policies

`INavigationRequestPolicy` can normalize or redirect a request before planning.

```csharp
public sealed class RequireStorePolicy : INavigationRequestPolicy
{
    public ValueTask<RouterNavigationRequest> ApplyAsync(
        NavigationRequestPolicyContext context,
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Route is null)
        {
            return ValueTask.FromResult(request);
        }

        return ValueTask.FromResult(request);
    }
}
```

Use request policies for:

- Authentication redirects.
- Tenant or store normalization.
- Feature flags.
- Source-specific request behavior.

#### Plan Policies

`INavigationPlanPolicy` can adjust a plan after app planning.

```csharp
public sealed class AddDiagnosticsReasonPolicy : INavigationPlanPolicy
{
    public ValueTask<NavigationPlan> ApplyAsync(
        NavigationPlanPolicyContext context,
        NavigationPlan plan,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(plan with
        {
            Reason = plan.Reason ?? $"Planned {context.Route.GetType().Name}"
        });
    }
}
```

Use plan policies for:

- Modal enforcement.
- Window selection.
- Transition metadata.
- App-wide state adjustments.

### History

The navigator records logical history entries after navigation, back navigation, and native reconciliation.

```csharp
var history = navigator.History;
var current = history.Current;
```

Each `NavigationHistoryEntry` contains:

- `Id`
- `Request`
- `Route`
- `State`
- `Reason`
- `Timestamp`

History is bounded to `128` entries by default.

Native stacks are projections of logical state, but native gestures can become authoritative user intent through reconciliation.

### Navigation Persistence And Restore

Persistence is for navigation state only. Snapshots store semantic route URIs plus logical host structure/history; they never store MAUI pages, DI scopes, handlers, or native stacks.

```csharp
services.AddMauiRouterFileNavigationPersistence(options =>
{
    options.BaseUri = new Uri("https://example.com/");
    options.PersistHistory = true;
    options.PersistModals = false;
});
```

When persistence is configured, successful navigation, back navigation, reconciliation, and restore save a fresh `NavigationSnapshot`. Save failures emit diagnostics but do not roll back completed navigation.

Restore goes through the normal presentation path during MAUI startup:

```csharp
services.AddMauiRouterStartup();
```

Snapshot restore is enabled by default during startup. Optional fallback navigation is configured separately with `FallbackRequestFactory` and runs only after app-link priority and snapshot restore have both been considered. See [Startup And Window Attachment](#startup-and-window-attachment) below for fallback customization.

Restore rules:

- Current navigation state is strict: an invalid restored route rejects the snapshot without mutating state/history.
- History is tolerant: invalid history entries are dropped and `CurrentIndex` is clamped.
- Fallback routing is not used for persisted snapshots.
- Restore uses `NavigationPlanKind.Restore`, runs plan policies, applies the presenter, then installs restored history without pushing an extra entry.
- Route-entry metadata is omitted unless `NavigationPersistenceOptions.MetadataSerializer` is set.

MAUI apps can use the built-in file store:

```csharp
builder.Services.AddMauiRouterFileNavigationPersistence(options =>
{
    options.BaseUri = new Uri("https://example.com/");
});
```

For cold start, prefer app links over snapshots. Register `AddMauiRouterStartup` and call `IMauiRouterStartupService.StartAsync(window)` from `CreateWindow`; the service checks buffered app links before restoring and skips restore/fallback when an app link is pending.

### Diagnostics

`NavigationDiagnostics` emits events for route matching, policies, planning, presentation, persistence/restore, MAUI startup, reconciliation, back navigation, app-link delivery, and failures.

```csharp
var diagnostics = new NavigationDiagnostics();

diagnostics.EventWritten += (_, e) =>
{
    Debug.WriteLine($"{e.OperationId} {e.Phase} {e.Severity} {e.Kind}: {e.Message}");
};
```

Every event has:

- `OperationId`: stable id for one navigation, back, reconciliation, or app-link operation.
- `Phase`: route matching, request policy, planning, plan policy, presentation, persistence, reconciliation, back, app link, or diagnostics.
- `Severity`: trace, debug, information, warning, error, or critical.
- `Data`: structured values with stable keys.

Common structured data keys include:

```csharp
NavigationDiagnosticDataKeys.DurationMs
NavigationDiagnosticDataKeys.ExceptionType
NavigationDiagnosticDataKeys.ExceptionMessage
NavigationDiagnosticDataKeys.Uri
NavigationDiagnosticDataKeys.Path
NavigationDiagnosticDataKeys.RequestSource
NavigationDiagnosticDataKeys.RouteType
NavigationDiagnosticDataKeys.RouteTemplate
NavigationDiagnosticDataKeys.RouteDiagnosticCode
NavigationDiagnosticDataKeys.RouteDiagnosticMessage
NavigationDiagnosticDataKeys.CandidateCount
NavigationDiagnosticDataKeys.PlanKind
NavigationDiagnosticDataKeys.PolicyType
NavigationDiagnosticDataKeys.RedirectCount
NavigationDiagnosticDataKeys.RedirectFrom
NavigationDiagnosticDataKeys.RedirectTo
NavigationDiagnosticDataKeys.RedirectTrace
NavigationDiagnosticDataKeys.WindowId
NavigationDiagnosticDataKeys.ReconciliationSource
NavigationDiagnosticDataKeys.PageType
NavigationDiagnosticDataKeys.HostId
NavigationDiagnosticDataKeys.BranchId
NavigationDiagnosticDataKeys.RouteEntryId
NavigationDiagnosticDataKeys.ModalId
NavigationDiagnosticDataKeys.HandlerName
NavigationDiagnosticDataKeys.SnapshotVersion
NavigationDiagnosticDataKeys.SnapshotAgeMs
NavigationDiagnosticDataKeys.SnapshotWindowCount
NavigationDiagnosticDataKeys.SnapshotHistoryCount
NavigationDiagnosticDataKeys.SnapshotStoreType
NavigationDiagnosticDataKeys.RestoreReason
NavigationDiagnosticDataKeys.StartupOutcome
NavigationDiagnosticDataKeys.AppLinkGraceMs
```

Observer interface:

```csharp
public sealed class LoggingNavigationObserver : INavigationObserver
{
    public void OnNavigationEvent(NavigationDiagnosticEvent diagnosticEvent)
    {
        Console.WriteLine($"{diagnosticEvent.Timestamp:o} {diagnosticEvent.Kind}");
    }
}

diagnostics.AddObserver(new LoggingNavigationObserver());
```

Observer failures are isolated from navigation. Completion and failure events include diagnostic data such as duration and exception details where applicable.

Diagnostics can mirror directly to `ILogger`:

```csharp
ILogger logger = loggerFactory.CreateLogger("Navigation");
var diagnostics = new NavigationDiagnostics(logger);
```

`AddMauiRouter` registers diagnostics with an `ILoggerFactory` when logging is available. The blessed MAUI runtime creates enabled diagnostics by default; use `NavigationDiagnostics.None` only in tests or custom internal composition.

Diagnostics also mirror events into the current `Activity`. Navigation activities are emitted from `NavigationActivitySources.Default`.

```csharp
using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == NavigationActivitySources.DefaultName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStopped = activity =>
    {
        foreach (var tag in activity.Tags)
        {
            Console.WriteLine($"{tag.Key}: {tag.Value}");
        }
    }
};

ActivitySource.AddActivityListener(listener);
```

Activity tags include operation id, request source, route type, route template, plan kind, reconciliation source, window id, and failure metadata when applicable.

Event kinds include:

```csharp
RouteMatchingStarted
RouteMatched
RouteNotMatched
RouteFallbackSelected
RouteMatchingFailed
RequestPolicyStarted
RequestRedirected
RequestRedirectLoopDetected
RequestPolicyCompleted
RequestPolicyFailed
PlanningStarted
PlanningCompleted
PlanningFailed
PlanPolicyStarted
PlanPolicyCompleted
PlanPolicyFailed
PresentationStarted
PresentationPageCreated
PresentationPageReleased
PresentationHandlerAttached
PresentationHandlerDetached
PresentationPresenterDisposed
PresentationCompleted
PresentationFailed
SnapshotSaveStarted
SnapshotSaved
SnapshotSaveFailed
SnapshotLoadStarted
SnapshotLoaded
SnapshotLoadFailed
RestoreStarted
RestoreCompleted
RestoreRejected
RestoreFailed
StartupStarted
StartupAppLinkPending
StartupRestoreSkipped
StartupFallbackNavigated
StartupCompleted
StartupFailed
ReconciliationStarted
ReconciliationCompleted
ReconciliationFailed
BackStarted
BackEvaluated
BackCompleted
BackUnhandled
BackFailed
NavigationFailed
AppLinkReceived
AppLinkBuffered
AppLinkDispatched
AppLinkFailed
DiagnosticObserverFailed
```

### Back Planning

`DefaultBackNavigator` creates host-aware back plans.

```csharp
var backNavigator = new DefaultBackNavigator(
    new BackNavigationOptions
    {
        ReturnToDefaultTabBeforeLeaving = true,
        ReturnToDefaultFlyoutItemBeforeLeaving = true
    },
    diagnostics);

NavigationPlan? backPlan = backNavigator.CreateBackPlan(navigator.CurrentState);
```

Default behavior:

1. Dismiss the top modal.
2. Delegate into the selected host branch.
3. Pop the selected stack.
4. Return to the default tab if configured.
5. Return to the default flyout item if configured.
6. Return `null` if no host accepts back navigation.

Apps normally call `IRouterNavigator.BackAsync()`, which asks the configured back navigator for a plan, presents it, and records logical history. If no host accepts back navigation, the result is unhandled so the app can delegate to the platform.

## MAUI Adapter Guide

The MAUI adapter is intentionally separate from core. Core does not reference MAUI pages.

### Recommended Registration

Most apps should use `AddMauiRouter<TPlanner>()`, register app-owned `INavigationRequestPolicy` services, and keep page maps on `MauiRoutePageRegistry`.

```csharp
builder.Services.AddSingleton<INavigationRequestPolicy>(_ =>
    new AllowedUriOriginPolicy(new[] { new Uri("https://example.com") }));

builder.Services.AddMauiRouter<CommerceNavigationPlanner>(
    SampleRouteTable.Create(),
    pages => pages
        .MapPage<StoreHomeRoute, StoreHomePage>()
        .MapPage<ProductDetailRoute, ProductDetailPage>());
```

If page mappings live outside the composition root, register them with `AddMauiRouterPages(...)` and keep `AddMauiRouter(...)` in the app host:

```csharp
services.AddMauiRouterPages(pages => pages
    .MapPage<StoreHomeRoute, StoreHomePage>()
    .MapPage<ProductDetailRoute, ProductDetailPage>());
```

### Page Mapping

Use the type-based overload when the page can be created by DI.

```csharp
.MapPage<ProductDetailRoute, ProductDetailPage>()
```

Use the factory overload when you need custom construction.

```csharp
.MapPage<ProductDetailRoute>((services, route) =>
{
    var page = services.GetRequiredService<ProductDetailPage>();
    page.BindingContext = new ProductDetailViewModel(route);
    return page;
});
```

Pages are created in a per-page DI scope by default. When the presenter removes a page from native navigation, it releases that scope.
Page constructors receive the typed `AppRoute`, not route-entry metadata. If a page needs route-entry metadata, prefer `IMauiRoutePageLifecycleHook` or app-specific page wiring.

### Startup And Window Attachment

Most MAUI apps should register the startup service and call it from `CreateWindow`. This is the standard cold-start entry point.

```csharp
using AdamE.MauiRouter.Requests;

builder.Services.AddMauiRouterStartup(options =>
{
    options.FallbackRequestFactory = (_, _) =>
        ValueTask.FromResult<RouterNavigationRequest?>(
            RouterNavigationRequest.FromUri(startupUri, NavigationRequestSource.Restore));
});
```

```csharp
protected override Window CreateWindow(IActivationState? activationState)
{
    var window = new Window(new ContentPage());

    MainThread.BeginInvokeOnMainThread(async () =>
    {
        await startup.StartAsync(window);
    });

    return window;
}
```

Startup waits up to `MauiRouterStartupOptions.AppLinkGracePeriod` for a buffered app link. If one is pending, startup gives it priority. Otherwise it tries snapshot restore, then the optional `FallbackRequestFactory`, then attaches the window even when no navigation occurs. If app-link dispatch fails later, the buffered request stays pending so a later startup-adjacent trigger such as another dispatch or foreground transition can retry it without losing order.

### Native Container Projection

The v1 MAUI presenter projects state into real MAUI containers.

| State node | MAUI projection |
| --- | --- |
| `StackNode` | `NavigationPage` |
| `TabsNode` | `TabbedPage` |
| `FlyoutNode` | `FlyoutPage` |
| `ModalNode` | `PushModalAsync` / `PopModalAsync` |

This is deliberate. Native containers preserve platform behavior such as swipe-back, Android back behavior, tab UX, modal presentation, and native-backed transition surfaces.

### Incremental Updates

The presenter tries to reuse existing host containers when host ids and route entry ids still line up. It preserves tab and flyout branch pages by branch id, diffs navigation stacks by common route-entry prefix, and releases removed page scopes.

For example, if a catalog stack changes from:

```text
catalog
```

to:

```text
catalog -> product-123
```

the presenter can push a product page onto the existing `NavigationPage` instead of replacing the whole tab host.

### Reconciliation

The presenter raises `ReconciliationRequested` when native UI changes should update logical state.

Current committed native reconciliation sources:

```csharp
NativeBackGesture
ModalDismissed
TabChanged
OtherNativeEvent
```

Android predictive back is not implemented in v1. MauiRouter's state/planning model is intended to make predictive back feasible later, but true support requires Android-specific gesture preview, cancellation, and commit handling.

The internal router runtime records reconciliation in logical history; app code continues to interact with `IRouterNavigator`.

### App Links

The adapter includes lifecycle wiring that turns platform links into `RouterNavigationRequest` instances.

Recommended setup:

```csharp
builder
    .UseMauiApp<App>()
    .UseMauiRouterAppLinks();
```

`UseMauiRouterAppLinks` listens for Android intents and iOS/Mac Catalyst URL/user-activity callbacks. Requests are buffered until `IMauiRouterStartupService.StartAsync(window)` marks the router ready. These built-in callbacks attach `NavigationRequestProvenance` automatically with providers from `MauiAppLinkProvenanceProviders`: `android-intent`, `ios-open-url`, `ios-user-activity`, and `maui-app-link`. For app-owned external sources such as Branch, push, or QR bridges, resolve `IMauiExternalNavigationDispatcher`, create a `RouterNavigationRequest` with explicit provenance, and call `Dispatch(RouterNavigationRequest?)`.

Interactive foreground boundaries such as a QR scanner may call `IRouterNavigator.NavigateAsync(RouterNavigationRequest)` directly when the caller must observe navigation failure to recover UI state. Keep that exception local to the interactive boundary and still attach explicit `NavigationRequestProvenance`.

Raw auth callbacks are auth events, not a first-class router source. Handle them in the auth subsystem, then replay a deferred navigation request or dispatch an app-authored post-auth request if the completed auth flow produces a navigation intent.

Platform manifest setup for Universal Links and Android App Links is still the app's responsibility.

## Commerce Sample Walkthrough

The sample app lives at:

```text
samples/Commerce.Sample
```

The sample starts from:

```text
https://example.com/stores/northwind/products/123?variant=blue&promo=spring
```

### Route Matching

The route table matches:

```text
/stores/{storeId}/products/{productId:int}
```

and creates:

```csharp
new ProductDetailRoute(
    StoreId: "northwind",
    ProductId: 123,
    Variant: "blue",
    Promo: "spring");
```

### App Planning

The sample planner creates:

```text
WindowNode("main")
  TabsNode("store-tabs", selected: "catalog", default: "home")
    home -> StackNode("home-stack")
      StoreHomeRoute("northwind")
    catalog -> StackNode("catalog-stack")
      StoreCatalogRoute("northwind")
      ProductDetailRoute("northwind", 123, "blue", "spring")
    cart -> StackNode("cart-stack")
      CartRoute("northwind")
    orders -> StackNode("orders-stack")
      OrdersRoute("northwind")
```

### Presentation

The MAUI presenter materializes:

```text
TabbedPage
  Home tab -> NavigationPage
  Catalog tab -> NavigationPage
    StoreCatalogPage
    ProductDetailPage
  Cart tab -> NavigationPage
  Orders tab -> NavigationPage
```

The query values `variant` and `promo` modify the product route. They do not select tabs or define host structure.

## Recipes

### Open A Route From A Button

```csharp
button.Clicked += async (_, _) =>
{
    await navigator.NavigateAsync(
        new ProductDetailRoute("northwind", 456, "black", "spring"),
        NavigationRequestSource.InAppCommand);
};
```

### Format A Route For Sharing

```csharp
var route = new ProductDetailRoute("northwind", 123, "blue", "spring");
var path = routeTable.Format(route);

// /stores/northwind/products/123?variant=blue&promo=spring
```

### Handle A Push Notification

```csharp
var request = RouterNavigationRequest.FromUri(
    new Uri("https://example.com/stores/northwind/orders"),
    NavigationRequestSource.Push,
    provenance: new NavigationRequestProvenance(
        provider: "firebase-push",
        correlationId: notificationId),
    metadata: new Dictionary<string, object?>
    {
        ["notificationId"] = "order-ready"
    });

externalNavigationDispatcher.Dispatch(request);
```

### Handle A QR Code

```csharp
var scannedUri = new Uri("https://example.com/stores/northwind/products/789?promo=clearance");

externalNavigationDispatcher.Dispatch(
    RouterNavigationRequest.FromUri(
        scannedUri,
        NavigationRequestSource.QrCode,
        provenance: new NavigationRequestProvenance(
            provider: "qr-scanner",
            originalUri: scannedUri,
            correlationId: scanId)));
```

### Trust Only Known External Link Origins

Register `AllowedUriOriginPolicy` as an `INavigationRequestPolicy` for external request sources.

```csharp
builder.Services.AddSingleton<INavigationRequestPolicy>(_ =>
    new AllowedUriOriginPolicy(new[] { new Uri("https://example.com") }));
```

The policy applies to app links, push links, and QR links by default. In-app commands are not blocked by this policy.

### Use A Policy To Redirect

```csharp
public sealed class ClosedStorePolicy : INavigationRequestPolicy
{
    public ValueTask<RouterNavigationRequest> ApplyAsync(
        NavigationRequestPolicyContext context,
        RouterNavigationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (context.Route is ProductDetailRoute { StoreId: "closed" })
        {
            return ValueTask.FromResult(
                RouterNavigationRequest.FromRoute(
                    new StoreHomeRoute("northwind"),
                    request.Source,
                    request.WindowId,
                    request.Metadata));
        }

        return ValueTask.FromResult(request);
    }
}
```

Register it:

```csharp
services.AddSingleton<INavigationRequestPolicy, ClosedStorePolicy>();
```

When a request policy changes the navigation target, the navigator treats that as a redirect. A target change means `Uri`, `Route`, `Source`, or `WindowId` changed. Metadata and timestamp changes are preserved, but they do not count as redirects and do not restart the pipeline.

Redirects restart request-policy execution from the first policy, then continue through route matching, fallback, planning, presentation, diagnostics, and history as a normal navigation. This keeps authentication, tenant normalization, and canonical route policies deterministic.

The blessed runtime allows up to `16` redirects by default. If a policy loop repeats a target or the redirect chain exceeds the limit, navigation throws `RouteRedirectLoopException` and does not mutate logical state or history.

Redirect diagnostics use:

```csharp
NavigationDiagnosticEventKind.RequestRedirected
NavigationDiagnosticEventKind.RequestRedirectLoopDetected

NavigationDiagnosticDataKeys.RedirectCount
NavigationDiagnosticDataKeys.RedirectFrom
NavigationDiagnosticDataKeys.RedirectTo
NavigationDiagnosticDataKeys.RedirectTrace
```

### Add Native Transitions

```csharp
var entry = new RouteEntry(
    "product-123",
    new ProductDetailRoute("northwind", 123),
    new SharedElementNavigationTransition(
        new[] { SharedElementPair.SameId("product-123-image") },
        Fallback: new FadeNavigationTransition(),
        Duration: TimeSpan.FromMilliseconds(260)));
```

Mark matching MAUI elements with the attached shared-element id:

```csharp
MauiRouterTransition.SetSharedElementId(thumbnail, "product-123-image");
MauiRouterTransition.SetSharedElementId(heroImage, "product-123-image");
```

The MAUI adapter executes stock platform animation through `PlatformDefaultNavigationTransition`, native-view fade/slide handlers, and shared-element animations for single stack or modal push/pop operations. iOS and Mac Catalyst use UIKit snapshots over the active native container. Android uses native view snapshots and an in-surface overlay inside the MAUI page/container view; it does not use cross-activity shared-element APIs because router navigation stays inside the MAUI app surface. If a source or destination element is missing, the adapter emits a fallback diagnostic and runs the built-in transition fallback.

Initial materialization, restore, and bulk structural reconciliations remain non-animated unless exactly one stack/modal push or pop is being applied. Native user-driven back remains native-first: iOS swipe-back and Android native back behavior come from the real `NavigationPage`, and the presenter reconciles `Popped`/`PoppedToRoot` events after native stack changes. V1 does not drive Android predictive-back gesture progress or preview transitions.

### Represent A Modal

```csharp
var window = state.ActiveWindow!;

var modal = new ModalNode(
    "cart-modal",
    new RouteEntry("cart", new CartRoute("northwind")));

var nextState = state.ReplaceWindow(window with
{
    Modals = window.Modals.Concat(new[] { modal }).ToArray()
});

var plan = new NavigationPlan(nextState, NavigationPlanKind.Navigate, "Open cart modal");
```

### Unit Test Navigation Without MAUI

Use `AdamE.MauiRouter.Testing` for framework-neutral route, planner, navigator, diagnostics, state, and snapshot serializer helpers. The testing package has no MAUI or test-framework dependency.

Route table tests:

```csharp
var routes = RouteTableSubject.Create(routes => routes.Map(
    "/stores/{storeId}/products/{productId:int}",
    match => new ProductDetailRoute(match.Path("storeId"), match.Path<int>("productId")),
    format => format
        .PathParam("storeId", route => route.StoreId)
        .PathParam("productId", route => route.ProductId)));

routes.RoundTrips(
    new ProductDetailRoute("northwind", 123),
    "/stores/northwind/products/123");

routes.ShouldNotMatch("/stores/northwind/products/not-a-number", "route.not_matched");
```

Planner and navigator tests:

```csharp
var routeTable = RouteTable.Create(routes => routes.Map(
    "/stores/{storeId}/products/{productId:int}",
    match => new ProductDetailRoute(match.Path("storeId"), match.Path<int>("productId")),
    format => format
        .PathParam("storeId", route => route.StoreId)
        .PathParam("productId", route => route.ProductId)));

var planner = TestNavigationPlanner.EchoStack();
var navigator = RouterTestNavigator.Create(routeTable, planner);

var result = await navigator.NavigateAsync(
    RouterNavigationRequest.FromUri(
        new Uri("https://example.com/stores/northwind/products/123"),
        NavigationRequestSource.Test));

var stack = NavigationStateAssert.Root<StackNode>(result.State);
var route = NavigationStateAssert.StackTop<ProductDetailRoute>(stack);
```

State fixture builders:

```csharp
var state = TestNavigationState.State(
    "main",
    TestNavigationState.Window(
        "main",
        TestNavigationState.Tabs(
            "store-tabs",
            "catalog",
            "home",
            TestNavigationState.Branch("home", "Home", TestNavigationState.Stack("home-stack")),
            TestNavigationState.Branch("catalog", "Catalog", TestNavigationState.Stack(
                "catalog-stack",
                TestNavigationState.Entry("catalog", new StoreCatalogRoute("northwind")),
                TestNavigationState.Entry("product", new ProductDetailRoute("northwind", 123)))))));

var tabs = NavigationStateAssert.SelectedTabs(state, "catalog");
var catalogStack = NavigationStateAssert.SelectedBranch<StackNode>(tabs, "catalog");
NavigationStateAssert.StackRouteTypes(
    catalogStack,
    typeof(StoreCatalogRoute),
    typeof(ProductDetailRoute));
```

Diagnostics capture:

```csharp
var observer = new RecordingNavigationObserver();
var diagnostics = new NavigationDiagnostics();
diagnostics.AddObserver(observer);

var routeTable = RouteTable.Create(routes => routes.Map(
    "/stores/{storeId}/products/{productId:int}",
    match => new ProductDetailRoute(match.Path("storeId"), match.Path<int>("productId")),
    format => format
        .PathParam("storeId", route => route.StoreId)
        .PathParam("productId", route => route.ProductId)));

var navigator = RouterTestNavigator.Create(
    routeTable,
    TestNavigationPlanner.EchoStack(),
    new RouterTestNavigatorOptions
    {
        Diagnostics = diagnostics
    });

var uri = new Uri("https://example.com/stores/northwind/products/123");
await navigator.NavigateAsync(
    RouterNavigationRequest.FromUri(uri, NavigationRequestSource.Test));

var matched = observer.Single(NavigationDiagnosticEventKind.RouteMatched);
```

Snapshot serializer helpers:

```csharp
var routeTable = RouteTable.Create(routes => routes
    .Map(
        "/stores/{storeId}/catalog",
        match => new StoreCatalogRoute(match.Path("storeId")),
        format => format.PathParam("storeId", route => route.StoreId))
    .Map(
        "/stores/{storeId}/products/{productId:int}",
        match => new ProductDetailRoute(match.Path("storeId"), match.Path<int>("productId")),
        format => format
            .PathParam("storeId", route => route.StoreId)
            .PathParam("productId", route => route.ProductId)));

var serializer = new NavigationSnapshotTestSerializer(routeTable);
var snapshot = serializer.CreateSnapshot(state, NavigationHistory.Empty);
var restored = serializer.Restore(snapshot);

Assert.True(restored.Accepted);
Assert.Equal(state, restored.State);
Assert.Equal(NavigationHistory.Empty, restored.History);
```

### Observe Diagnostics In Tests

```csharp
var routeTable = RouteTable.Create(routes => routes.Map(
    "/stores/{storeId}/products/{productId:int}",
    match => new ProductDetailRoute(match.Path("storeId"), match.Path<int>("productId")),
    format => format
        .PathParam("storeId", route => route.StoreId)
        .PathParam("productId", route => route.ProductId)));

var planner = TestNavigationPlanner.EchoStack();
var diagnostics = new NavigationDiagnostics();
var events = new List<NavigationDiagnosticEventKind>();

diagnostics.EventWritten += (_, e) => events.Add(e.Kind);

var navigator = RouterTestNavigator.Create(
    routeTable,
    planner,
    new RouterTestNavigatorOptions
    {
        Diagnostics = diagnostics
    });

var uri = new Uri("https://example.com/stores/northwind/products/123");
await navigator.NavigateAsync(
    RouterNavigationRequest.FromUri(uri, NavigationRequestSource.Test));

Assert.Contains(NavigationDiagnosticEventKind.RouteMatched, events);
Assert.Contains(NavigationDiagnosticEventKind.PlanningCompleted, events);
```

## Recommended App Structure

A typical consuming app can organize navigation like this:

```text
Navigation/
  AppRoutes.cs
  AppRouteTable.cs
  AppNavigationPlanner.cs
  NavigationPolicies.cs

Pages/
  StoreHomePage.cs
  StoreCatalogPage.cs
  ProductDetailPage.cs
  CartPage.cs
  OrdersPage.cs

MauiProgram.cs
App.xaml.cs
```

Keep these boundaries clear:

- Route records are semantic and durable.
- Route table code parses and formats URLs.
- Planner code maps semantic routes to structural state.
- Page code renders UI and sends new navigation requests.
- Policies contain cross-cutting app decisions.

## What Not To Put In URLs

Use path segments for primary structure:

```text
/stores/northwind/products/123
/stores/northwind/cart
/stores/northwind/orders
```

Use query parameters for modifiers:

```text
?variant=blue
?promo=spring
?sort=name
?section=shipping
```

Avoid putting host structure in query parameters:

```text
/app?tab=catalog&id=123
```

That shape makes the URL less durable and forces host decisions into parsing.

## Build And Test

From the repository root:

```bash
dotnet test tests/AdamE.MauiRouter.Tests/AdamE.MauiRouter.Tests.csproj
dotnet build tests/AdamE.MauiRouter.Maui.Tests/AdamE.MauiRouter.Maui.Tests.csproj -f net10.0-maccatalyst
dotnet build src/AdamE.MauiRouter.Maui/AdamE.MauiRouter.Maui.csproj
dotnet build samples/Commerce.Sample/Commerce.Sample.csproj -f net10.0-maccatalyst
dotnet pack src/AdamE.MauiRouter/AdamE.MauiRouter.csproj
dotnet pack src/AdamE.MauiRouter.Maui/AdamE.MauiRouter.Maui.csproj
dotnet pack src/AdamE.MauiRouter.Testing/AdamE.MauiRouter.Testing.csproj
```

For platform execution with XHarness and manual release checks, see [docs/release-checklist.md](docs/release-checklist.md).

For the next Scavos dogfooding checkpoint on whether route-owned metadata and request intent should stay split, see [docs/app-route-request-dogfood-checkpoint.md](docs/app-route-request-dogfood-checkpoint.md).

Useful source scans:

```bash
rg "Shell|Prism" src samples -g '!**/bin/**' -g '!**/obj/**'
rg "stores/northwind/products" samples tests -g '!**/bin/**' -g '!**/obj/**'
```

## Current Limitations

These are intentional v1 boundaries:

- No Shell integration.
- No Prism integration.
- No Windows MAUI adapter target.
- No broad `netstandard` target.
- No source generators.
- No attribute routing.
- No full deferred deep-link or attribution SDK replacement.
- No complete multi-window orchestration beyond core state seams.
- No custom tab or flyout selection animations in the first transition slice.
- No virtual-host-only renderer as the default MAUI experience.

## Roadmap Ideas

Likely future work:

- Source generator for route tables and formatters.
- Attribute-based route discovery.
- Platform association-file generation or validation helpers.
- Richer transition coordination for multi-step/bulk reconciliations.
- Rich multi-window orchestration.
- Additional presenters for MVU or custom host models.
- Package publishing workflow.

## Glossary

| Term | Meaning |
| --- | --- |
| App route | Typed semantic destination, such as `ProductDetailRoute`. |
| Navigation request | A route or URI plus source metadata. |
| Route matching | URI/template parsing into a typed route. |
| App planning | App-owned conversion from typed route to navigation state. |
| Navigation state | Canonical logical model of windows, hosts, branches, stacks, and modals. |
| Navigation plan | Intended mutation from current state to target state. |
| Presenter | Adapter that materializes state into UI. |
| Reconciliation | Native UI event updating logical state/history. |
| Host | Structural UI container such as tabs, flyout, stack, modal, or window. |
| Route entry | A route placed into a structural node, usually a stack or modal. |

## License

MIT. See [LICENSE](LICENSE).

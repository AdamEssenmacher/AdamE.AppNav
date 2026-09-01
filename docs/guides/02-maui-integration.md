# MAUI integration

[Documentation home](../index.md)

This guide walks through one common MAUI integration shape for AppNav. It is a
complete example, not the only public way to use the library. It continues the
fictional RPG [Glyphmere](../concepts/01-routing-and-metadata.md#meet-glyphmere)
where a concrete domain example makes the boundary clearer.

## Choose project boundaries deliberately

`AdamE.AppNav` targets plain `net10.0`; `AdamE.AppNav.Maui` owns the MAUI and
native target frameworks. A larger application can preserve that boundary in
its own projects:

| Pure inner project, for example `Glyphmere.Presentation` | Outer `Glyphmere.Maui` project |
| --- | --- |
| References `AdamE.AppNav` | References `AdamE.AppNav.Maui` and the inner project |
| Typed routes and route metadata | MAUI pages and generated page mappings |
| Navigation model, policies, and app transforms | `AddAppNav(...)`, startup, and lifecycle ingress |
| Render-independent view models that request typed routes | Native presentation and UI-thread work |
| Fast routing, policy, topology, history, and Back tests | Focused adapter, platform, and UI tests |

The MAUI composition root joins the generated route table and logical model to
the generated page module and native presenter. The inner project therefore
does not need to reference a `Page` or a platform target such as
`net10.0-ios`. This layering is optional; the minimal sample keeps everything
together to reduce onboarding ceremony.

Read [Application architecture and
testing](03-application-architecture-and-testing.md) for the full Glyphmere
example. `Presentation` there means an application layer containing
render-independent view models; it is not the same thing as AppNav's host-facing
`INavigationPresenter` adapter contract.

## One navigation owner: no Shell or Prism navigation

**Do not combine an AppNav-managed window with
`Microsoft.Maui.Controls.Shell`, Shell routing, or Prism navigation.** AppNav
does not adapt to or synchronize with another navigation framework's route
table, stack, history, or Back handling.

For the window attached to AppNav:

- do not use `AppShell`, `Shell.Current`, `Shell.Current.GoToAsync`, or Shell
  route registration;
- do not issue navigation through Prism's navigation service;
- do not let another router push, pop, replace, select, or dismiss content on
  the native stacks and modals owned by the AppNav presenter;
- do use AppNav routes and `IRouterNavigator` for semantic navigation, and let
  host-originated native actions return through AppNav reconciliation.

AppNav commits logical state only after its presenter successfully applies a
plan. Native mutations issued through another owner bypass that transaction,
so AppNav can no longer guarantee that logical state, visible UI, history, and
Back agree.

This rule is intentionally scoped to navigation ownership. The application can
use an MVVM toolkit, dependency-injection container, messaging system, or
non-navigation parts of another framework. Route-owned presentation pages also
remain inside AppNav's MAUI transaction; they are not a second navigator.

An existing Shell- or Prism-navigated window therefore needs a deliberate
migration: replace its route registrations and navigation calls with AppNav
routes, topology, page mappings, and startup. This preview does not include a
Shell or Prism bridge or a mixed-ownership migration mode.

## Map routes to MAUI presentation

The built-in MAUI adapter maps each logical route entry to one anchor `Page`.
That is an adapter requirement, not the definition of the route. The route
remains semantic destination identity, while the page and its view model are
application-owned presentation choices.

An anchor page can display one route in a particular state, host custom
controls, and update when a compatible route entry is reused. A route can also
own a native segment of additional presentation pages. Those pages participate
in native Back without becoming new routes or logical history entries. See
[Route-owned presentation pages](../advanced/route-owned-presentation-pages.md)
for that advanced lifecycle.

View models can request typed routes and can consume route information supplied
by the app's page or composition layer. AppNav does not navigate to a view-model
type or require a one-route-to-one-view-model relationship. A custom host
adapter may map logical entries to non-page artifacts; the MAUI adapter uses
pages so it can participate in MAUI's native navigation containers.

## Common flow

1. Define durable semantic destinations as `AppRoute`.
2. Use `AppRoute` or `AppRouteRequest` in app code; construct
   `RouterNavigationRequest` for external events and runtime infrastructure.
3. Keep route-owned metadata app-defined in a `RouteStateRegistry` when
   formatting, persistence, or downstream app behavior depend on it.
4. Register `AddAppNav(...)`, optional persistence or deferred-request
   services, and `AddAppNavStartup(...)` as needed by the app.
5. Use `IAppNavStartupService`, `IMauiExternalNavigationDispatcher`, or direct
   `IRouterNavigator` calls where MAUI lifecycle or runtime boundaries need
   explicit control.

```text
App code / host code     Boundary / lifecycle        Router runtime
---------------------    ---------------------       --------------
AppRoute / AppRouteRequest       RouterNavigationRequest
                                              ->    transformers -> match -> policies -> planner -> presenter
```

## App-authored navigation

`AppRouteRequest` is useful in:

- route factories
- presentation actions that request typed destinations
- URL formatting and sharing
- compatibility normalization that is still app-authored

Typical app code:

```csharp
await navigator.NavigateAsync(
    AppRouteRequest
        .For(new InventoryItemRoute(itemId))
        .WithMetadata(
            GlyphmereRouteMetadata.CompareWithItemId,
            equippedItemId),
    RouterNavigationDisposition.Contextual);
```

Use plain `AppRoute` when no route-owned metadata is involved and the simpler
overload is clearer.

## Route-owned metadata

Keep route-owned metadata app-defined in a `RouteStateRegistry`.

- canonical metadata belongs in the URI shape
- restorable metadata belongs in persistence
- ephemeral metadata belongs only in live in-memory requests and state

See [Choose a metadata
lifetime](../concepts/01-routing-and-metadata.md#choose-a-metadata-lifetime) for
the URI, persistence, and live-memory decision rules.

Register that registry with navigation persistence when route-owned metadata
participates in formatting or restore.

MAUI page constructors still receive `AppRoute`, not route-entry metadata. If
a page needs route-entry metadata, use `IMauiRoutePageLifecycleHook` or
app-specific mapping.

## Runtime and external boundaries

`RouterNavigationRequest` is useful when URI, source, provenance, disposition,
request metadata, or window targeting matter explicitly.

Common runtime uses:

- app-link ingress
- app-owned push and QR boundaries
- startup fallback
- restore
- request-policy pipelines
- pre-match request transformation
- deferred request replay
- tests

`RouterNavigationRequest` can carry `NavigationRequestProvenance`, which is
runtime context describing how a request entered AppNav: provider, original
URI, referrer URI, correlation ID, cold-start flag when known, and string
attributes. Keep provenance out of `AppRoute`, `AppRouteRequest`, route
formatting, and `RouteStateRegistry`. AppNav records context for platform links
it handles; apps record context from providers they integrate. For field
ownership, see
[Requests and provenance](../concepts/03-requests-and-provenance.md).

A request always has exactly one target. Create it with `FromUri`, `FromRoute`,
or `FromRouteRequest`, and use `WithTarget(Uri)` or `WithTarget(AppRoute)` when
a transformer or policy replaces that target.

Typical boundary code:

```csharp
var mapUri = new Uri(
    "https://links.glyphmere.example/pause/world-map/regions/ashen-coast");
var request = RouterNavigationRequest.FromUri(
    mapUri,
    NavigationRequestSource.AppLink,
    provenance: new NavigationRequestProvenance(
        provider: "branch",
        originalUri: mapUri,
        correlationId: branchCorrelationId));
```

## MAUI wiring and startup

One common MAUI setup uses:

- `AddAppNav(...)`
- optional persistence or deferred-request registration
- `AddAppNavPages(...)` when page mappings live outside the composition root
- `AddAppNavStartup(...)`
- `IAppNavStartupService.Start(window, windowId)` from `CreateWindow`

Startup ordering depends on the request source:

```text
buffered app link -> attach window -> await that dispatch epoch
                                       | success: startup completes
                                       + terminal: continue

no successful app link -> detect deferred work -> create fallback if configured
                                                   |
                                                   v
                                      present fallback -> attach window
```

If no fallback is configured, startup still attaches the window. Deferred
request detection reports pending protected work; the owning auth flow decides
when to invoke replay.

App-link lifecycle ingress targets one active AppNav MAUI host per process. The
most recently created host receives platform callbacks. Disposing that host
cancels its in-flight external navigation and drops its queued requests.
Callbacks arriving after disposal remain buffered until a replacement host is
created. Disposing an older host does not unregister a newer one.

`AddAppNav(...)` owns the unkeyed `IRouterNavigator` registration because that
navigator must share the runtime's built-in MAUI presenter. Do not register or
replace an unkeyed navigator when using `AddAppNav(...)`; keyed navigators may
coexist for unrelated flows. Advanced hosts that need a custom navigator must
instead own the complete navigator/presenter pair through
`RouterNavigatorFactory` and skip the MAUI `AddAppNav(...)` composition helper.

The final `AddAppNav(...)` callback configures the three app-owned router settings. Its
`FallbackRouteFactory` runs only when a URI has no matching route; `MaxRedirects` and
`MaxHistoryEntries` default to 16 and 128. Startup fallback remains a separate
concern configured through `AddAppNavStartup(...)`:

```csharp
services.AddAppNav(
    routes,
    model,
    pages => pages.AddModule(AppNavGenerated.MauiPageModule),
    navigator =>
    {
        navigator.FallbackRouteFactory = context => new NotFoundRoute(context.Request.Uri!);
        navigator.MaxRedirects = 16;
        navigator.MaxHistoryEntries = 128;
    });
```

Diagnostics, logging, request transformers, request policies, Back navigation,
presenter ownership, and initial state remain owned by the MAUI composition
root and are not replaceable through this callback.

`AddAppNav(...)` discovers `INavigationRequestTransformer` and
`INavigationRequestPolicy` registrations in order. Transformers run before
route matching, including for unmatched and redirected targets. Policies run
after matching and use `NavigationRequestPolicyContext.Route` for the resolved
route and `RouteMetadata` for metadata produced by the current match.
`Request.Metadata` contains only explicit request-envelope metadata. Returning
`WithTarget(...)` preserves the request envelope; returning a newly constructed
request authoritatively replaces it. Route metadata from an old target never
crosses a redirect.

The MAUI `AppNavRuntime` is the sole shutdown owner for its factory-created
navigator and presenter. Both disposal forms stop admission and cancel accepted
work; asynchronous disposal additionally waits for rollback, native cleanup,
page release, and async scope disposal. Work past the presentation commit point
completes successfully. Public presentation surfaces force runtime creation so
DI cannot dispose presenter dependencies independently.

Deferred replay is lease-based and at-least-once. Acquiring a lease does not
remove persisted requests; successful navigation is removed only after durable
acknowledgement. A crash between presentation and acknowledgement may replay
once more but cannot lose the request. Schema 3 persists canonical route-backed
requests and safe provider provenance. Schema-2 preview data is reset once;
future schema data is quarantined byte-for-byte for downgrade safety. Malformed
or oversized data is quarantined. A quarantine failure preserves the original
and fails safely.

Diagnostics use `NavigationDiagnosticDataMode.Safe` by default for observers,
logging, and activities. Safe mode emits structural types, templates, codes,
counts, and timings; it reduces absolute URIs to their origin and omits raw
paths, application-defined navigation IDs, and presentation mismatch values.
Call `AddAppNavDiagnostics(...)` to opt into Full mode, and register
`INavigationDiagnosticRedactor` for app-specific redaction. Redactor failures
fall back to built-in Safe output without affecting navigation.

See [Logging, tracing, and diagnostics](../reference/diagnostics.md) for the
`AdamE.AppNav.Diagnostics` logger category, event observers, activity source,
severity mapping, privacy comparison, and operation-level troubleshooting.

## Other public runtime seams

`IRouterNavigator` exposes full-envelope navigation, Back, and
`ReconcileAsync(...)`. Four typed extension methods cover app-authored routes.
Host-owned boundaries construct a complete `RouterNavigationRequest`.

### Cancelable and deferrable Back

Register singleton `IBackNavigationPolicy` implementations when leaving the
current logical destination may require an asynchronous save or confirmation.
The router first creates and validates the deterministic candidate Back plan,
then evaluates policies in DI registration order. Each policy receives the
exact candidate plan and returns `Continue` or `Cancel`. Cancellation is a
normal `BackNavigationStatus.Canceled` result: no native presentation, logical
state, or history mutation occurs. An unhandled Back has no candidate plan and
does not invoke policies.

Policies execute inside the serialized router operation. They may await UI or
persistence work, but they must not call `NavigateAsync`, `BackAsync`, or
`ReconcileAsync` on the same navigator. Exceptions and cancellation-token
cancellation fail the operation without committing navigation.

For application commands, call `IRouterNavigator.BackAsync(...)` normally. For
MAUI host Back affordances, use `IMauiHostBackDispatcher`; it first pops a
route-owned presentation page and invokes guarded logical Back only when the
logical route would leave:

```csharp
MauiHostBackResult result = await hostBackDispatcher.BackAsync();
```

A synchronous page override can queue one safely observed operation. Repeated
presses are coalesced while it is pending. Because an asynchronous result
arrives after `OnBackButtonPressed()` returns, provide a callback that directly
performs the app's platform fallback when the result is `Unhandled`:

```csharp
protected override bool OnBackButtonPressed()
{
    return hostBackDispatcher.TryBack(onUnhandled: PerformPlatformBackFallback)
        || base.OnBackButtonPressed();
}
```

`PerformPlatformBackFallback` is app/platform-owned—for example, it can finish
the Android activity at the logical root. It must perform the fallback itself;
calling `base.OnBackButtonPressed()` later cannot return its result to the
original platform callback. The fallback runs on the MAUI main thread. A
canceled policy remains consumed and does not invoke it.

Custom toolbar commands should await `IMauiHostBackDispatcher.BackAsync()`.
AppNav does not automatically replace MAUI platform handlers or gestures. A
native pop, swipe, or modal dismissal that commits without going through this
dispatcher is reconciled afterward and cannot be asynchronously vetoed
retroactively.

### Native presentation motion

The MAUI presenter uses the platform-default animation for a singular visible
stack push or pop, or modal push or pop. Initial materialization, native
reconciliation, root or container replacement, composite changes, rollback,
recovery, and cleanup remain unanimated. Changes confined to inactive branches
are applied without animation and do not prevent an otherwise singular visible
operation from using native motion. A logical route pop that must also remove
route-owned presentation pages is composite and therefore unanimated.

Applications can replace `IMauiPresentationOperationPolicy` before calling
`AddAppNav(...)` to suppress an eligible operation based on its plan, runtime
presentation context, operation kind, and source or target route entry:

```csharp
services.AddSingleton<IMauiPresentationOperationPolicy, ReducedMotionPolicy>();
services.AddAppNav(
    routes,
    model,
    pages => pages.AddModule(AppNavGenerated.MauiPageModule));
```

The policy returns `MauiPresentationOperationOptions` with `Automatic`,
`PlatformDefault`, or `Suppressed` motion. It is invoked only for operations
the presenter has already determined are safe to animate. It cannot enable
motion for reconciliation, composite changes, rollback, or recovery, and it
must not re-enter the router. This seam controls MAUI's native animated flag;
AppNav still does not provide custom, shared-element, or predictive-Back
transitions.

### Branch-host presentation

Branch hosts render as `TabbedPage` by default. Presentation is selected per
logical branch-host id, so one application can use tabs, a flyout, and an
application-owned control surface at the same time:

```csharp
services.AddAppNavMauiPresentation(options =>
    options.MapBranchHost(
        "store-branches",
        new MauiFlyoutBranchHostFactory(
            "Store",
            FlyoutLayoutBehavior.Default,
            isGestureEnabled: true)));
```

Use `new MauiTabbedBranchHostFactory()` for an explicit tab host, or pass an
application implementation of `IMauiBranchHostFactory` for custom UI:

```csharp
services.AddAppNavMauiPresentation(options =>
{
    options.MapBranchHost("store-branches", new MauiFlyoutBranchHostFactory("Store"));
    options.MapBranchHost("settings", new MauiTabbedBranchHostFactory());
    options.MapBranchHost("workspace", new WorkspaceBranchHostFactory());
});
```

A factory declares its supported placements (`WindowRoot`, `Nested`, and
`ModalContent`). AppNav checks those capabilities against the complete target
topology before creating pages or mutating native UI. The returned
`IMauiBranchHost` owns its page, applies the ordered branch list and selected
branch, and raises `SelectionChanged` only for host-originated user actions.
Updates must be reversible; AppNav commits them after verification and invokes
rollback during failure or cancellation. The presenter releases retired hosts
and branch trees exactly once.

The built-in flyout menu uses each branch title and root-page icon. Inactive
detail trees remain alive, so their navigation stacks, binding contexts, and
page scopes behave like inactive tabs. Selecting a menu item closes the flyout
and reconciles the logical selected branch with `BranchChanged`.

`MauiFlyoutBranchHostFactory` supports only `WindowRoot`; nested and modal
flyouts are rejected before native presentation because `FlyoutPage` is a root
navigation control. Custom factories can support a different placement set.
Unmapped branch hosts retain the existing `TabbedPage` behavior.

## Route-owned presentation pages

Workflows that use several native pages for one semantic route are documented
separately in [Route-owned presentation pages](../advanced/route-owned-presentation-pages.md).
That advanced guide covers DI scopes, lifecycle hooks, reentrancy, native Back,
transactional push/pop, rollback, and consistency faults.

## Notes

- Built-in MAUI app-link ingress sets provenance automatically.
- App-owned external sources should resolve
  `IMauiExternalNavigationDispatcher`, attach explicit
  `NavigationRequestProvenance`, and call `TryDispatch(...)`.
- Interactive foreground boundaries may call
  `IRouterNavigator.NavigateAsync(RouterNavigationRequest)` directly when the
  caller must observe navigation failure to recover UI state.
- AppNav does not ship Branch, push, QR scanner, or auth-provider SDK integrations.
- Raw auth callbacks belong to the auth subsystem; the router should usually
  see deferred replay or an app-authored post-auth request.

## Next steps

- Define [routing and metadata](../concepts/01-routing-and-metadata.md).
- Keep navigation decisions testable with
  [Application architecture and testing](03-application-architecture-and-testing.md).
- Handle [navigation outcomes and failures](04-navigation-outcomes-and-failure-handling.md)
  at the boundary that can recover the initiating UI.
- Configure [external navigation](05-external-navigation.md) only after defining
  trusted origins.
- Add [deferred navigation](06-deferred-navigation.md) only for a real auth defer/replay flow.
- Diagnose integration failures with [Troubleshooting](07-troubleshooting.md).

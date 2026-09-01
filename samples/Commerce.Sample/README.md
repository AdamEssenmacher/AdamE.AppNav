# Commerce sample

Commerce is the advanced MAUI sample for AdamE.AppNav. It uses a
`BranchHostNavigationModel<AppRoute>` to present four branches through a native flyout, generated route and page
modules, typed in-app requests, a typed startup fallback, and opt-in external URI ingress. It
does not use Shell, Prism, deferred persistence, or an app-specific planner.

For the minimal onboarding path, start with
[`GettingStarted.Sample`](../GettingStarted.Sample/README.md).

## What the sample proves

`MauiProgram.CreateMauiApp` registers:

- the generated `AppNavGenerated` route table and MAUI page module;
- `CommerceNavigationModel.Create()`, which declares the Home, Catalog, Cart, and Orders
  branches;
- a typed startup `FallbackRouteFactory` that creates `ProductDetailRoute`; AppNav wraps it in an
  in-app canonical startup request;
- a router `FallbackRouteFactory` that turns otherwise unmatched URIs into
  `CommerceNotFoundRoute`;
- `https://example.com` and `https://legacy.example.com` as trusted production origins;
- `appnav-commerce://shop` as a Debug-only origin; and
- `LegacyProductUrlTransformer`, which rewrites `/p/{productId}` before route matching.

`App.CreateWindow` creates a real MAUI `Window` and calls the observed synchronous
`IAppNavStartupService.Start(window, "main")` helper. Route types and pages are discovered by
the two source generators. Page constructors receive typed `AppRoute` values, while page code
navigates through the four typed `IRouterNavigator` extension overloads.

Route identity and route-entry metadata remain separate. For example,
`CommerceRouteFactory.ProductDetail` creates an `AppRouteRequest` containing a
`ProductDetailRoute` and optional typed `campaign` metadata. The generated route table formats
that metadata as the canonical query without adding it to the page-constructor route type.

## Topology and independent flyout branch stacks

`CommerceNavigationModel` declares one canonical `store-tabs` branch host and maps that host to
`FlyoutPage` presentation in `MauiProgram` through `MauiFlyoutBranchHostFactory`. Each branch owns a distinct retained native navigation stack:

| Branch | Root | Canonical detail shape |
| --- | --- | --- |
| Home | `StoreHomeRoute` | Home |
| Catalog | `StoreCatalogRoute` | Catalog -> Product detail |
| Cart | `CartRoute` | Cart |
| Orders | `OrdersRoute` | Orders |

Contextual navigation changes only the owning branch and selects it. Other branch stacks remain
dormant rather than being rebuilt. To see that behavior:

1. Start the app; the typed fallback builds Catalog -> Product 123.
2. Open the native flyout and select Cart.
3. Open the flyout and select Catalog again; Product 123 is still the visible Catalog entry.
4. Use the native or system Back action; Catalog becomes visible without changing the other
   branches.

Selecting a native flyout branch reconciles the logical state with `BranchChanged`. Completing a native
Back gesture reconciles with `HostBack`. The synthesized history request is recorded with the
host-neutral `HostReconciliation` source.

## Every disposition

The running sample exercises the complete standard disposition contract:

| Disposition | Sample action | Result |
| --- | --- | --- |
| `Auto` | Home's **Browse catalog** or **View cart** buttons | In-app requests use contextual push/branch selection, with canonical fallback. External requests using `Auto` are canonical. |
| `Contextual` | Select a product in Catalog or choose **Add to cart** on a product | Push into the owning branch while preserving every other branch stack. |
| `ReplaceCurrent` | Choose **Open product 456** on a product | Replace the Catalog stack's current detail; use canonical planning if contextual replacement is unavailable. |
| `Canonical` | Choose **Back to catalog** on a product | Rebuild the declared canonical branch-host topology for the Catalog route. |

The route-only and `AppRouteRequest` overloads are both represented. External boundaries do not
use those conveniences: MAUI constructs the complete `RouterNavigationRequest` envelope after
the URI passes origin validation.

## Run the sample

From the repository root, build any supported target with warnings treated as errors:

```sh
dotnet build samples/Commerce.Sample/Commerce.Sample.csproj \
  -c Debug \
  -f net10.0-maccatalyst \
  -warnaserror
```

Substitute `net10.0-android` or `net10.0-ios` as needed. The canonical release gate also publishes
Commerce with representative full-trim, linked, and NativeAOT settings.

The HTTPS origins demonstrate production trust configuration, but a real app must additionally
own the domains and configure Android App Links and Apple Associated Domains. The Debug-only
custom scheme is registered by this sample on Android, iOS, and Mac Catalyst so local cold- and
warm-ingress checks are runnable without domain ownership. Release builds neither register nor
trust that scheme.

Use either route below in the platform commands:

```text
appnav-commerce://shop/stores/northwind/products/456?variant=black&promo=spring&campaign=manual
appnav-commerce://shop/p/456
```

The second route exercises the legacy transformer.

### Android emulator or device

Launch the Debug app once from Rider or with `dotnet build -t:Run`, then issue a cold link:

```sh
dotnet build samples/Commerce.Sample/Commerce.Sample.csproj \
  -c Debug \
  -f net10.0-android \
  -t:Run

adb shell am force-stop com.companyname.commerce.sample
adb shell am start -W \
  -a android.intent.action.VIEW \
  -c android.intent.category.BROWSABLE \
  -d 'appnav-commerce://shop/stores/northwind/products/456?variant=black&promo=spring&campaign=cold'
```

Without force-stopping the app, repeat the final `adb shell am start` command with a different
product or `campaign=warm`. `LaunchMode.SingleTop` delivers that warm intent to the existing
activity.

### iOS simulator

With a simulator booted, build and install the Debug bundle, then open the first URL while the
process is terminated:

```sh
dotnet build samples/Commerce.Sample/Commerce.Sample.csproj \
  -c Debug \
  -f net10.0-ios \
  -r iossimulator-arm64

xcrun simctl install booted \
  samples/Commerce.Sample/bin/Debug/net10.0-ios/iossimulator-arm64/Commerce.Sample.app
xcrun simctl terminate booted com.companyname.commerce.sample 2>/dev/null || true
xcrun simctl openurl booted \
  'appnav-commerce://shop/stores/northwind/products/456?variant=black&campaign=cold'
```

Run `xcrun simctl openurl` again with `campaign=warm` while Commerce is foregrounded to exercise
warm ingress.

### Mac Catalyst

Build and open the Debug bundle once so Launch Services sees its URL registration:

```sh
dotnet build samples/Commerce.Sample/Commerce.Sample.csproj \
  -c Debug \
  -f net10.0-maccatalyst \
  -r maccatalyst-arm64

open samples/Commerce.Sample/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/Commerce.Sample.app
```

Quit Commerce, then run the first command for cold ingress. Run the second while the app remains
open for warm ingress:

```sh
open 'appnav-commerce://shop/stores/northwind/products/456?variant=black&campaign=cold'
open 'appnav-commerce://shop/stores/northwind/products/789?promo=clearance&campaign=warm'
```

Diagnostics use AppNav's privacy-safe observer/logger/activity contract. Rejection and lifecycle
events never include raw query or provenance values.

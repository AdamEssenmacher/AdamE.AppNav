# Commerce.Sample

Commerce.Sample is a production-pattern MAUI sample for AdamE.MauiRouter. It uses no Shell or Prism and demonstrates one complete MAUI integration shape: app code authors `AppRouteRequest`, runtime boundaries use `RouterNavigationRequest`, `AddMauiRouter` plus `AddMauiRouterStartup` provide MAUI wiring, and route-owned metadata is registered through a `RouteStateRegistry`.

The sample keeps route identity and route-entry metadata intentionally separate:

- Pages receive typed `AppRoute` constructor parameters.
- In-app page code navigates with `AppRouteRequest` through `IRouterNavigator`.
- Startup fallback remains a `RouterNavigationRequest.FromUri(...)` boundary.
- The sample route table formats the canonical `campaign` metadata query from `AppRouteRequest` without pushing that value into page constructor route types.

The catalog-to-product flow demonstrates `SharedElementNavigationTransition` by assigning the same shared-element id to the product swatch in the catalog row and the product hero swatch on the detail page. iOS and Mac Catalyst run the transition with UIKit snapshots. Android runs it with native view snapshots and an overlay inside the MAUI page surface. Bulk stack reconciliation, initial restore, and missing shared elements fall back to deterministic non-animated or fallback transition behavior.

Platform link entitlement and asset-link setup remains an app responsibility; the sample only wires the MAUI lifecycle hooks that deliver links into the router.

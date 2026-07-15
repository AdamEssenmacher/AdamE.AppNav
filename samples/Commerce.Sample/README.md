# Commerce.Sample

Commerce.Sample is a production-pattern MAUI sample for AdamE.AppNav. It uses no Shell or Prism and demonstrates one complete MAUI integration shape: app code authors `AppRouteRequest`, runtime boundaries use `RouterNavigationRequest`, `AddAppNav` plus `AddAppNavStartup` provide MAUI wiring, and route-owned metadata is registered through a `RouteStateRegistry`.

The sample keeps route identity and route-entry metadata intentionally separate:

- Pages receive typed `AppRoute` constructor parameters.
- In-app page code navigates with `AppRouteRequest` through `IRouterNavigator`.
- Startup fallback remains a `RouterNavigationRequest.FromUri(...)` boundary.
- `LegacyProductUrlTransformer` rewrites `/p/{productId}` before route matching, so legacy URLs need no fallback route.
- Source-generated route and page modules provide the route table and standard page mappings.
- The generated route table formats the canonical `campaign` metadata query from `AppRouteRequest` without pushing that value into page constructor route types.

Platform link entitlement and asset-link setup remains an app responsibility; the sample only wires the MAUI lifecycle hooks that deliver links into the router.

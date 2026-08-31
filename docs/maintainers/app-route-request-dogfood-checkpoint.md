# AppRouteRequest dogfood checkpoint

[Documentation home](../README.md)

This is an internal public-preview design checkpoint for AppNav maintainers. It
is not application integration guidance.

Source generators are now part of the preview package contract. Revisit this API split during Scavos public-preview
dogfooding and before declaring the API stable.

Re-open the API question only if one or more of these pressures appear:

- new app-specific wrappers or helpers whose main purpose is bundling source or disposition with route requests
- repeated UI callsites that always combine `AppRouteRequest` with the same explicit `NavigationRequestSource` and `RouterNavigationDisposition`
- new metadata-bearing navigations that still feel forced back into raw `RouterNavigationRequest`

If those pressures do not appear, keep the current split:

- `AppRouteRequest` is the app-facing abstraction for semantic route plus route-owned metadata
- `RouterNavigationRequest` remains the runtime transport shape for URI ingress, source, window targeting, policy, persistence, and reconciliation

The public-preview API keeps one `IRouterNavigator` surface and typed route extension methods. External boundaries still
construct the full `RouterNavigationRequest` envelope; no second navigator facade is planned for the preview.

## Next steps

- Revisit this checkpoint during Scavos preview dogfooding.
- Record any accepted API change in the relevant [release notes](../release-notes/README.md).

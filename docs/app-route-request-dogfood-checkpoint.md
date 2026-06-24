# AppRouteRequest Dogfood Checkpoint

Revisit this after another Scavos dogfooding cycle, before adding source generators or treating the public API as broadly frozen.

Re-open the API question only if one or more of these pressures appear:

- new app-specific wrappers or helpers whose main purpose is bundling source or disposition with route requests
- repeated UI callsites that always combine `AppRouteRequest` with the same explicit `NavigationRequestSource` and `RouterNavigationDisposition`
- new metadata-bearing navigations that still feel forced back into raw `RouterNavigationRequest`

If those pressures do not appear, keep the current split:

- `AppRouteRequest` is the app-facing abstraction for semantic route plus route-owned metadata
- `RouterNavigationRequest` remains the runtime transport shape for URI ingress, source, window targeting, policy, persistence, and reconciliation

This documentation/sample pass intentionally codified that split in the README, the MAUI integration guide, the sample app, and repository guardrails without adding a new facade.

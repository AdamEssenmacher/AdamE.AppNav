# Diagnostics

`NavigationDiagnostics` reports each pipeline and presentation phase. Safe mode is the default for observers, logging,
and activities.

Safe diagnostics retain structural information such as event kind, phase, route/type names, templates, decision,
counts, timings, failure class, queue attempt, and sanitized URI origin. They omit raw paths where unsafe, query values,
application navigation IDs, mismatch payloads, and provenance values.

External ingress adds structural events for rejection, overflow, deduplication, retry, expiry, and terminal drop.
Persistence adds reset, prune, overflow, corrupt/quarantine, and future-schema events. These events are the bounded
dead-letter signal; there is no durable raw-request quarantine API.

Call `AddAppNavDiagnostics` to configure data mode. Full mode is an explicit application choice. Register an
`INavigationDiagnosticRedactor` for app-specific values. A redactor failure falls back to built-in safe output and does
not interrupt navigation.

Do not log raw external query strings, original/referrer URIs, correlation IDs, arbitrary provenance attributes, or
credential-bearing configuration values.

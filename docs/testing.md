# Testing

## Deterministic gate

Run twice from a clean worktree:

```bash
eng/verify.sh release
eng/verify.sh release
```

Focused modes are `contracts`, `packages`, and `native`. The release mode covers core, route-generator,
MAUI-generator, adapter-contract, package-content, cold-cache consumer, allocations, supported targets, trim/AOT
analysis, and representative native publishes. Warnings are errors, except explicitly isolated external advisory checks.

Default packing must produce `0.1.0-preview.local`, never stable `1.0.0`. The isolated consumer references only the
packed `AdamE.AppNav.Maui` package and restores through an empty global-package and HTTP cache.

## Runtime gate

Use `eng/run-maui-platform-tests.sh` for Android, iOS, and Mac Catalyst. Runtime tests attach to a real `Window`, run on
the main thread, and verify logical state, native tree, history, and released scopes.

A lane fails on zero tests, unexpected skips, missing results, DeviceRunners failure, crash markers, unhandled
exceptions, presentation consistency faults, or retained scopes. PRs compile every target and execute Mac Catalyst
tests. Nightly runs latest Android/iOS/Mac Catalyst; weekly and release runs also exercise Android API 28.

## Scenario coverage

Cover cold/warm ingress, startup buffering, burst overflow, tab changes, Back/swipe, modal dismissal, deferred restore,
retry bypass, cancellation, injected presentation failure, rollback/rebuild, and disposal. Scavos provides the advanced
five-branch dogfood matrix; the in-memory lab is preferred for deterministic native automation.

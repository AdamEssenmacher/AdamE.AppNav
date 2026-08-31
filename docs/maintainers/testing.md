# Testing

[Documentation home](../index.md)

This document is for AppNav repository maintainers. Application consumers do
not need to run the release gates.

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

## Local SonarQube analysis

SonarQube analysis is opt-in developer tooling for the shared local stack at `~/sonarqube-local`. It is not a CI,
pull-request, or release gate. Do not call the script from GitHub Actions or any other hosted CI workflow.

Create a token in the local SonarQube instance and expose it as `SONAR_TOKEN`, either in the shell or in
`~/sonarqube-local/.env`. `SONAR_HOST_URL` defaults to `http://localhost:9000` and can be overridden for another local
endpoint.

```bash
./scripts/sonar-scan.sh
./scripts/sonar-scan.sh --no-tests
```

The default command scans the portable `net10.0` production graph and collects OpenCover data from the core,
adapter-contract, route-generator, and MAUI-generator test suites. `--no-tests` performs a faster build-only scan.
Build-only scans upload to the separate SonarQube project `adame-appnav-build`, preserving coverage history in the
full-analysis project `adame-appnav`. Native MAUI tests, samples, benchmarks, and package-consumer fixtures are
intentionally outside this local baseline. Failed builds, tests, or coverage collection clean up without uploading an
incomplete analysis.

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

## Next steps

- Follow the [public preview release checklist](release-checklist.md).
- Review the current [release notes](../release-notes/index.md).

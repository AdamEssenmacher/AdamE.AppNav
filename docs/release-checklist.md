# Release Checklist

This checklist is for validating a production release candidate before publishing packages.

## Platform Matrix

- Android: build adapter, build Commerce sample, run `eng/run-maui-platform-tests.sh android` against an attached emulator or device, launch Commerce, and verify catalog-to-product navigation without crashes.
- iOS: build Commerce for simulator, run `eng/run-maui-platform-tests.sh ios`, launch Commerce in a simulator, verify app-link startup, stack navigation, swipe-back, and modal dismissal.
- Mac Catalyst: build adapter, build Commerce, run `eng/run-maui-platform-tests.sh maccatalyst`, and verify startup, restore, stack navigation, and modal behavior.

## Required Commands

```bash
dotnet test tests/AdamE.MauiRouter.Tests/AdamE.MauiRouter.Tests.csproj
dotnet build tests/AdamE.MauiRouter.Maui.Tests/AdamE.MauiRouter.Maui.Tests.csproj -f net10.0-maccatalyst
dotnet build src/AdamE.MauiRouter.Maui/AdamE.MauiRouter.Maui.csproj
dotnet build samples/Commerce.Sample/Commerce.Sample.csproj -f net10.0-maccatalyst
dotnet pack src/AdamE.MauiRouter/AdamE.MauiRouter.csproj
dotnet pack src/AdamE.MauiRouter.Maui/AdamE.MauiRouter.Maui.csproj
dotnet pack src/AdamE.MauiRouter.Testing/AdamE.MauiRouter.Testing.csproj
```

## XHarness

Install XHarness before running platform tests:

```bash
dotnet tool install Microsoft.DotNet.XHarness.CLI --global \
  --add-source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/index.json \
  --version "11.0.0-prerelease*"
```

Run one platform:

```bash
eng/run-maui-platform-tests.sh android
eng/run-maui-platform-tests.sh ios
eng/run-maui-platform-tests.sh maccatalyst
```

Artifacts are written under `artifacts/xharness/`. A valid platform pass requires all of the following:

- XHarness exits successfully.
- A test result XML/TRX artifact is present.
- The result artifact reports zero failed tests.
- Logs contain no unhandled exception or native crash marker.

The runner fails if XHarness launches the app but does not produce test results.

## CI

The release-confidence workflow runs core tests, MAUI adapter build, Commerce Mac Catalyst build, package packing, and Mac Catalyst XHarness tests on every push and pull request. iOS simulator and Android emulator platform jobs are available as manual `workflow_dispatch` gates for runners with stable simulator/emulator support.

## Manual Smoke Checks

- No Shell or Prism dependency appears in source or samples.
- Commerce uses app-link startup, fallback routing, persistence, and shared-element ids.
- App-link cold start prefers the incoming link over restored snapshots.
- Persistence restore rejects corrupt or unsupported snapshots without mutating state/history.
- Logs contain no unhandled exceptions, stale hidden shared-element views, failed native delegates, or missing cleanup diagnostics.

## Known V1 Limits

- No Shell integration.
- No Windows MAUI adapter target.
- No MVU presenter.
- No complete multi-window orchestration.
- No route matcher trie/indexing performance work.
- No custom tab/flyout selection animation.

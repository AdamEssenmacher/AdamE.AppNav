# Release Checklist

This checklist is for validating a production release candidate before publishing packages.

## Platform Matrix

- Android: build adapter, build Commerce sample, run `eng/run-maui-platform-tests.sh android` against an attached emulator or device, launch Commerce, and verify catalog-to-product navigation without crashes.
- iOS: build Commerce for simulator, run `eng/run-maui-platform-tests.sh ios`, launch Commerce in a simulator, verify app-link startup, stack navigation, swipe-back, and modal dismissal.
- Mac Catalyst: build adapter, build Commerce, run `eng/run-maui-platform-tests.sh maccatalyst`, and verify startup, restore, stack navigation, and modal behavior.

## Required Commands

```bash
dotnet test tests/AdamE.AppNav.Tests/AdamE.AppNav.Tests.csproj
dotnet build tests/AdamE.AppNav.Maui.Tests/AdamE.AppNav.Maui.Tests.csproj -f net10.0-maccatalyst
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release -f net9.0 -p:EnableTrimAnalyzer=true -p:EnableAotAnalyzer=true -p:SuppressTrimAnalysisWarnings=false -p:WarningsNotAsErrors=NU1900 -warnaserror
dotnet build samples/Commerce.Sample/Commerce.Sample.csproj -f net10.0-maccatalyst
dotnet publish samples/Commerce.Sample/Commerce.Sample.csproj -c Release -f net10.0-maccatalyst -r maccatalyst-arm64 -p:PublishAot=true -p:EnableCodeSigning=false -p:CodesignKey=- -p:CodesignProvision= -o artifacts/aot/maccatalyst
dotnet publish samples/Commerce.Sample/Commerce.Sample.csproj -c Release -f net10.0-android -r android-arm64 -p:PublishTrimmed=true -p:TrimMode=full -p:AndroidLinkMode=Full -o artifacts/aot/android-arm64
dotnet build benchmarks/AdamE.AppNav.Benchmarks/AdamE.AppNav.Benchmarks.csproj -c Release
dotnet run -c Release --project benchmarks/AdamE.AppNav.Benchmarks -- --check-budgets
dotnet pack src/AdamE.AppNav/AdamE.AppNav.csproj
dotnet pack src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj
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

The release-confidence workflow runs core and generator tests, a warning-as-error AOT analyzer build, Mac Catalyst NativeAOT and Android full-trim sample publishes, package packing, and Mac Catalyst XHarness tests on every push and pull request. iOS simulator and Android emulator platform jobs are available as manual `workflow_dispatch` gates for runners with stable simulator/emulator support.

## Manual Smoke Checks

- No Shell or Prism dependency appears in source or samples.
- Commerce uses app-link startup, fallback routing, deferred request persistence, and shared-element ids.
- App-link cold start prefers the incoming link over fallback startup navigation.
- Deferred request persistence recovers from corrupt or unsupported persisted payloads by clearing the store and continuing startup.
- Optional release-candidate performance review: run `dotnet run -c Release --project benchmarks/AdamE.AppNav.Benchmarks -- --filter "*" --join`.
- Logs contain no unhandled exceptions, stale hidden shared-element views, failed native delegates, or missing cleanup diagnostics.

## Known V1 Limits

- No Shell integration.
- No Windows MAUI adapter target.
- No MVU presenter.
- No complete multi-window orchestration.
- No full trie-based route matcher; the current literal-prefix candidate index has benchmark and allocation-budget coverage across 10, 100, and 1000 routes.
- No custom tab selection animation.

# Release Checklist

This checklist is for validating a production release candidate before publishing packages.

## Platform Matrix

- Android: build adapter, build Commerce sample, run `eng/run-maui-platform-tests.sh android` against an attached emulator or device, launch Commerce, and verify catalog-to-product navigation without crashes.
- iOS: build Commerce for simulator, run `eng/run-maui-platform-tests.sh ios`, launch Commerce in a simulator, verify app-link startup, stack navigation, swipe-back, and modal dismissal.
- Mac Catalyst: build adapter, build Commerce, run `eng/run-maui-platform-tests.sh maccatalyst`, and verify startup, restore, stack navigation, and modal behavior.

## Required Commands

Install MAUI workload packs for both target lines. `eng/sdk/net9/global.json` scopes the first installation to .NET 9;
repository-root build commands use the .NET 10 compiler to produce both .NET 9 and .NET 10 assets.

```bash
cd eng/sdk/net9
dotnet workload install maui
cd ../../..
dotnet workload install maui

dotnet test tests/AdamE.AppNav.Tests/AdamE.AppNav.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -p:CheckEolTargetFramework=false
dotnet test tests/AdamE.AppNav.Generators.Tests/AdamE.AppNav.Generators.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -p:CheckEolTargetFramework=false
dotnet build tests/AdamE.AppNav.Maui.Tests/AdamE.AppNav.Maui.Tests.csproj -f net10.0-maccatalyst

dotnet build src/AdamE.AppNav/AdamE.AppNav.csproj -c Release -f net9.0 -p:CheckEolTargetFramework=false -warnaserror
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release -f net9.0 -p:CheckEolTargetFramework=false -warnaserror
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release -f net9.0-android -p:CheckEolTargetFramework=false -warnaserror
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release -f net9.0-ios -p:CheckEolTargetFramework=false -warnaserror
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release -f net9.0-maccatalyst -p:CheckEolTargetFramework=false -warnaserror
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release -f net9.0 -p:CheckEolTargetFramework=false -p:EnableTrimAnalyzer=true -p:EnableAotAnalyzer=true -p:SuppressTrimAnalysisWarnings=false -p:WarningsNotAsErrors=NU1900 -warnaserror
dotnet build src/AdamE.AppNav/AdamE.AppNav.csproj -c Release -f net10.0 -warnaserror
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release -f net10.0 -warnaserror
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release -f net10.0-android -warnaserror
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release -f net10.0-ios -warnaserror
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release -f net10.0-maccatalyst -warnaserror
dotnet build src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release -f net10.0 -p:EnableTrimAnalyzer=true -p:EnableAotAnalyzer=true -p:SuppressTrimAnalysisWarnings=false -p:WarningsNotAsErrors=NU1900 -warnaserror

dotnet build samples/Commerce.Sample/Commerce.Sample.csproj -c Release -f net10.0-maccatalyst -warnaserror
dotnet publish samples/Commerce.Sample/Commerce.Sample.csproj -c Release -f net10.0-maccatalyst -r maccatalyst-arm64 -p:PublishAot=true -p:EnableCodeSigning=false -p:CodesignKey=- -p:CodesignProvision= -o artifacts/aot/maccatalyst
dotnet publish samples/Commerce.Sample/Commerce.Sample.csproj -c Release -f net10.0-android -r android-arm64 -p:PublishTrimmed=true -p:TrimMode=full -p:AndroidLinkMode=Full -o artifacts/aot/android-arm64
dotnet build benchmarks/AdamE.AppNav.Benchmarks/AdamE.AppNav.Benchmarks.csproj -c Release
dotnet run -c Release --project benchmarks/AdamE.AppNav.Benchmarks -- --check-budgets
dotnet pack src/AdamE.AppNav/AdamE.AppNav.csproj -c Release --no-build -p:CheckEolTargetFramework=false -o artifacts/packages
dotnet pack src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj -c Release --no-build -p:CheckEolTargetFramework=false -o artifacts/packages
eng/verify-package-assets.sh artifacts/packages
```

.NET 9 remains supported by policy. `CheckEolTargetFramework=false` suppresses only the SDK's platform-EOL warning for
that target; compiler, trim, AOT, and package checks remain warning-as-error gates.

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

The release-confidence workflow exposes independent required jobs for unit/API/allocation contracts, supported-target
and analyzer/package checks, Mac Catalyst NativeAOT, Android full trimming, and Mac Catalyst XHarness tests. A failure
in one gate does not suppress the others. iOS simulator and Android emulator platform jobs remain manual
`workflow_dispatch` gates for runners with stable simulator/emulator support.

## Manual Smoke Checks

- No Shell or Prism dependency appears in source or samples.
- Commerce uses app-link startup, fallback routing, deferred request persistence, and pre-match URL transformation.
- App-link cold start prefers the incoming link over fallback startup navigation.
- Schema 1/future deferred data is cleared; malformed schema-2 data is quarantined byte-for-byte and never partially replayed.
- Safe diagnostics expose no URI credentials, query/fragment values, provenance values, or exception messages.
- Async DI shutdown waits for presenter rollback, page release, lifecycle hooks, and async scope disposal.
- Optional release-candidate performance review: run `dotnet run -c Release --project benchmarks/AdamE.AppNav.Benchmarks -- --filter "*" --join`.
- Logs contain no unhandled exceptions, failed native operations, consistency faults, or missing cleanup diagnostics.

## Known V1 Limits

- No Shell integration.
- No Windows MAUI adapter target.
- No MVU presenter.
- No complete multi-window orchestration.
- No full trie-based route matcher; the current literal-prefix candidate index has benchmark and allocation-budget coverage across 10, 100, and 1000 routes.
- No custom tab selection animation.

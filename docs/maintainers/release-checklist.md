# Public preview release checklist

[Documentation home](../README.md)

This checklist is for AppNav repository maintainers, not application
integration.

This checklist validates a `0.1.0-preview.*` candidate. Stable qualification and NuGet.org publication are separate
work. The supported baseline is .NET 10, Android API 28+, iOS 15+, and Mac Catalyst 15+.

## Pinned toolchain

The repository root `global.json` pins SDK `10.0.400`, workload set `10.0.302.1`, and `latestFeature` roll-forward.
Android builds require JDK 21; CI pins Microsoft's JDK 21 distribution.
Run commands from the repository root so the pin is honored. Install the matching workload set once:

```bash
dotnet workload install maui
```

## Deterministic non-device gate

Run the canonical gate twice from a clean working tree:

```bash
eng/verify.sh release
eng/verify.sh release
```

The gate runs unit and API contracts, route and MAUI generator tests when present, adapter-contract tests when present,
allocation budgets, all supported target builds, trim/AOT analyzers, Android arm64 full trimming, iOS arm64 linked
publishing, Mac Catalyst arm64 NativeAOT publishing, package inspection, and an isolated package-consumer build.

The consumer fixture references only the locally packed `AdamE.AppNav.Maui` package. It restores into an empty temporary
global-packages and HTTP-cache directory, then compiles an attributed route, a generated route module, an attributed
MAUI page, and a generated MAUI page module. A project reference or a warm global-package cache cannot mask missing
package assets.

Useful focused gates are:

```bash
eng/verify.sh contracts
eng/verify.sh packages
eng/verify.sh native
```

Local packages default to `0.1.0-preview.local`. Set `APPNAV_RELEASE_TAG=v0.1.0-preview.1` to validate a tagged preview
version. Stable package versions are rejected unless `AppNavStableRelease=true` is explicitly supplied; the public
preview workflow does not set it.

## Runtime platform gate

The runtime project pins DeviceRunners and its `dotnet test` integration, so no global test tool is required. Run:

```bash
eng/run-maui-platform-tests.sh android
eng/run-maui-platform-tests.sh ios
eng/run-maui-platform-tests.sh maccatalyst
```

Artifacts are written under `artifacts/device-runners/`. A pass requires a TRX result, at least one executed test, no
failures or unexpected skips, no unhandled exception/native crash/consistency-fault markers, and a successful
DeviceRunners exit.

Pull requests compile all supported targets and run Mac Catalyst tests in Release. Nightly CI adds current Android and
iOS simulator runs. Weekly CI also runs Android API 28. If simulator or emulator infrastructure is unavailable, stop
and record the required local command; do not waive the gate.

## Package and publication checks

The API and schema changes for this candidate are recorded in
[`docs/release-notes/0.1.0-preview.1.md`](../release-notes/0.1.0-preview.1.md).

- Exactly one `.nupkg` and `.snupkg` exist for both `AdamE.AppNav` and `AdamE.AppNav.Maui`.
- Packages contain only the documented .NET 10 target assets and their assigned source generators.
- Package metadata points to `AdamEssenmacher/AdamE.AppNav`.
- Public API and schema baselines match the release notes.
- The exact green `main` commit is tagged `v0.1.0-preview.1`.
- The tag workflow reuses already-validated packages, emits SHA-256 hashes, and creates a GitHub prerelease.
- No workflow pushes to NuGet.org for this preview.

## Known preview limits

- No Shell or Prism integration.
- No Windows MAUI adapter target.
- No production Blazor adapter.
- No full MAUI multi-window orchestration.
- No transition system.
- Android predictive back is not implemented.

## Next steps

- Run the complete [testing matrix](testing.md).
- Validate the [0.1.0-preview.1 release notes](../release-notes/0.1.0-preview.1.md).

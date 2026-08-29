#!/usr/bin/env bash

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"
ARTIFACT_ROOT="${ARTIFACT_ROOT:-$ROOT/artifacts}"
PACKAGE_DIRECTORY="$ARTIFACT_ROOT/packages"
CONTRACT_RESULTS_DIRECTORY="$ARTIFACT_ROOT/test-results/contracts"

usage() {
  cat <<'USAGE'
Usage: eng/verify.sh [contracts|packages|native|native-android|native-ios|native-maccatalyst|release]

  contracts            Unit, API, generator, adapter-contract, and allocation gates.
  packages             Supported target/analyzer builds, packing, package inspection,
                       and an isolated MAUI package-consumer build.
  native               Android full-trim, iOS linked, and Mac Catalyst NativeAOT publishes.
  native-android       Android arm64 full-trim publish only.
  native-ios           iOS arm64 linked publish only.
  native-maccatalyst   Mac Catalyst arm64 NativeAOT publish only.
  release              The complete deterministic, non-device public-preview gate.

Environment:
  CONFIGURATION          Build configuration. Default: Release.
  ARTIFACT_ROOT          Output root. Default: artifacts.
  APPNAV_RELEASE_TAG     Optional validated v-prefixed tag; overrides package version.
  APPNAV_PACKAGE_VERSION Optional non-tag package version. Default: 0.1.0-preview.local.
USAGE
}

read_trx_counter() {
  local counters="$1"
  local name="$2"
  local pattern="(^|[[:space:]])${name}=\"([0-9]+)\""

  if [[ "$counters" =~ $pattern ]]; then
    printf '%s\n' "${BASH_REMATCH[2]}"
    return 0
  fi

  return 1
}

verify_all_pass_trx() {
  local result="$1"
  local label="$2"
  # VSTest reports a successful dotnet-test run as Completed, while DeviceRunners
  # reports Passed. The counters below are the authoritative all-pass contract.
  if ! grep -E '<ResultSummary outcome="(Passed|Completed)"' "$result" >/dev/null 2>&1; then
    echo "$label TRX ResultSummary is neither Passed nor Completed: $result" >&2
    exit 1
  fi

  local counters
  counters="$(sed -n '/<Counters /{s/.*\(<Counters [^>]*>\).*/\1/;p;q;}' "$result")"
  if [[ -z "$counters" ]]; then
    echo "$label did not emit a readable TRX Counters record: $result" >&2
    exit 1
  fi

  local total
  local executed
  local passed
  total="$(read_trx_counter "$counters" total)" || {
    echo "$label TRX is missing the total counter: $result" >&2
    exit 1
  }
  executed="$(read_trx_counter "$counters" executed)" || {
    echo "$label TRX is missing the executed counter: $result" >&2
    exit 1
  }
  passed="$(read_trx_counter "$counters" passed)" || {
    echo "$label TRX is missing the passed counter: $result" >&2
    exit 1
  }

  if (( total <= 0 || executed <= 0 || passed <= 0 )); then
    echo "$label TRX must prove a non-zero test run (total=$total executed=$executed passed=$passed): $result" >&2
    exit 1
  fi
  if (( total != executed || executed != passed )); then
    echo "$label TRX is not all-pass (total=$total executed=$executed passed=$passed): $result" >&2
    exit 1
  fi

  local counter
  local value
  local non_pass_counters=(
    failed error timeout aborted inconclusive passedButRunAborted notRunnable
    notExecuted disconnected warning completed inProgress pending
  )
  for counter in "${non_pass_counters[@]}"; do
    value="$(read_trx_counter "$counters" "$counter")" || {
      echo "$label TRX is missing the $counter counter: $result" >&2
      exit 1
    }
    if (( value != 0 )); then
      echo "$label TRX reports $counter=$value; every non-pass counter must be zero: $result" >&2
      exit 1
    fi
  done
}

dotnet_test() {
  local project="$1"
  local result_name="$2"
  local result_directory="$CONTRACT_RESULTS_DIRECTORY/$result_name"
  mkdir -p "$result_directory"
  find "$result_directory" -mindepth 1 -delete

  dotnet test "$project" \
    -c "$CONFIGURATION" \
    -p:TreatWarningsAsErrors=true \
    -p:CheckEolTargetFramework=false \
    --results-directory "$result_directory" \
    --logger "trx;LogFileName=test-results.trx"

  local results=()
  local result
  while IFS= read -r -d '' result; do
    results+=("$result")
  done < <(find "$result_directory" -type f -name '*.trx' -print0)
  if [[ "${#results[@]}" -ne 1 ]]; then
    echo "$result_name must emit exactly one TRX result; found ${#results[@]} under $result_directory." >&2
    exit 1
  fi

  verify_all_pass_trx "${results[0]}" "$result_name"
}

require_jdk_21() {
  local java_command="java"
  if [[ -n "${JAVA_HOME:-}" && -x "$JAVA_HOME/bin/java" ]]; then
    java_command="$JAVA_HOME/bin/java"
  elif ! command -v java >/dev/null 2>&1; then
    echo "Android verification requires JDK 21, but java was not found on PATH." >&2
    exit 1
  fi

  local java_version
  java_version="$("$java_command" -version 2>&1 | head -n 1)"
  if [[ ! "$java_version" =~ \"21([.\"]|$) ]]; then
    echo "Android verification requires JDK 21; active runtime: $java_version" >&2
    echo "Set JAVA_HOME to a JDK 21 installation and retry." >&2
    exit 1
  fi
}

run_contracts() {
  dotnet_test "$ROOT/tests/AdamE.AppNav.Tests/AdamE.AppNav.Tests.csproj" core
  dotnet_test "$ROOT/tests/AdamE.AppNav.Generators.Tests/AdamE.AppNav.Generators.Tests.csproj" route-generator
  dotnet_test "$ROOT/tests/AdamE.AppNav.Maui.Generators.Tests/AdamE.AppNav.Maui.Generators.Tests.csproj" maui-generator
  dotnet_test "$ROOT/tests/AdamE.AppNav.AdapterContract.Tests/AdamE.AppNav.AdapterContract.Tests.csproj" adapter-contract
  dotnet run \
    -c "$CONFIGURATION" \
    --project "$ROOT/benchmarks/AdamE.AppNav.Benchmarks/AdamE.AppNav.Benchmarks.csproj" \
    -p:TreatWarningsAsErrors=true \
    -- \
    --check-budgets
}

build_supported_targets() {
  local package_properties=("$@")

  require_jdk_21
  dotnet build "$ROOT/src/AdamE.AppNav/AdamE.AppNav.csproj" \
    -c "$CONFIGURATION" \
    -f net10.0 \
    "${package_properties[@]}" \
    -warnaserror

  local framework
  for framework in net10.0 net10.0-android net10.0-ios net10.0-maccatalyst; do
    dotnet build "$ROOT/src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj" \
      -c "$CONFIGURATION" \
      -f "$framework" \
      "${package_properties[@]}" \
      -warnaserror
  done

  dotnet build "$ROOT/src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj" \
    -c "$CONFIGURATION" \
    -f net10.0 \
    -p:EnableTrimAnalyzer=true \
    -p:EnableAotAnalyzer=true \
    -p:SuppressTrimAnalysisWarnings=false \
    "${package_properties[@]}" \
    -warnaserror

  for framework in net10.0-android net10.0-ios net10.0-maccatalyst; do
    dotnet build "$ROOT/samples/GettingStarted.Sample/GettingStarted.Sample.csproj" \
      -c "$CONFIGURATION" \
      -f "$framework" \
      -t:Compile \
      -p:CheckEolTargetFramework=false \
      "${package_properties[@]}" \
      -warnaserror
  done
}

resolve_package_version() {
  local release_tag="${APPNAV_RELEASE_TAG:-}"
  if [[ -n "$release_tag" ]]; then
    if [[ ! "$release_tag" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]; then
      echo "APPNAV_RELEASE_TAG must be a validated v-prefixed semantic version tag." >&2
      exit 2
    fi

    printf '%s\n' "${release_tag#v}"
    return
  fi

  printf '%s\n' "${APPNAV_PACKAGE_VERSION:-0.1.0-preview.local}"
}

verify_stable_package_guard() {
  local guard_root="$ARTIFACT_ROOT/stable-package-guard"
  mkdir -p "$guard_root"
  find "$guard_root" -mindepth 1 -maxdepth 1 -delete

  local stable_version
  local guard_index=0
  for stable_version in 1.0.0 1.0.0+build-1; do
    guard_index=$((guard_index + 1))
    local guard_log="$guard_root/output-$guard_index.log"
    if dotnet pack "$ROOT/src/AdamE.AppNav/AdamE.AppNav.csproj" \
      -c "$CONFIGURATION" \
      --no-build \
      -p:PackageVersion="$stable_version" \
      -p:AppNavStableRelease=false \
      -o "$guard_root" >"$guard_log" 2>&1; then
      echo "Default packing unexpectedly accepted stable package identity $stable_version without AppNavStableRelease=true." >&2
      exit 1
    fi

    if ! grep -q 'Stable AppNav packages require AppNavStableRelease=true' "$guard_log"; then
      echo "Stable-package validation for $stable_version failed for an unexpected reason." >&2
      cat "$guard_log" >&2
      exit 1
    fi
  done
}

pack_and_verify() {
  local package_version="$1"
  shift
  local package_properties=("$@")

  verify_stable_package_guard
  mkdir -p "$PACKAGE_DIRECTORY"
  find "$PACKAGE_DIRECTORY" -mindepth 1 -maxdepth 1 -type f \
    \( -name 'AdamE.AppNav*.nupkg' -o -name 'AdamE.AppNav*.snupkg' \) -delete

  dotnet pack "$ROOT/src/AdamE.AppNav/AdamE.AppNav.csproj" \
    -c "$CONFIGURATION" \
    --no-build \
    -p:CheckEolTargetFramework=false \
    "${package_properties[@]}" \
    -warnaserror \
    -o "$PACKAGE_DIRECTORY"
  dotnet pack "$ROOT/src/AdamE.AppNav.Maui/AdamE.AppNav.Maui.csproj" \
    -c "$CONFIGURATION" \
    --no-build \
    "${package_properties[@]}" \
    -warnaserror \
    -o "$PACKAGE_DIRECTORY"

  "$ROOT/eng/verify-package-assets.sh" "$PACKAGE_DIRECTORY" "$package_version"
  "$ROOT/eng/verify-package-consumer.sh" "$PACKAGE_DIRECTORY"
}

run_packages() {
  local package_version
  package_version="$(resolve_package_version)"
  local release_tag="${APPNAV_RELEASE_TAG:-}"
  local package_properties=(
    "-p:Version=$package_version"
    "-p:PackageVersion=$package_version"
  )
  if [[ -n "$release_tag" ]]; then
    package_properties+=("-p:AppNavReleaseTag=$release_tag")
  fi

  build_supported_targets "${package_properties[@]}"
  pack_and_verify "$package_version" "${package_properties[@]}"
}

prepare_output() {
  local output="$1"
  mkdir -p "$output"
  find "$output" -mindepth 1 -maxdepth 1 -delete
}

publish_android() {
  require_jdk_21
  local output="$ARTIFACT_ROOT/native/android-arm64"
  prepare_output "$output"
  dotnet publish "$ROOT/samples/Commerce.Sample/Commerce.Sample.csproj" \
    -c "$CONFIGURATION" \
    -f net10.0-android \
    -r android-arm64 \
    -p:PublishTrimmed=true \
    -p:TrimMode=full \
    -p:AndroidLinkMode=Full \
    -warnaserror \
    -o "$output"
}

publish_ios() {
  local output="$ARTIFACT_ROOT/native/ios-arm64"
  prepare_output "$output"
  dotnet publish "$ROOT/samples/Commerce.Sample/Commerce.Sample.csproj" \
    -c "$CONFIGURATION" \
    -f net10.0-ios \
    -r ios-arm64 \
    -p:PublishTrimmed=true \
    -p:MtouchLink=SdkOnly \
    -p:EnableCodeSigning=false \
    -p:CodesignKey=- \
    -p:CodesignProvision= \
    -warnaserror \
    -o "$output"
}

publish_maccatalyst() {
  local output="$ARTIFACT_ROOT/native/maccatalyst-arm64"
  prepare_output "$output"
  dotnet publish "$ROOT/samples/Commerce.Sample/Commerce.Sample.csproj" \
    -c "$CONFIGURATION" \
    -f net10.0-maccatalyst \
    -r maccatalyst-arm64 \
    -p:PublishAot=true \
    -p:EnableCodeSigning=false \
    -p:CodesignKey=- \
    -p:CodesignProvision= \
    -warnaserror \
    -warnnotaserror:IL2104,IL3053 \
    -o "$output"
}

run_native() {
  publish_android
  publish_ios
  publish_maccatalyst
}

mode="${1:-release}"
case "$mode" in
  contracts)
    run_contracts
    ;;
  packages)
    run_packages
    ;;
  native)
    run_native
    ;;
  native-android)
    publish_android
    ;;
  native-ios)
    publish_ios
    ;;
  native-maccatalyst)
    publish_maccatalyst
    ;;
  release)
    run_contracts
    run_packages
    run_native
    ;;
  --help|-h)
    usage
    ;;
  *)
    usage >&2
    exit 2
    ;;
esac

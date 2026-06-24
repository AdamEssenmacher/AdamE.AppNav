#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/tests/AdamE.MauiRouter.Maui.Tests/AdamE.MauiRouter.Maui.Tests.csproj"
CONFIGURATION="${CONFIGURATION:-Debug}"
OUTPUT_ROOT="${OUTPUT_ROOT:-$ROOT/artifacts/xharness}"
PACKAGE_NAME="${PACKAGE_NAME:-com.adame.mauirouter.maui.tests}"
APPLE_RESULT_FILE_NAME="AdamE.MauiRouter.Maui.Tests.xml"
APPLE_RUNNER_LOG_FILE_NAME="AdamE.MauiRouter.Maui.Tests.runner.log"
APPLE_RESULT_XML_BASE64_PREFIX="MAUI_ROUTER_XHARNESS_RESULT_XML_BASE64:"

usage() {
  cat <<'USAGE'
Usage: eng/run-maui-platform-tests.sh [android|ios|maccatalyst|all]

Runs the MAUI adapter platform test app through XHarness.

Environment overrides:
  CONFIGURATION          Build configuration. Default: Debug.
  OUTPUT_ROOT            XHarness artifact root. Default: artifacts/xharness.
  XHARNESS_TARGET        Override the XHarness Apple target.
  IOS_RUNTIME_IDENTIFIER Override iOS simulator RID. Default: iossimulator-arm64.
  MACCATALYST_RUNTIME_IDENTIFIER Override Mac Catalyst RID. Default: maccatalyst-arm64.
  PACKAGE_NAME           Android package name. Default: com.adame.mauirouter.maui.tests.

Prerequisite:
  Install XHarness and ensure `xharness` is on PATH:
    dotnet tool install Microsoft.DotNet.XHarness.CLI --global \
      --add-source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/index.json \
      --version "11.0.0-prerelease*"
USAGE
}

require_xharness() {
  if ! command -v xharness >/dev/null 2>&1; then
    echo "xharness was not found on PATH." >&2
    echo "Install it with the command shown in --help output." >&2
    exit 127
  fi
}

build_project() {
  local framework="$1"
  shift
  dotnet build "$PROJECT" -c "$CONFIGURATION" -f "$framework" "$@"
}

require_test_results() {
  local output="$1"
  local platform="$2"
  local result
  result="$(find "$output" -type f \( -name '*.xml' -o -name '*.trx' \) -print | head -n 1)"
  if [[ -z "$result" ]]; then
    echo "XHarness $platform run completed without a test result XML/TRX artifact." >&2
    echo "Logs are available under: $output" >&2
    exit 1
  fi

  if grep -E 'failed="[1-9][0-9]*"|failures="[1-9][0-9]*"' "$result" >/dev/null 2>&1; then
    echo "XHarness $platform test result contains failures: $result" >&2
    exit 1
  fi
}

fail_on_unhandled_exceptions() {
  local output="$1"
  if find "$output" -type f -name '*.log' -print0 |
    xargs -0 grep -E -i 'Unhandled exception|Native crash|SIGABRT|SIGSEGV' >/dev/null 2>&1; then
    echo "XHarness logs contain an unhandled exception or native crash marker." >&2
    echo "Logs are available under: $output" >&2
    exit 1
  fi
}

decode_base64_to_file() {
  local value="$1"
  local destination="$2"
  if command -v base64 >/dev/null 2>&1 && printf '%s' "$value" | base64 --decode >"$destination" 2>/dev/null; then
    return 0
  fi

  if command -v base64 >/dev/null 2>&1 && printf '%s' "$value" | base64 -D >"$destination" 2>/dev/null; then
    return 0
  fi

  python3 -c 'import base64, pathlib, sys; pathlib.Path(sys.argv[2]).write_bytes(base64.b64decode(sys.argv[1]))' "$value" "$destination"
}

extract_apple_result_xml_from_logs() {
  local output="$1"
  local encoded
  encoded="$(
    find "$output" -type f -name '*.log' -print0 |
      xargs -0 grep -h "$APPLE_RESULT_XML_BASE64_PREFIX" 2>/dev/null |
      tail -n 1 |
      sed "s/.*$APPLE_RESULT_XML_BASE64_PREFIX//"
  )"

  if [[ -z "$encoded" ]]; then
    return 0
  fi

  decode_base64_to_file "$encoded" "$output/$APPLE_RESULT_FILE_NAME"
}

run_android() {
  local output="$OUTPUT_ROOT/android"
  rm -rf "$output"
  mkdir -p "$output"

  build_project net10.0-android -p:EmbedAssembliesIntoApk=true
  local apk
  apk="$(find "$ROOT/tests/AdamE.MauiRouter.Maui.Tests/bin/$CONFIGURATION/net10.0-android" -name '*-Signed.apk' -print | head -n 1)"
  if [[ -z "$apk" ]]; then
    echo "No signed Android test APK was found." >&2
    exit 1
  fi

  local xharness_exit=0
  xharness android test \
    --output-directory="$output" \
    --package-name="$PACKAGE_NAME" \
    --instrumentation="$PACKAGE_NAME.AndroidMauiTestInstrumentation" \
    --app="$apk" || xharness_exit=$?
  require_test_results "$output" "android"
  fail_on_unhandled_exceptions "$output"
  if [[ "$xharness_exit" -ne 0 ]]; then
    exit "$xharness_exit"
  fi
}

run_ios() {
  local rid="${IOS_RUNTIME_IDENTIFIER:-iossimulator-arm64}"
  local target="${XHARNESS_TARGET:-ios-simulator-64}"
  local output="$OUTPUT_ROOT/ios"
  rm -rf "$output"
  mkdir -p "$output"

  build_project net10.0-ios -p:RuntimeIdentifier="$rid" -p:EmbedAssembliesIntoAppBundle=true -p:CodesignKey="" -p:CodesignProvision=""
  local app
  app="$(find "$ROOT/tests/AdamE.MauiRouter.Maui.Tests/bin/$CONFIGURATION/net10.0-ios/$rid" -maxdepth 1 -name '*.app' -print | head -n 1)"
  if [[ -z "$app" ]]; then
    echo "No iOS simulator test app was found." >&2
    exit 1
  fi

  local xharness_exit=0
  xharness apple test \
    --app="$app" \
    --output-directory="$output" \
    --target="$target" || xharness_exit=$?
  extract_apple_result_xml_from_logs "$output"
  require_test_results "$output" "ios"
  fail_on_unhandled_exceptions "$output"
  if [[ "$xharness_exit" -ne 0 ]]; then
    exit "$xharness_exit"
  fi
}

run_maccatalyst() {
  local rid="${MACCATALYST_RUNTIME_IDENTIFIER:-maccatalyst-arm64}"
  local target="${XHARNESS_TARGET:-maccatalyst}"
  local output="$OUTPUT_ROOT/maccatalyst"
  rm -rf "$output"
  mkdir -p "$output"
  local container="$HOME/Library/Containers/$PACKAGE_NAME/Data"
  local fallback_result="$HOME/Library/$APPLE_RESULT_FILE_NAME"
  local fallback_runner_log="$HOME/Library/$APPLE_RUNNER_LOG_FILE_NAME"
  rm -f "$fallback_result" "$fallback_runner_log"
  if [[ -d "$container" ]]; then
    find "$container" -name "$APPLE_RESULT_FILE_NAME" -delete
    find "$container" -name "$APPLE_RUNNER_LOG_FILE_NAME" -delete
  fi

  build_project net10.0-maccatalyst -p:RuntimeIdentifier="$rid" -p:EmbedAssembliesIntoAppBundle=true -p:CodesignKey="" -p:CodesignProvision=""
  local app
  app="$(find "$ROOT/tests/AdamE.MauiRouter.Maui.Tests/bin/$CONFIGURATION/net10.0-maccatalyst/$rid" -maxdepth 1 -name '*.app' -print | head -n 1)"
  if [[ -z "$app" ]]; then
    echo "No Mac Catalyst test app was found." >&2
    exit 1
  fi

  local xharness_exit=0
  xharness apple test \
    --app="$app" \
    --output-directory="$output" \
    --target="$target" || xharness_exit=$?
  local result_path=""
  local runner_log_path=""
  if [[ -d "$container" ]]; then
    result_path="$(find "$container" -name "$APPLE_RESULT_FILE_NAME" -print | head -n 1)"
    runner_log_path="$(find "$container" -name "$APPLE_RUNNER_LOG_FILE_NAME" -print | head -n 1)"
  fi
  if [[ -z "$result_path" && -f "$fallback_result" ]]; then
    result_path="$fallback_result"
  fi
  if [[ -z "$runner_log_path" && -f "$fallback_runner_log" ]]; then
    runner_log_path="$fallback_runner_log"
  fi
  if [[ -n "$result_path" && -f "$result_path" ]]; then
    cp "$result_path" "$output/$APPLE_RESULT_FILE_NAME"
  fi
  if [[ -n "$runner_log_path" && -f "$runner_log_path" ]]; then
    cp "$runner_log_path" "$output/$APPLE_RUNNER_LOG_FILE_NAME"
  fi
  require_test_results "$output" "maccatalyst"
  fail_on_unhandled_exceptions "$output"
  if [[ "$xharness_exit" -ne 0 ]]; then
    exit "$xharness_exit"
  fi
}

platform="${1:-maccatalyst}"
if [[ "$platform" == "--help" || "$platform" == "-h" ]]; then
  usage
  exit 0
fi

require_xharness

case "$platform" in
  android)
    run_android
    ;;
  ios)
    run_ios
    ;;
  maccatalyst)
    run_maccatalyst
    ;;
  all)
    run_android
    run_ios
    run_maccatalyst
    ;;
  *)
    usage >&2
    exit 2
    ;;
esac

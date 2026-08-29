#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/tests/AdamE.AppNav.Maui.Tests/AdamE.AppNav.Maui.Tests.csproj"
CONFIGURATION="${CONFIGURATION:-Release}"
OUTPUT_ROOT="${OUTPUT_ROOT:-$ROOT/artifacts/device-runners}"
CONNECTION_TIMEOUT="${DEVICE_RUNNERS_CONNECTION_TIMEOUT:-180}"
DATA_TIMEOUT="${DEVICE_RUNNERS_DATA_TIMEOUT:-60}"
LAST_TRX_TOTAL=0

usage() {
  cat <<'USAGE'
Usage: eng/run-maui-platform-tests.sh [android|ios|maccatalyst|all]

Builds, deploys, and runs the MAUI adapter platform tests through the
DeviceRunners.Testing.Targets `dotnet test` integration.

Environment overrides:
  CONFIGURATION                    Build configuration. Default: Release.
  OUTPUT_ROOT                      Artifact root. Default: artifacts/device-runners.
  DEVICE_RUNNERS_DEVICE            Device/emulator identifier for every platform.
  ANDROID_DEVICE                   Android emulator/device identifier.
  IOS_DEVICE                       iOS simulator UDID.
  IOS_RUNTIME_IDENTIFIER           iOS simulator RID. Default: iossimulator-arm64.
  MACCATALYST_RUNTIME_IDENTIFIER   Mac Catalyst RID. Default: maccatalyst-arm64.
  DEVICE_RUNNERS_CONNECTION_TIMEOUT Seconds to wait for the app. Default: 180.
  DEVICE_RUNNERS_DATA_TIMEOUT      Seconds without test data before failure. Default: 60.
  TEST_FILTER                      Optional standard `dotnet test --filter` expression.

No global test tool is required. DeviceRunners.Testing.Targets is pinned in the
test project and supplies the build, deployment, result collector, and CLI tools.
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
  local platform="$2"
  if ! grep -E '<ResultSummary outcome="Passed"' "$result" >/dev/null 2>&1; then
    echo "DeviceRunners $platform TRX ResultSummary is not Passed: $result" >&2
    exit 1
  fi

  local counters
  counters="$(sed -n '/<Counters /{s/.*\(<Counters [^>]*>\).*/\1/;p;q;}' "$result")"
  if [[ -z "$counters" ]]; then
    echo "DeviceRunners $platform result has no readable TRX Counters record: $result" >&2
    exit 1
  fi

  local total
  local executed
  local passed
  total="$(read_trx_counter "$counters" total)" || {
    echo "DeviceRunners $platform TRX is missing the total counter: $result" >&2
    exit 1
  }
  executed="$(read_trx_counter "$counters" executed)" || {
    echo "DeviceRunners $platform TRX is missing the executed counter: $result" >&2
    exit 1
  }
  passed="$(read_trx_counter "$counters" passed)" || {
    echo "DeviceRunners $platform TRX is missing the passed counter: $result" >&2
    exit 1
  }

  if (( total <= 0 || executed <= 0 || passed <= 0 )); then
    echo "DeviceRunners $platform must prove a non-zero run (total=$total executed=$executed passed=$passed): $result" >&2
    exit 1
  fi
  if (( total != executed || executed != passed )); then
    echo "DeviceRunners $platform is not all-pass (total=$total executed=$executed passed=$passed): $result" >&2
    exit 1
  fi
  LAST_TRX_TOTAL="$total"

  local counter
  local value
  local non_pass_counters=(
    failed error timeout aborted inconclusive passedButRunAborted notRunnable
    notExecuted disconnected warning completed inProgress pending
  )
  for counter in "${non_pass_counters[@]}"; do
    value="$(read_trx_counter "$counters" "$counter")" || {
      echo "DeviceRunners $platform TRX is missing the $counter counter: $result" >&2
      exit 1
    }
    if (( value != 0 )); then
      echo "DeviceRunners $platform reports $counter=$value; every non-pass counter must be zero: $result" >&2
      exit 1
    fi
  done
}

require_test_results() {
  local output="$1"
  local platform="$2"
  local results=()
  local result
  while IFS= read -r -d '' result; do
    results+=("$result")
  done < <(find "$output" -type f -name '*.trx' -print0)

  if [[ "${#results[@]}" -ne 1 ]]; then
    echo "DeviceRunners $platform must emit exactly one TRX result; found ${#results[@]}." >&2
    echo "Logs are available under: $output" >&2
    exit 1
  fi

  verify_all_pass_trx "${results[0]}" "$platform"
}

verify_event_stream() {
  local output="$1"
  local platform="$2"
  local event_files=()
  local event_file
  while IFS= read -r -d '' event_file; do
    event_files+=("$event_file")
  done < <(find "$output" -type f -name '*.jsonl' -print0)

  if [[ "${#event_files[@]}" -ne 1 ]]; then
    echo "DeviceRunners $platform must emit exactly one JSONL event stream; found ${#event_files[@]}." >&2
    exit 1
  fi

  event_file="${event_files[0]}"
  local begin_count
  local end_count
  local result_count
  local passed_count
  begin_count="$(grep -Ec '"type":"begin"' "$event_file" || true)"
  end_count="$(grep -Ec '"type":"end"' "$event_file" || true)"
  result_count="$(grep -Ec '"type":"result"' "$event_file" || true)"
  passed_count="$(grep -Ec '"type":"result".*"status":"Passed"' "$event_file" || true)"

  if (( begin_count != 1 || end_count != 1 )); then
    echo "DeviceRunners $platform JSONL stream is incomplete (begin=$begin_count end=$end_count): $event_file" >&2
    exit 1
  fi
  if (( result_count <= 0 || result_count != passed_count || result_count != LAST_TRX_TOTAL )); then
    echo "DeviceRunners $platform JSONL/TRX results disagree (events=$result_count passed-events=$passed_count trx-total=$LAST_TRX_TOTAL): $event_file" >&2
    exit 1
  fi
}

fail_on_runtime_markers() {
  local output="$1"
  local files=()
  local file

  while IFS= read -r -d '' file; do
    files+=("$file")
  done < <(find "$output" -type f \( -name '*.log' -o -name '*.txt' -o -name '*.jsonl' \) -print0)

  if [[ "${#files[@]}" -gt 0 ]] &&
    grep -E -i 'Unhandled exception|Native crash|app(lication)? appears to have crashed|incomplete: app crashed|SIGABRT|SIGSEGV|crash marker|consistency fault|retained scopes?[^0-9]*[1-9][0-9]*|"status":"(Failed|Error|TimedOut|Timeout|Aborted|Inconclusive|NotRunnable|NotExecuted|Skipped|Disconnected|Warning)"|"type":"(crash|error|abort|disconnect)"' "${files[@]}" >/dev/null 2>&1; then
    echo "DeviceRunners logs contain an exception, crash, consistency-fault, or retained-scope marker." >&2
    echo "Logs are available under: $output" >&2
    exit 1
  fi
}

run_platform() {
  local platform="$1"
  local framework="$2"
  local rid="$3"
  local device="$4"
  local output="$OUTPUT_ROOT/$platform"
  local test_exit=0
  local args=(
    dotnet test "$PROJECT"
    --configuration "$CONFIGURATION"
    --framework "$framework"
    --results-directory "$output"
    --logger "trx;LogFileName=test-results.trx"
    "-p:TreatWarningsAsErrors=true"
    "-p:DeviceRunnersConnectionTimeout=$CONNECTION_TIMEOUT"
    "-p:DeviceRunnersDataTimeout=$DATA_TIMEOUT"
  )

  if [[ -n "$rid" ]]; then
    args+=("-p:RuntimeIdentifier=$rid")
  fi

  if [[ -n "$device" ]]; then
    args+=("-p:DeviceRunnersDevice=$device")
  fi

  if [[ -n "${TEST_FILTER:-}" ]]; then
    args+=(--filter "$TEST_FILTER")
  fi

  rm -rf "$output"
  mkdir -p "$output"

  "${args[@]}" 2>&1 | tee "$output/dotnet-test.log" || test_exit=$?

  require_test_results "$output" "$platform"
  verify_event_stream "$output" "$platform"
  fail_on_runtime_markers "$output"

  if [[ "$test_exit" -ne 0 ]]; then
    echo "DeviceRunners $platform run failed with exit code $test_exit." >&2
    exit "$test_exit"
  fi
}

run_android() {
  run_platform \
    android \
    net10.0-android \
    "" \
    "${ANDROID_DEVICE:-${DEVICE_RUNNERS_DEVICE:-}}"
}

run_ios() {
  run_platform \
    ios \
    net10.0-ios \
    "${IOS_RUNTIME_IDENTIFIER:-iossimulator-arm64}" \
    "${IOS_DEVICE:-${DEVICE_RUNNERS_DEVICE:-}}"
}

run_maccatalyst() {
  run_platform \
    maccatalyst \
    net10.0-maccatalyst \
    "${MACCATALYST_RUNTIME_IDENTIFIER:-maccatalyst-arm64}" \
    "${DEVICE_RUNNERS_DEVICE:-}"
}

platform="${1:-maccatalyst}"
case "$platform" in
  --help|-h)
    usage
    ;;
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

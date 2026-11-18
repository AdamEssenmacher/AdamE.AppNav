#!/bin/bash
# Capture the simulator display while an XCTest invocation runs.
set -euo pipefail
device=${1:?Usage: ./record.sh SIMULATOR_UDID OUTPUT_MP4 [additional xcodebuild test arguments]}
output=${2:?Usage: ./record.sh SIMULATOR_UDID OUTPUT_MP4 [additional xcodebuild test arguments]}
shift 2
test_runner="$(dirname "$0")/run.sh"

xcrun simctl io "$device" recordVideo --codec=h264 --force "$output" &
recorder=$!
stop_recording() {
    kill -INT "$recorder" 2>/dev/null || true
    wait "$recorder" || true
}
trap stop_recording EXIT

"$test_runner" "$device" "$@"

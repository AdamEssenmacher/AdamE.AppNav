#!/bin/bash
# Run against the prototype host already installed on this simulator.
set -euo pipefail
cd "$(dirname "$0")"
: "${1:?Usage: ./run.sh SIMULATOR_UDID [additional xcodebuild test arguments]}"
device=$1
shift
xcodebuild test -project NavigationTests.xcodeproj -scheme NavigationTests \
  -destination "platform=iOS Simulator,id=$device" -parallel-testing-enabled NO \
  -derivedDataPath "${DERIVED_DATA_PATH:-${TMPDIR:-/tmp}/appnav-probe-ui}" "$@"

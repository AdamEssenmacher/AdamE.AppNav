#!/usr/bin/env bash
# Builds the UIKit presenter's native checks (tests/AdamE.AppNav.iOS.NativeChecks), installs them on one booted
# simulator, runs them in the app's own window, and fails unless the app reports PASS.
#
# Usage: IOS_DEVICE=<booted simulator UDID> eng/run-ios-native-checks.sh
#
# The simulator is never chosen for you: simulators on a machine are shared, so name the one this run may use.
# Environment: CONFIGURATION (Debug), IOS_CHECKS_TIMEOUT seconds (300).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/tests/AdamE.AppNav.iOS.NativeChecks/AdamE.AppNav.iOS.NativeChecks.csproj"
BUNDLE_ID="com.adame.appnav.ios.nativechecks"
CONFIGURATION="${CONFIGURATION:-Debug}"
TIMEOUT="${IOS_CHECKS_TIMEOUT:-300}"
DEVICE="${IOS_DEVICE:-}"

if [[ -z "$DEVICE" ]]; then
  echo "Set IOS_DEVICE to the UDID of the booted simulator this run may use." >&2
  exit 2
fi
if ! xcrun simctl list devices booted | grep -q "($DEVICE)"; then
  echo "Simulator $DEVICE is not booted." >&2
  exit 2
fi

dotnet build "$PROJECT" -c "$CONFIGURATION" -p:RuntimeIdentifier=iossimulator-arm64 -nologo -v:q
APP="$(find "$ROOT/tests/AdamE.AppNav.iOS.NativeChecks/bin/$CONFIGURATION" -path '*iossimulator-arm64*' -name '*.app' -maxdepth 4 | head -n 1)"
if [[ -z "$APP" ]]; then
  echo "The check app was not built." >&2
  exit 1
fi

RUN="$(date +%s)-$$"
xcrun simctl terminate "$DEVICE" "$BUNDLE_ID" >/dev/null 2>&1 || true
xcrun simctl install "$DEVICE" "$APP"
CONTAINER="$(xcrun simctl get_app_container "$DEVICE" "$BUNDLE_ID" data)"
REPORT="$CONTAINER/Library/Caches/appnav-ios-checks.txt"
rm -f "$REPORT"
# A simulator app is a process on this Mac: simctl prints "<bundle ID>: <PID>".
PID="$(xcrun simctl launch --terminate-running-process "$DEVICE" "$BUNDLE_ID" --run "$RUN" | awk '{ print $NF }')"

deadline=$((SECONDS + TIMEOUT))
while (( SECONDS < deadline )); do
  if [[ -f "$REPORT" ]] && grep -q "^RUN $RUN$" "$REPORT"; then
    if grep -q "^PASS $RUN$" "$REPORT"; then
      grep '^OK ' "$REPORT"
      echo "PASS: $(grep -c '^OK ' "$REPORT") native check groups."
      xcrun simctl terminate "$DEVICE" "$BUNDLE_ID" >/dev/null 2>&1 || true
      exit 0
    fi
    if grep -q "^FAIL $RUN" "$REPORT"; then
      cat "$REPORT" >&2
      xcrun simctl terminate "$DEVICE" "$BUNDLE_ID" >/dev/null 2>&1 || true
      exit 1
    fi
  fi
  if ! kill -0 "$PID" 2>/dev/null; then
    if [[ -f "$REPORT" ]] && grep -q -E "^(PASS|FAIL) $RUN" "$REPORT"; then continue; fi
    echo "The check app exited without a result; look for its crash report in ~/Library/Logs/DiagnosticReports." >&2
    [[ -f "$REPORT" ]] && cat "$REPORT" >&2
    exit 1
  fi
  sleep 1
done
echo "The checks did not finish within ${TIMEOUT}s." >&2
[[ -f "$REPORT" ]] && cat "$REPORT" >&2
xcrun simctl terminate "$DEVICE" "$BUNDLE_ID" >/dev/null 2>&1 || true
exit 1

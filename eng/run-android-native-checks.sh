#!/usr/bin/env bash
# Builds the Android presenter's native checks (tests/AdamE.AppNav.Android.NativeChecks), installs them on one running
# emulator, runs them in the app's own activity, and fails unless the app reports PASS.
#
# Usage: ANDROID_SERIAL=<adb serial, e.g. emulator-5554> eng/run-android-native-checks.sh
#
# The emulator is never chosen for you: emulators on a machine are shared, so name the one this run may use.
# Environment: CONFIGURATION (Debug), ANDROID_CHECKS_TIMEOUT seconds (300).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/tests/AdamE.AppNav.Android.NativeChecks/AdamE.AppNav.Android.NativeChecks.csproj"
PACKAGE="com.adame.appnav.android.nativechecks"
ACTIVITY="$PACKAGE/$PACKAGE.CheckActivity"
CONFIGURATION="${CONFIGURATION:-Debug}"
TIMEOUT="${ANDROID_CHECKS_TIMEOUT:-300}"
SERIAL="${ANDROID_SERIAL:-}"

if [[ -z "$SERIAL" ]]; then
  echo "Set ANDROID_SERIAL to the adb serial of the running emulator this run may use." >&2
  exit 2
fi
if [[ "$(adb -s "$SERIAL" get-state 2>/dev/null)" != "device" ]]; then
  echo "Emulator $SERIAL is not running." >&2
  exit 2
fi
ADB=(adb -s "$SERIAL")

dotnet build "$PROJECT" -c "$CONFIGURATION" -nologo -v:q
APK="$(find "$ROOT/tests/AdamE.AppNav.Android.NativeChecks/bin/$CONFIGURATION" -name "$PACKAGE-Signed.apk" | head -n 1)"
if [[ -z "$APK" ]]; then
  echo "The check app was not built." >&2
  exit 1
fi

RUN="$(date +%s)-$$"
"${ADB[@]}" shell am force-stop "$PACKAGE" >/dev/null 2>&1 || true
"${ADB[@]}" install -r -t "$APK" >/dev/null
"${ADB[@]}" shell run-as "$PACKAGE" rm -f files/appnav-android-checks.txt files/appnav-android-press >/dev/null 2>&1 || true
# The device stays awake and unlocked for the run: the checks press Back in their own focused window.
"${ADB[@]}" shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1 || true
"${ADB[@]}" shell wm dismiss-keyguard >/dev/null 2>&1 || true
"${ADB[@]}" shell am start -W -n "$ACTIVITY" --es run "$RUN" >/dev/null

report() { "${ADB[@]}" shell run-as "$PACKAGE" cat files/appnav-android-checks.txt 2>/dev/null || true; }

deadline=$((SECONDS + TIMEOUT))
while (( SECONDS < deadline )); do
  # A check that needs the system's own Back key asks for it with this file: an app cannot inject keys.
  if "${ADB[@]}" shell run-as "$PACKAGE" test -f files/appnav-android-press 2>/dev/null; then
    "${ADB[@]}" shell input keyevent KEYCODE_BACK
    "${ADB[@]}" shell run-as "$PACKAGE" rm -f files/appnav-android-press
    continue
  fi
  REPORT="$(report)"
  if grep -q "^RUN $RUN$" <<<"$REPORT"; then
    if grep -q "^PASS $RUN$" <<<"$REPORT"; then
      grep '^OK ' <<<"$REPORT"
      echo "PASS: $(grep -c '^OK ' <<<"$REPORT") native check groups."
      "${ADB[@]}" shell am force-stop "$PACKAGE" >/dev/null 2>&1 || true
      exit 0
    fi
    if grep -q "^FAIL $RUN" <<<"$REPORT"; then
      echo "$REPORT" >&2
      "${ADB[@]}" shell am force-stop "$PACKAGE" >/dev/null 2>&1 || true
      exit 1
    fi
  fi
  if [[ -z "$("${ADB[@]}" shell pidof "$PACKAGE" 2>/dev/null)" ]]; then
    REPORT="$(report)"
    if grep -q -E "^(PASS|FAIL) $RUN" <<<"$REPORT"; then continue; fi
    echo "The check app exited without a result; its crash:" >&2
    "${ADB[@]}" logcat -d -b crash 2>/dev/null | tail -n 60 >&2 || true
    echo "$REPORT" >&2
    exit 1
  fi
  sleep 0.3
done
echo "The checks did not finish within ${TIMEOUT}s." >&2
report >&2
"${ADB[@]}" shell am force-stop "$PACKAGE" >/dev/null 2>&1 || true
exit 1

#!/usr/bin/env bash
# Builds the Android presenter's native checks (tests/AdamE.AppNav.Android.NativeChecks), installs them on one running
# emulator, runs them in the app's own activity, and fails unless the app reports PASS.
#
# Usage: ANDROID_SERIAL=<adb serial, e.g. emulator-5554> eng/run-android-native-checks.sh
#
# The emulator is never chosen for you: emulators on a machine are shared, so name the one this run may use.
# Environment: CONFIGURATION (Debug), ANDROID_CHECKS_TIMEOUT seconds (300), ANDROID_CHECKS_DIAGNOSTICS (the directory a
# failed run leaves its screenshot, window list and log in; artifacts/android-native-checks).
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
# The device stays awake and unlocked for the run, with nothing of the system's over the app: the checks press Back in
# their own focused window. A freshly booted emulator can show a system dialog that keeps focus.
STAY_ON="$("${ADB[@]}" shell settings get global stay_on_while_plugged_in 2>/dev/null | tr -d '\r')"
"${ADB[@]}" shell svc power stayon true >/dev/null 2>&1 || true
# Restore what the shared device had.
trap '"${ADB[@]}" shell settings put global stay_on_while_plugged_in "${STAY_ON:-0}" >/dev/null 2>&1 || true' EXIT
"${ADB[@]}" shell input keyevent KEYCODE_WAKEUP >/dev/null 2>&1 || true
"${ADB[@]}" shell wm dismiss-keyguard >/dev/null 2>&1 || true
"${ADB[@]}" shell am broadcast -a android.intent.action.CLOSE_SYSTEM_DIALOGS >/dev/null 2>&1 || true
"${ADB[@]}" shell am start -W -n "$ACTIVITY" --es run "$RUN" >/dev/null

report() { "${ADB[@]}" shell run-as "$PACKAGE" cat files/appnav-android-checks.txt 2>/dev/null || true; }
# A slow emulator's System UI can raise an "isn't responding" dialog over the app, which takes window focus from the
# checks; closing system dialogs dismisses it. The check app's own "isn't responding" dialog is a failure, never
# dismissed. Returns 0 when the check app has focus.
current_focus() { "${ADB[@]}" shell dumpsys window 2>/dev/null | grep -m1 'mCurrentFocus=' || true; }
clear_anr() {
  local focused
  focused="$(current_focus)"
  if grep -q "Application Not Responding: $PACKAGE" <<<"$focused"; then
    echo "The check app is not responding:" >&2
    report >&2
    focus
    "${ADB[@]}" shell am force-stop "$PACKAGE" >/dev/null 2>&1 || true
    exit 1
  fi
  if grep -q 'Application Not Responding:' <<<"$focused"; then
    echo "Closing another app's 'not responding' dialog over the checks: $focused" >&2
    "${ADB[@]}" shell am broadcast -a android.intent.action.CLOSE_SYSTEM_DIALOGS >/dev/null 2>&1 || true
    return 1
  fi
  grep -q "$PACKAGE" <<<"$focused"
}
# What has window focus, for a failure that depends on it, and what the device showed, for the run's artifacts.
DIAGNOSTICS="${ANDROID_CHECKS_DIAGNOSTICS:-$ROOT/artifacts/android-native-checks}"
focus() {
  "${ADB[@]}" shell dumpsys window 2>/dev/null | grep -E 'mCurrentFocus|mFocusedApp' >&2 || true
  mkdir -p "$DIAGNOSTICS"
  "${ADB[@]}" exec-out screencap -p > "$DIAGNOSTICS/screen.png" 2>/dev/null || true
  "${ADB[@]}" shell dumpsys window windows > "$DIAGNOSTICS/windows.txt" 2>/dev/null || true
  "${ADB[@]}" logcat -d -t 2000 > "$DIAGNOSTICS/logcat.txt" 2>/dev/null || true
  echo "Diagnostics in $DIAGNOSTICS." >&2
}

deadline=$((SECONDS + TIMEOUT))
next_anr_check=0
while (( SECONDS < deadline )); do
  if (( SECONDS >= next_anr_check )); then
    clear_anr || true
    next_anr_check=$((SECONDS + 3))
  fi
  # A check that needs the system's own Back key asks for it with this file: an app cannot inject keys. Read with cat:
  # run-as on older Android has no test.
  if [[ -n "$("${ADB[@]}" shell run-as "$PACKAGE" cat files/appnav-android-press 2>/dev/null)" ]]; then
    # Only into the check app's own window: a dialog in front would take the key. Otherwise the press waits for the
    # next pass, and the report and crash checks below still run.
    if clear_anr; then
      "${ADB[@]}" shell input keyevent KEYCODE_BACK
      "${ADB[@]}" shell run-as "$PACKAGE" rm -f files/appnav-android-press
      continue
    fi
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
      focus
      "${ADB[@]}" shell am force-stop "$PACKAGE" >/dev/null 2>&1 || true
      exit 1
    fi
  fi
  if [[ -z "$("${ADB[@]}" shell pidof "$PACKAGE" 2>/dev/null)" ]]; then
    REPORT="$(report)"
    if grep -q -E "^(PASS|FAIL) $RUN" <<<"$REPORT"; then continue; fi
    focus
    echo "The check app exited without a result; its crash:" >&2
    "${ADB[@]}" logcat -d -b crash 2>/dev/null | tail -n 60 >&2 || true
    echo "$REPORT" >&2
    exit 1
  fi
  sleep 0.3
done
echo "The checks did not finish within ${TIMEOUT}s." >&2
report >&2
focus
"${ADB[@]}" shell am force-stop "$PACKAGE" >/dev/null 2>&1 || true
exit 1

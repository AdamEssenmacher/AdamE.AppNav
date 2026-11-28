#!/usr/bin/env bash
# Run the modal-open recreation regression on the Android device selected by ANDROID_SERIAL.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/tests/AdamE.AppNav.Maui.Tests"
package=com.adame.appnav.maui.tests
adb=(adb -s "${ANDROID_SERIAL:?Set ANDROID_SERIAL to the target emulator serial}")
case "$("${adb[@]}" shell getprop ro.product.cpu.abi | tr -d '\r')" in
  arm64-v8a) rid=android-arm64 ;;
  x86_64) rid=android-x64 ;;
  *) echo "Unsupported Android test device architecture." >&2; exit 1 ;;
esac

dotnet build "$project" -c Release -f net10.0-android -p:RuntimeIdentifier="$rid"
"${adb[@]}" install -r "$project/bin/Release/net10.0-android/$rid/$package-Signed.apk"
run="$(date +%s)-$$"
"${adb[@]}" shell am start -S -W -n "$package/.RecreationActivity" --es run "$run"
for ((attempt=0; attempt<60; attempt++)); do
  report="$("${adb[@]}" logcat -d -s AppNavRecreation:I)"
  if [[ "$report" == *"$run PASS "* ]]; then
    grep -F "$run " <<<"$report"
    exit 0
  fi
  if [[ "$report" == *"$run FAIL "* ]]; then
    echo "$report" >&2
    exit 1
  fi
  sleep 1
done
echo "Recreation produced no result for run $run." >&2
exit 1

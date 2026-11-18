# AppNav Compose probe

The probe renders AppNav's projection with Jetpack Compose Navigation 3. See the [Compose presenter guide](../../src/AdamE.AppNav.Compose/README.md) for the screen library and Activity integration, and the [shared presenter checks](../README.md) for transaction and mount lifetime coverage.

Install the .NET 11 Android workload, Android SDK platform 37 for the managed host and platform 36 for the Gradle screen library, and JDK 21. The build script selects the repository's prototype SDK and builds AppNav core from this checkout. Set `OAHU_FEED` to a NuGet source containing OAHU preview `0.1.0-preview.g7a56b3ba`. The first build needs access to NuGet.org and the Gradle repositories.

From the repository root, with a booted, unlocked arm64 emulator and Android gesture navigation enabled:

```sh
export ANDROID_HOME=/path/to/android-sdk
export JAVA_HOME=/path/to/jdk-21
export OAHU_FEED=/path/to/oahu-feed
export ANDROID_SERIAL=emulator-5554
prototype/Android/build.sh
python3 prototype/Android/UITests/run.py --serial "$ANDROID_SERIAL" \
  --apk prototype/Android/Host/bin/Release/net11.0-android37.0/android-arm64/com.adamessenmacher.appnav.probe-Signed.apk
```

Keep the device animator duration scale at 1× for the Game checks. The driver covers six scenarios.

| Scenario | Checks |
| --- | --- |
| Branch switching | Canonical deep link, native Back, and retained Compose state when switching branches |
| Link recreation | Same-process Activity replacement while the deep link is pending, with the outgoing Activity's observation fenced |
| Modal stack | Back pops the dialog stack, then dismisses it to reveal the underlying branch |
| Managed Back | The app's asynchronous Back policy controls native Back acceptance |
| Game push and Back | App-supplied resize sees different scene sizes and settles before commit. A canceled edge swipe keeps Game, and a completed swipe reconciles Back once. |
| Game recreation | Replacement Activity witnesses the pending push without spending the original cover key |

The [earlier API 36 recording](../results/home-game-predictive-android.mp4) and [accompanying trace](../results/home-game-predictive-android.trace.txt) capture the Game push, predictive Back and same-process recreation.

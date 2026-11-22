# SwiftUI presenter probe

This app exercises the reusable [SwiftUI presenter](../src/AdamE.AppNav.SwiftUI/README.md) with OAHU-mounted screens. Its UI tests read both the native stack and the router's committed history.

## AppNav-driven Home → Game push

The Home list includes a Button that sends an AppNav Open Game request and a native SwiftUI NavigationLink for comparison. Both display the same cover and Game toolbar. The button uses a pressed style and keeps the row selected during handoff. The two rows use separate zoom source IDs in a shared namespace. The native reference uses a view destination, whereas Scavos uses a value link with an OAHU requested mount ID. The managed row allocates an ordinary OAHU mount through the presenter.

The SwiftUI shell supplies `NavigationCustomization`. Its transaction hook animates the Game push, its destination hook selects the zoom for the tapped cover, and its chrome hook installs the Game toolbar. Replacing a branch or installing a multi-entry deep link still uses an animation-disabled transaction. The presenter listens for an active navigation controller's interactive transition before checking whether its top page is hittable. During a zoom, UIKit can temporarily hit-test its transition container.

The paired recordings show the AppNav row at left and the native link at right. They show press feedback, the tapped cover expanding into Game, and the Ready toolbar item moving with the destination. Display-link probes on the mounted Game content and the toolbar item also logged the same first tick during the animated push on both OS versions. UIKit scales and fades the bar as it zooms. The full-size, fully legible toolbar comes later in that animation.

- [iOS 18.6 side-by-side recording](results/home-game-zoom-ios18.mp4)
- [iOS 27.0 side-by-side recording](results/home-game-zoom-ios27.mp4)
- [iOS 18.6 canceled swipe followed by completed Back](results/home-game-return-ios18.mp4)

`testHomeGameZoomCancelAndReturn` verifies the full history after the push, after a slow edge drag that UIKit reports as canceled, and after tapping native Back. The counts are 2, 2 and 3. The native reference leaves AppNav history at its starting count of 1. The probe uses a synthetic cover and game screen in the presenter simulator app. Run the simulator build and install commands below, then record both tests with the following command.

```sh
prototype/UITests/record.sh "$DEVICE" /tmp/home-game.mp4 \
  -only-testing:NavigationTests/NavigationTests/testHomeGameZoomCancelAndReturn \
  -only-testing:NavigationTests/NavigationTests/testNativeHomeGameZoomReference
```

`testHomeGameZoomTakenBackDuringPush` covers a Back swipe that arrives before the shell settles the Game push. On a slower device a slow swipe can catch the zoom, and UIKit gives the push back. The probe's `--probe-hold-game-settlement` argument keeps a band over the navigation controller's centre while Game is on top, until a Back gesture begins. The shell cannot settle the push, so an ordinary edge swipe makes SwiftUI shorten the path while the push is still applying. The test expects the push and a HostReconciliation Back, a history count of 3, and Game to stay gone. The probe simulates the applying condition. The Game zoom has finished before the swipe, so the test does not reproduce UIKit reversing a push that is still animating. That case was checked on an iPhone SE running Scavos. `testHomeGameCancelledBackDuringPush` releases the same swipe short and expects Game to stay and the push to settle with a history count of 2.

## Native cross-fade

On iOS 27 the probe presents a single-entry sheet using `.native(.crossFade)` on the presented container. Its background fades in at a fixed position; dismissal returns to the original Home stack. iOS 18 uses the automatic presentation. [iOS 27.2 sheet recording](results/fade-sheet-ios27.mp4).

A separate Home button requests the same native style on a Game stack push. On the tested iOS 27 runtimes it slides. A direct SwiftUI `NavigationLink` with the concrete cross-fade modifier also slid on iOS 27.0. The cancellation test checks the visible Game path and the full history after an interrupted edge swipe, then returns with the system Back button. The replacement test swaps the window's hosting controller during an animated push while keeping the router and scene alive. It holds the new controller's settled observation, injects a matching observation carrying the old attachment epoch, and checks that releasing the new observation commits the push once. [iOS 27 stack push, cancellation and Back recording](results/home-game-crossfade-request-ios27.mp4).

The focused SwiftUI test cases run against the installed probe:

```sh
prototype/UITests/run.sh "$DEVICE" \
  -only-testing:NavigationTests/NavigationTests/testNativeFadeSheetPreservesHistory \
  -only-testing:NavigationTests/NavigationTests/testHomeGameCrossFadeCancelAndReturn \
  -only-testing:NavigationTests/NavigationTests/testReplaceHostDuringGamePush
```

## Earlier SwiftUI prototype recordings

The initial direct-path prototype passed 6/6 UI test runs on the iPhone 16 Pro simulator (iOS 18.6, 22G86) and 6/6 on the iPhone 18 Pro simulator (iOS 27.0, 24A434). Each simulator ran the canceled-swipe and Back-menu scenario three times and the ordinary Back traversal three times. Both recordings show the direct visible jump. The managed checks passed again after the native change. The host was built for the arm64 simulator with .NET 11 CoreCLR JIT and Xcode 27.2.

## Reproduce

Use an arm64 Mac with the .NET 11 SDK and iOS workload, Xcode 27.2, and a booted iPhone simulator running iOS 18 or later. Set `OAHU_FEED` to a NuGet source containing OAHU preview `0.1.0-preview.g7a56b3ba` and set `DEVICE` to the simulator UDID. The build uses AppNav from this checkout.

From the repository root, run:

```sh
prototype/Host/build.sh
xcrun simctl install "$DEVICE" prototype/Host/bin/Release/net11.0-ios/iossimulator-arm64/AppNavSwiftUIProbe.app
prototype/UITests/run.sh "$DEVICE"
```

The build script selects .NET 11 through `prototype/global.json`. See the [shared integration guide](README.md) for the managed transaction and hosting checks.

The UI tests launch the installed .NET host. The host’s deep-link button submits a canonical HTTPS URI with AppNav's AppLink request source. OS universal-link registration is outside this probe.

## Acceptance criteria

| Event | Visible native result | AppNav result |
| --- | --- | --- |
| Launch | Home detail | One history entry |
| Open the catalog deep link | Detail appears directly, with Catalog → Group → Item beneath it | One additional AppLink entry containing the entire planned target |
| Begin an interactive edge pop and release short | Transition reports cancellation and Detail remains visible | Identical committed state and history |
| Hold system Back and choose Group | Item and Detail both leave the stack | A single HostReconciliation entry, with Home's inactive stack preserved |
| Attempt Back while Home detail is guarded | System edge Back leaves Home detail visible and the app's Back command is canceled by policy | No additional history |
| Allow Back and tap the app's Back button | Home appears | One AppNav Back entry |
| Tap Home, then Catalog | Each tab returns to its retained screen | A HostReconciliation entry for each selection |
| Open the editor sheet | Review appears directly over Home detail | One command entry including the sheet stack |
| Tap Back in the sheet, then Done | Edit appears, then the sheet dismisses | One HostReconciliation entry for the inner pop and one for dismissal |

The managed checks also deliver incorrect, partial, and obsolete acknowledgements and exercise cancellation during an apply. They run independently of the native UI test.

On iOS 18.6, a preliminary test held the Back button, released, and then tapped a button labeled Group. Native timestamps showed this performed two distinct pops 1.53 seconds apart. Inspecting the accessibility tree with the finger still held revealed that the first menu item overlapped the Back button. The checked-in test keeps the finger down, moves to the second row, and releases once. This distinction matters because AppNav should record two history entries for two Back actions and one entry for a single multi-pop.

## How acknowledgement works

AppNav stages a numbered projection of both branch stacks. For a branch-switching link, the shell assigns the branch selection and complete typed paths in one animation-disabled SwiftUI transaction. SwiftUI can use its normal tab transition and create offscreen destinations lazily. Apple's [NavigationStack documentation](https://developer.apple.com/documentation/swiftui/navigationstack) illustrates replacing a path with multiple values to display the last destination and preserve the earlier ones for Back.

The acknowledgement combines the bound path with independent observations of the selected native stack. Its controller count must match the path, the visible top must have the expected destination marker, and every controller with a previously observed identity must agree at its position. An offscreen controller without a marker gets its logical identity from the path. The selected surface has to agree for two consecutive display-link samples with no reported navigation-controller transition. Acknowledgement concerns the navigable surface; a system tab crossfade can still be finishing. If a newly visible top disagrees when Back reveals it, the shell withholds the native observation. Obsolete revisions and incomplete paths cannot commit the deep link. A failed apply attempts a separately acknowledged rollback.

The UI test records the sampled tops of the destination branch during the apply. On a direct deep link it expects only Detail. A second test uses ordinary Back to reveal Item, then Group, then Catalog and checks each mounted page and history count. Frame-by-frame inspection of a recorded jump on each simulator showed only Home Detail and Detail during the handoff. [iOS 18.6 recording](results/direct-link-ios18.mp4) · [iOS 27.0 recording](results/direct-link-ios27.mp4).

A native Back observation must be a proper prefix of the accepted stack. The presenter submits that observed state to AppNav with its original base state. It serializes consecutive observations in arrival order and consumes an already-witnessed reconciliation without republishing its stack. On a canceled interaction the shell waits for UIKit's completion and takes a fresh stack reading. The test checks that reading alongside the mounted page, visible navigation bar, and full AppNav history.

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

`testHomeGameZoomTakenBackDuringPush` covers a Back swipe that arrives before the shell settles the Game push. On a slower device a slow swipe can catch the zoom, and UIKit gives the push back. The probe's `--probe-hold-game-settlement` argument keeps a band over the navigation controller's centre while Game is on top, until a Back gesture begins. The shell cannot settle the push, so an ordinary edge swipe makes SwiftUI shorten the path while the push is still applying. The test expects the push and a HostReconciliation Back, a history count of 3, and Game to stay gone. The probe simulates the applying condition. The Game zoom has finished before the swipe, so the test does not reproduce UIKit reversing a push that is still animating. That case was checked on an iPhone SE running Scavos. `testHomeGameCancelledBackDuringPush` releases the same swipe short and expects Game to stay and the push to settle with a history count of 2. SwiftUI writes the shorter path only once the gesture completes, so this test never takes the push back and its probe reads `restored=0`. The shell still restores a taken-back push if UIKit's stack settles with the page still on top, but no simulator test reaches that path ([#190](https://github.com/AdamEssenmacher/AdamE.AppNav/issues/190)).

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

## Split: Home beside the selected game

The `--probe-split` launch argument starts the probe on a `SplitNode`: Home in the primary column and, once a row is selected, that game's own stack in the secondary column. Home's rows are plain buttons that ask AppNav to select, and the game screens can push a page, switch to the other game, or send an AppNav Back. The footer adds `probe-split-layout`, which shows the split's layout, how often the shell repaired the compact column, and how many selections were taken back. The split tests run on any simulator. On a phone, or on the iPhone Duo simulator while it is closed, they cover the collapsed split. The expanded test needs the Duo open, and the fold test needs its posture driven from outside, so it skips unless the runner sees `APPNAV_POSTURE=1`.

| Test | Layout | What it proves |
| --- | --- | --- |
| `testSplitCollapsedSelectAndSwipeBack` | Collapsed | Selecting from Home pushes the game's root over Home. An edge swipe from that root closes the column as one `HostReconciliation`, and a later selection opens the other game. |
| `testSplitCollapsedSwitchGames` | Collapsed | A pop to the game's root leaves SwiftUI's stray `.sidebar` binding. Switching games then pops the old game natively before SwiftUI shows the new one. One Back still reaches Home, so no duplicate root is left underneath. |
| `testSplitSelectionTakenBack` | Collapsed | A Back during the animated selection, held applying by `--probe-hold-game-settlement`, records the selection and a Back that closes the column. The game does not come back. |
| `testSplitSheetOverGame` | Any | A sheet over the split, dismissed natively, keeps both columns. |
| `testSplitGuardedGameRootCollapsed` | Collapsed | With `--probe-guard-game`, Game A's root shows the app's Back. The edge swipe leaves it in place, the policy cancels the app's Back, and after Allow Back the column closes. |
| `testGuardedZoomedGameKeepsItsPolicy` | Plain stack | With `--probe-guard-game`, Game, which would otherwise zoom from its cover, has the app's Back and pushes without the zoom, so an edge swipe cannot pop it past the policy. |
| `testSplitGuardedGameRootReleased` | Collapsed | Stopping the guard while Game A's root shows removes the app's Back. The system Back returns, the guard puts back the swipe settings it found, and an edge swipe closes the column. |
| `testSplitGuardedGameRootSwipeOntoRoot` | Collapsed | A Back swipe from a page above the guarded root completes, with no cancelled gesture, because the guard waits for a quiet frame. The guard then holds against both an edge swipe and a content swipe. |
| `testSplitGuardedGameRootExpanded` | Expanded, sideways | The guarded root has no Back of either kind beside Home. |
| `testSplitExpandedSwitchAndBack` | Expanded, sideways | The Show Sidebar button reveals Home. Selecting the other game replaces the detail column. The system Back inside the game's stack and an AppNav Back both reconcile, the second one closing the column. |
| `testSplitFoldKeepsGameAfterPop` | Fold and unfold | After the stray `.sidebar` write, unfolding repairs the compact column once. Folding again keeps the game on top with nothing new in history, and one Back reaches Home. |

Run the collapsed tests on the closed Duo, or on a phone, and the expanded test on the open Duo:

```sh
prototype/UITests/run.sh "$DUO" -only-testing:NavigationTests/NavigationTests/testSplitCollapsedSelectAndSwipeBack
TEST_RUNNER_APPNAV_POSTURE=1 prototype/UITests/run.sh "$DUO" \
  -only-testing:NavigationTests/NavigationTests/testSplitFoldKeepsGameAfterPop
```

While the fold test runs, open the device when the log prints `SPLIT-POSTURE: open the device`, and close it at `SPLIT-POSTURE: close the device`, for example with the simulator controls' posture action.

On Xcode 27.2, XCUITest's taps on the detail column of the upright Duo inner display never reach the app, though the same taps from the simulator do, so the expanded test skips upright. The upright run was checked by hand on the Duo simulator: Show Sidebar overlays Home on the dimmed game, a selection keeps the overlay open, Hide Sidebar closes it, and then a push, a system Back and an AppNav Back reconcile as in the sideways test. The system Back left the stray binding, and the shell repaired it on the next quiet frame.

Further checks by hand on the Duo simulator:
- **AppNav Back over a stray binding, collapsed.** The game pushes a page, and the system Back returns to the game's root, leaving SwiftUI's stray `.sidebar` binding. An AppNav Back then traced `popping the collapsed secondary column to home`. UIKit showed Home on the next quiet frame, and the revision settled with the column closed after one pop request.
- **Fold race.** The simulator's posture change could not unfold and fold faster than the quiet expanded frame that repairs the binding, so the race is covered by `SurfaceState` tests only. A half-open-then-closed fold kept the game on top and added nothing to history.
- **Guarded root through a fold.** With the guarded Game A root on top, unfolding restored the swipes, and the root showed no Back beside Home. Folding again turned them off, and an edge swipe left the root in place. A guarded entry no longer takes the zoom, because UIKit's zoom-dismiss edge pan popped a zoomed guarded page past the policy. `testGuardedZoomedGameKeepsItsPolicy` covers the same bypass in a plain stack: it failed before the change and passes after it.
- **First selection side by side.** On the open Duo held sideways, SwiftUI wrote no compact column for the first selection: no `SwiftUI set the compact column` trace appeared.

On 2026-11-22, at the head that added the swipe-onto-guarded-root test, the whole suite passed on an iPhone 17 Pro simulator with iOS 27.2: 28 tests, 25 passed and 3 skipped.
- **Passed:** the 17 existing tests, the plain-stack guarded Game test, and the seven split tests that run collapsed: select and swipe back, game switch, taken-back selection, sheet over a game, guarded root, guard released, and swipe onto a guarded root.
- **Skipped:** the two expanded tests and the fold test.

On the Duo the same day, all nine collapsed tests passed on the closed device, including the plain-stack guard test and the posture-driven fold test, and three passed on the open device held sideways: the expanded switch, the guarded root, and the sheet.

A screen recording of `testSplitCollapsedSelectAndSwipeBack` on that phone shows Game A open with the zoom from its Home cover, and the swipe back shrinking the card into the cover. That is the Home → Game zoom, now going through a collapsed split.

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

A native Back observation must be a proper prefix of the accepted stack. The presenter submits that observed state to AppNav with its original base state. It submits consecutive observations in arrival order and consumes an already-witnessed reconciliation without republishing its stack. AppNav admits a submitted reconciliation ahead of any navigation still queued, so an operation queued behind a push the user took back plans from the shorter stack. The hosting checks cover that with a sheet queued behind a taken-back Game push. On a canceled interaction the shell waits for UIKit's completion and takes a fresh stack reading. The test checks that reading alongside the mounted page, visible navigation bar, and full AppNav history.

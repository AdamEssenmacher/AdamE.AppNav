# AppNav for Jetpack Compose

Android hosts can render AppNav's one-window projection with `AppNavNavigationShell`. It supports a single primary stack or a selected stack within a branch host, plus an optional dialog with its own stack. Every destination draws an OAHU mount supplied by the managed host. Android Back edits the displayed stack and reports the settled result to AppNav. A replacement Activity starts from the projection sent to its new attachment.

Include this directory's `src/main/kotlin` in the Android screen library's Kotlin source set. When consuming the NuGet package, use its `native/src/main/kotlin` directory. The [probe Gradle project](../../prototype/Android/Native/screens/build.gradle.kts) shows how to compile against the same OAHU runtime and Compose versions packaged by the .NET app. It uses Navigation 3 runtime and UI 1.2.1. The managed package supplies the matching Compose dependencies and includes the two unbound Nav3 AARs in the app build. The probe's [host project](../../prototype/Android/Host/Host.csproj) shows the OAHU code generation and Gradle integration.

For offline builds, set the MSBuild property `AppNavNavigation3AarDirectory` to the directory containing the two pinned AARs. When using a project reference, import the [transitive targets](buildTransitive/AdamE.AppNav.Compose.targets) in the Android app as shown in the probe. NuGet imports them automatically for package references.

Use a Gradle property to point the screen library at either source directory.

```kotlin
val appNavComposeKotlin = providers.gradleProperty("appNavComposeKotlin")
kotlin {
    sourceSets.named("main") {
        kotlin.srcDir(appNavComposeKotlin)
    }
}
```

Reference `AdamE.AppNav.Compose` from the managed Android host. Bind `OahuNavigationPresenter` to the AppNav router and use `AndroidNavigationHost.Attach` with the router's route entry lease registry, a factory that creates OAHU mounts for the app's screens, and a factory for the app's OAHU shell contract. Install the returned host's `View` in the Activity. The host wires observations to the attachment epoch. Dispose it on the Android main thread during Activity teardown. Attach a new host after recreation and call the router's `RefreshPresentationAsync` to render its current state.

In the OAHU-generated Compose screen, collect the shell model's `target` flow, map the wire target to `NavigationProjection`, and forward the `NavigationObservation` from `AppNavNavigationShell` to the model's `report` method. Apply the app's Material theme around the shell. The [probe screen](../../prototype/Android/Native/screens/src/main/kotlin/com/adamessenmacher/appnav/probe/DemoScreens.kt) shows the adapter and how to add an optional top bar.

Pass `requestBack` if your app evaluates Back policy in AppNav. For a stack pop or dialog dismissal, suspend in this callback until the router's asynchronous Back request finishes, including when policy refuses it. The shell reports callback failures through `onBackFailure`, which logs to Android by default, and releases the gate. Cancellation of the shell coroutine propagates normally. A request-local timeout from `withTimeout` is reported as a failure. Use a nonthrowing failure reporter. While a managed request is pending, the shell consumes repeated Back input and keeps the gate closed across projection updates. An accepted operation will publish a new projection, and a refused operation will leave the displayed stack in place. With the default callback absent, the shell handles Back in Nav3 and reconciles the settled result with AppNav. With a one-way bridge such as OAHU, assign each request a unique ID and bound the wait for its matching completion. The [probe](../../prototype/Android/Native/screens/src/main/kotlin/com/adamessenmacher/appnav/probe/DemoScreens.kt) uses `withTimeout` to bound that wait. Set a deadline appropriate for the app's policy and presentation. A local timeout ends the wait while managed work can continue. The probe holds a single request in its retained managed session until that request finishes. Requests arriving while it is busy get an immediate rejection with their own ID. The attached shell receives accepted completions and rejected IDs through separate state values so a rejection cannot replace the active request's completion.

The optional `branchLabel` and `unavailableContent` slots customize the branch label and the placeholder for a destination whose mount is unavailable.

The host renders one window and one optional dialog stack. Shared elements require both destinations to register in the same stack's shared scope. Retained-router reattachment restores the current projection during same-process Activity recreation. The app owns navigation persistence across process death. Predictive Back progress comes from the default native path. Supplying `requestBack` routes the decision through managed policy and applies the projection that the router publishes.

## Nav3 transitions and shared elements

Map the managed target's `transition` into `NavigationPresentationTransition`. It describes the operation, the two route occurrence IDs, an optional source key supplied by the UI action, and optional screen names provided by the managed presenter. Pass `NavigationAnimations` to select native `ContentTransform` values for forward transitions, ordinary pops, and predictive pops. Each callback receives Nav3's animation scope and the context for its actual pair of scenes. The predictive callback also receives the swipe edge. Return `null` for Nav3's standard transform. Use `entryMetadata` to supply native `NavEntry` metadata, which follows Nav3's normal precedence.

Pass `NavigationAnimations(sizeTransform = SizeTransform(clip = false) { _, _ -> tween(500) })` to animate changes in scene size. The shell supplies the transform to Nav3's `NavDisplay` for the primary and dialog stacks. With a size transform, the display allows each scene to measure at its own size within the available area. Screens that fill the available area have matching endpoints. Nav3 takes the size spec from this display-wide hook. The forward, pop and predictive-pop callbacks and the entry metadata still select the scene's enter and exit animations.

Mounted screens can read `LocalNavigationEntryScope` to get the current projection, the push origin of their mounted occurrence, and Compose's shared and animated visibility scopes. Register matching elements with `Modifier.sharedElement` or `sharedBounds` inside those scopes. The [Game probe](../../prototype/Android/Native/screens/src/main/kotlin/com/adamessenmacher/appnav/probe/GameScreen.kt) registers the expanded cover only when the destination has the matching `game-cover` origin. Each branch stack and each dialog stack has its own shared scope.

Nav3 owns gesture progress. During predictive Back, `predictiveBackProgress` exposes Android's actual progress while AppNav still holds the committed route. `predictiveBackCancellationCount` reflects the native Back handler's cancellations. A completed gesture reports the shorter stack after the outgoing scene leaves composition, Nav3 settles the display's size animation, and the shared elements settle. AppNav keeps outgoing mounts until that report is accepted. A canceled gesture leaves history unchanged. Refresh, rollback, and attachment replacement discard prior origins and show the specified state directly. Push origins otherwise remain available for reversing live, adjacent occurrences across subsequent pushes and pops.

The [Android probe guide](../../prototype/Android/README.md) covers building the signed APK and running the UI checks on an unlocked emulator. Enable gesture navigation and set the animator duration scale to 1× for the Game checks. The checks exercise canonical links, retained branch state, Back policy, modal stacks, a custom scene resize that finishes before AppNav commits, shared-element predictive Back cancellation and completion, and Activity replacement during a pending presentation.

```sh
python3 prototype/Android/UITests/run.py --serial emulator-5554 --apk /path/to/probe-Signed.apk
```

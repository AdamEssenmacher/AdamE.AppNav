# AppNav for Jetpack Compose

Android hosts can render AppNav's one-window projection with `AppNavNavigationShell`. It supports a single primary stack, a selected stack within a branch host, or a [split](#splits) at the window root, plus an optional modal with its own stack, shown in a dialog by default or in a window the app supplies. Every destination draws an OAHU mount supplied by the managed host. Android Back edits the displayed stack and reports the settled result to AppNav. A replacement Activity starts from the projection sent to its new attachment.

Include this directory's `src/main/kotlin` in the Android screen library's Kotlin source set. When consuming the NuGet package, use its `native/src/main/kotlin` directory. The [probe Gradle project](../../prototype/Android/Native/screens/build.gradle.kts) shows how to compile against the same OAHU runtime and Compose versions packaged by the .NET app. It uses Navigation 3 runtime and UI 1.2.1. The managed package supplies the matching Compose dependencies and includes the two unbound Nav3 AARs in the app build. The probe's [host project](../../prototype/Android/Host/Host.csproj) shows the OAHU code generation and Gradle integration.

For offline builds, set the MSBuild property `AppNavNavigation3AarDirectory` to the directory containing the two pinned AARs. When using a project reference, import the [transitive targets](buildTransitive/AdamE.AppNav.Compose.targets) in the Android app as shown in the probe. NuGet imports them automatically for package references.

Set `appNavComposeKotlin` in `gradle.properties` to either source directory. Relative paths resolve from the Gradle root project, as in the [probe's Gradle properties](../../prototype/Android/Native/gradle.properties).

```kotlin
val appNavComposeKotlin = providers.gradleProperty("appNavComposeKotlin")
kotlin {
    sourceSets.named("main") {
        kotlin.srcDir(rootProject.file(appNavComposeKotlin.get()))
    }
}
```

Reference `AdamE.AppNav.Compose` from the managed Android host. Bind `OahuNavigationPresenter` to the AppNav router and use `AndroidNavigationHost.Attach` with the router's route entry lease registry, a factory that creates OAHU mounts for the app's screens, and a factory for the app's OAHU shell contract. Install the returned host's `View` in the Activity. The host wires observations to the attachment epoch. Dispose it on the Android main thread during Activity teardown. Attach a new host after recreation and call the router's `RefreshPresentationAsync` to render its current state.

In the OAHU-generated Compose screen, collect the shell model's `target` flow, map the wire target to `NavigationProjection`, and forward the `NavigationObservation` from `AppNavNavigationShell` to the model's `report` method. Apply the app's Material theme around the shell. The [probe screen](../../prototype/Android/Native/screens/src/main/kotlin/com/adamessenmacher/appnav/probe/DemoScreens.kt) shows the adapter and how to add an optional top bar.

Pass `requestBack` if your app evaluates Back policy in AppNav. For a stack pop or dialog dismissal, suspend in this callback until the router's asynchronous Back request finishes, including when policy refuses it. The shell reports callback failures through `onBackFailure`, which logs to Android by default, and releases the gate. Cancellation of the shell coroutine propagates normally. A request-local timeout from `withTimeout` is reported as a failure. Use a nonthrowing failure reporter. While a managed request is pending, the shell consumes repeated Back input and keeps the gate closed across projection updates. An accepted operation will publish a new projection, and a refused operation will leave the displayed stack in place. With the default callback absent, the shell handles Back in Nav3 and reconciles the settled result with AppNav. With a one-way bridge such as OAHU, assign each request a unique ID and bound the wait for its matching completion. The [probe](../../prototype/Android/Native/screens/src/main/kotlin/com/adamessenmacher/appnav/probe/DemoScreens.kt) uses `withTimeout` to bound that wait. Set a deadline appropriate for the app's policy and presentation. A local timeout ends the wait while managed work can continue. The probe holds a single request in its retained managed session until that request finishes. Requests arriving while it is busy get an immediate rejection with their own ID. The attached shell receives accepted completions and rejected IDs through separate state values so a rejection cannot replace the active request's completion.

The optional `branchLabel` and `unavailableContent` slots customize the branch label and the placeholder for a destination whose mount is unavailable.

To present the modal stack in your own native window, such as a full-screen dialog or a bottom sheet, use the overload that takes `modalHost`. The shell calls it with a `NavigationModalPresentation` and the mounted content, which the host must compose. The shell owns the modal's lifetime: it composes the host for exactly as long as the occurrence is presented. Route every dismissal gesture through `onDismissRequest`. AppNav ignores the request while a transition is pending, which `canDismiss` reports as `false`, and managed Back policy can refuse it. A host must therefore never hide itself. A Material sheet only calls its own `onDismissRequest` after it has hidden, so veto the hidden state and ask AppNav from that veto instead:

```kotlin
AppNavNavigationShell(projection, report, modalHost = { presentation, content ->
    val current by rememberUpdatedState(presentation)
    val sheet = rememberModalBottomSheetState(confirmValueChange = { value ->
        if (value == SheetValue.Hidden) current.onDismissRequest()
        value != SheetValue.Hidden
    })
    ModalBottomSheet(onDismissRequest = presentation.onDismissRequest, sheetState = sheet) { content() }
}, requestBack = requestBack)
```

Repeated requests are harmless: the shell ignores them while one is pending.

A host that draws in the Activity window instead of its own still receives system Back: the shell routes it to the modal while one is shown. A callback from a retired modal occurrence does nothing, so a host can't dismiss the modal's replacement.

The host renders one window, which can be a split, and one optional modal stack. Shared elements require both destinations to register in the same stack's shared scope. Retained-router reattachment restores the current projection during same-process Activity recreation. The app owns navigation persistence across process death. Predictive Back progress comes from the default native path. Supplying `requestBack` routes the decision through managed policy and applies the projection that the router publishes.

## Splits

A window whose root is a `SplitNode` with a stack in each column renders as Material's list-detail layout through a custom Navigation 3 scene strategy. Map the wire's `ShellTarget.Split` into `NavigationProjection.split` with `NavigationSplitColumn.fromWireName`, and report the observation's `secondaryEntries` and `compactColumn.wireName`, as [the probe screen](../../prototype/Android/Native/screens/src/main/kotlin/com/adamessenmacher/appnav/probe/DemoScreens.kt) does. The projection's only branch is the primary column. `AndroidNavigationHost` reports `PresentsSplits`, so the OAHU presenter projects splits to it. Splits inside branches or modals, and branch hosts inside a column, are refused with `NotSupportedException` before anything is staged.

The shell hands Navigation 3 one flattened back stack: the primary column's entries, then the secondary column's. Each entry renders from exactly one scene:

- When the window is at least 840 dp wide and at least 480 dp tall (expanded width, not compact height), the two-pane scene shows the primary column's top in a 360 dp list pane, a divider, and the secondary column's top in the detail pane. With nothing selected the detail pane shows the shell's `splitPlaceholder`. A phone turned sideways keeps one pane.
- Otherwise the one-pane scene shows the phone stack: the primary column, with the secondary column on top while `compactColumn` is `Secondary`. A default secondary column under a primary column on top stays in the stack unrendered, so its saved state survives until a wider window shows it.

Folding, unfolding and resizing never change the logical state or send a reading, and they swap layouts without an animation. Entries keep their saved state across the swap because they stay in the same `NavDisplay`. With the Activity handling its own size changes, the composition survives; a recreated Activity starts from the projection as usual.

Back follows the column on top, as AppNav plans it:

| Window | On top | System Back |
| --- | --- | --- |
| One pane | Secondary column deeper than its root | Pops inside the secondary column |
| One pane | Secondary root | Closes the column, as `ClearSecondary` does; the reading reports no secondary column and the primary column on top |
| Two panes | Detail deeper than its root | Pops inside the detail pane, with predictive Back confined to that pane |
| Two panes | Detail root, primary column at its root | Not intercepted, so the system leaves the app, as from Home on a phone |
| Two panes | Detail root, primary column deeper | Closes the detail, as AppNav plans it |
| Either | Primary column on top | Pops the primary column and closes a default secondary column, as AppNav plans it |

This deviates from Material's recommended default, `PopUntilScaffoldValueChange`, which leaves the app from any detail screen while both panes show; here Back first goes back inside the detail pane. At the detail root both leave the app. Not intercepting there also deviates from AppNav core, whose planned Back would close the column: the press never reaches the router, so managed Back policy is not asked. With `requestBack`, the shell asks the router only when it would intercept, and the router's projection drives the change.

In two panes, a pane whose top changes cross-fades with Navigation 3's default motion, and a predictive Back scales the outgoing pane. The other pane stays still. Entries can read `LocalNavigationEntryScope`'s `splitColumn` and `isSplitExpanded`. Hide an Up affordance on the secondary root while the columns are side by side. A selection shown beside its source has no `origin`, so a shared-element zoom from a list row applies only when one pane stacks the selection over the list, and an unfold drops a zoom already shown.

A modal covers the whole split. Route entries in both columns stay mounted, and their leases stay open, in both layouts. The placeholder is not an entry; it can compose twice while a pane animates. Hinge-aware and tabletop layouts are not supported: a half-open book posture shows the same 360 dp list pane.

## Nav3 transitions and shared elements

Map the managed target's `transition` into `NavigationPresentationTransition`. It describes the operation, the two route occurrence IDs, an optional source key supplied by the UI action, and optional screen names provided by the managed presenter. Pass `NavigationAnimations` to select native `ContentTransform` values for forward transitions, ordinary pops, and predictive pops. Each callback receives Nav3's animation scope and the context for its actual pair of scenes. The predictive callback also receives the swipe edge. Return `null` for Nav3's standard transform. Use `entryMetadata` to supply native `NavEntry` metadata, which follows Nav3's normal precedence.

When creating `NavigationAnimations` in a composable, use `remember` and key it on any inputs that affect the callbacks. Reusing the instance lets the shell retain Nav3 entries and their transition metadata across recompositions. To animate changes in scene size, pass the remembered configuration as the shell's `animations` argument.

```kotlin
val animations = remember {
    NavigationAnimations(sizeTransform = SizeTransform(clip = false) { _, _ -> tween(500) })
}
```

The shell supplies the transform to Nav3's `NavDisplay` for the primary and dialog stacks. With a size transform, the display allows each scene to measure at its own size within the available area. Screens that fill the available area have matching endpoints. Nav3 takes the size spec from this display-wide hook. The forward, pop and predictive-pop callbacks and the entry metadata still select the scene's enter and exit animations.

Mounted screens can read `LocalNavigationEntryScope` to get the current projection, the push origin of their mounted occurrence, and Compose's shared and animated visibility scopes. Register matching elements with `Modifier.sharedElement` or `sharedBounds` inside those scopes. The [Game probe](../../prototype/Android/Native/screens/src/main/kotlin/com/adamessenmacher/appnav/probe/GameScreen.kt) registers the expanded cover only when the destination has the matching `game-cover` origin. Each branch stack and each dialog stack has its own shared scope.

Nav3 owns gesture progress. During predictive Back, `predictiveBackProgress` exposes Android's actual progress while AppNav still holds the committed route. `predictiveBackCancellationCount` reflects the native Back handler's cancellations. A completed gesture reports the shorter stack after the outgoing scene leaves composition, Nav3 settles the display's size animation, and the shared elements settle. AppNav keeps outgoing mounts until that report is accepted. A canceled gesture leaves history unchanged. Refresh, rollback, and attachment replacement discard prior origins and show the specified state directly. Push origins otherwise remain available for reversing live, adjacent occurrences across subsequent pushes and pops.

The [Android probe guide](../../prototype/Android/README.md) covers building the signed APK and running the UI checks on an unlocked emulator. Enable gesture navigation and set the animator duration scale to 1× for the Game checks. The checks exercise canonical links, retained branch state, Back policy, modal stacks, a custom scene resize that finishes before AppNav commits, shared-element predictive Back cancellation and completion, and Activity replacement during a pending presentation.

```sh
python3 prototype/Android/UITests/run.py --serial emulator-5554 --apk /path/to/probe-Signed.apk
```

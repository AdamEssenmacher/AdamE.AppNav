package com.adamessenmacher.appnav.compose

import android.util.Log
import androidx.activity.compose.BackHandler
import androidx.compose.animation.EnterExitState
import androidx.compose.animation.SharedTransitionLayout
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.wrapContentSize
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.PrimaryScrollableTabRow
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.MutableState
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalWindowInfo
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.navigation3.runtime.NavEntry
import androidx.navigation3.runtime.rememberDecoratedNavEntries
import androidx.navigation3.runtime.rememberSaveableStateHolderNavEntryDecorator
import androidx.navigation3.scene.SceneInfo
import androidx.navigation3.scene.SinglePaneSceneStrategy
import androidx.navigation3.scene.rememberNavigationEventState
import androidx.navigation3.scene.rememberSceneState
import androidx.navigation3.ui.LocalNavAnimatedContentScope
import androidx.navigation3.ui.NavDisplay
import androidx.navigation3.ui.defaultPopTransitionSpec
import androidx.navigation3.ui.defaultPredictivePopTransitionSpec
import androidx.navigation3.ui.defaultTransitionSpec
import androidx.navigationevent.NavigationEventTransitionState
import androidx.navigationevent.compose.NavigationBackHandler
import androidx.navigationevent.compose.NavigationEventState
import com.adamessenmacher.oahu.compose.OahuMount
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

private val DefaultAnimations = NavigationAnimations()

/** Renders the shell using the original positional controls argument. */
@Composable
public fun AppNavNavigationShell(
    target: NavigationProjection,
    report: (NavigationObservation) -> Unit,
    requestBack: (suspend () -> Unit)?,
    branchLabel: @Composable (NavigationBranch) -> Unit,
    unavailableContent: @Composable (MountedEntry) -> Unit,
    content: @Composable () -> Unit,
) {
    AppNavNavigationShell(target, report, requestBack, branchLabel, unavailableContent, controls = content)
}

/** Renders the shell and logs managed Back failures to Android. */
@Composable
public fun AppNavNavigationShell(
    target: NavigationProjection,
    report: (NavigationObservation) -> Unit,
    requestBack: (suspend () -> Unit)? = null,
    branchLabel: @Composable (NavigationBranch) -> Unit = { Text(it.title) },
    unavailableContent: @Composable (MountedEntry) -> Unit = { UnavailableContent() },
    animations: NavigationAnimations = DefaultAnimations,
    controls: @Composable () -> Unit = {},
) {
    AppNavNavigationShell(
        target, report, requestBack, branchLabel, unavailableContent, animations,
        onBackFailure = { Log.e("AppNav", "Managed Back failed", it) },
        controls = controls,
    )
}

/**
 * Render the managed projection as a selected native stack, optionally overlaid by a modal stack.
 * A new revision supersedes any local edits. On Activity recreation the shell starts with the
 * current managed projection, so Nav3's saved state cannot replay obsolete stack keys.
 *
 * Set [requestBack] when AppNav must evaluate application Back policy before changing the UI.
 * In that mode the callback requests a managed navigation operation and its next projection
 * drives the stack change. Suspend until the managed request finishes, even when the policy
 * refuses Back. The shell holds the Back gate closed while the callback is running.
 * If the callback fails, [onBackFailure] receives the error and the gate reopens.
 * Calls across a one-way bridge should bound the wait for completion and report timeout as a failure.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
public fun AppNavNavigationShell(
    target: NavigationProjection,
    report: (NavigationObservation) -> Unit,
    requestBack: (suspend () -> Unit)? = null,
    branchLabel: @Composable (NavigationBranch) -> Unit = { Text(it.title) },
    unavailableContent: @Composable (MountedEntry) -> Unit = { UnavailableContent() },
    animations: NavigationAnimations = DefaultAnimations,
    onBackFailure: (Throwable) -> Unit,
    controls: @Composable () -> Unit = {},
) {
    AppNavNavigationShell(
        target, report, modalHost = { presentation, content -> DefaultModalHost(presentation, content) },
        requestBack, branchLabel, unavailableContent, animations, onBackFailure, controls,
    )
}

/**
 * State and actions for the active modal occurrence's native presentation.
 *
 * The shell owns the modal's lifetime: the occurrence is presented for exactly as long as the shell
 * composes the host for it. [onDismissRequest] only asks; AppNav can ignore it while a transition
 * is pending or refuse it through managed Back policy. A host must therefore never hide itself.
 * Keep the window shown until the shell stops composing it. A Material sheet calls its own dismiss
 * callback only after hiding, so in `confirmValueChange` call [onDismissRequest] and return false
 * for `SheetValue.Hidden`.
 */
public class NavigationModalPresentation internal constructor(
    public val id: String,
    public val entryCount: Int,
    /** False while AppNav ignores dismissal requests. True does not promise Back policy will allow it. */
    public val canDismiss: Boolean,
    public val onDismissRequest: () -> Unit,
)

/**
 * Renders the shell with a caller-provided modal window. The [modalHost] must compose its
 * mounted content and route dismissal requests through [NavigationModalPresentation.onDismissRequest].
 * That callback preserves AppNav's transition gate and managed Back policy. A host that draws in
 * the Activity window instead of its own still gets system Back: the shell routes it to the modal.
 *
 * A projection with a [NavigationProjection.split] shows both columns when the window is at least 840 dp
 * wide and 480 dp tall: the primary column's top in a 360 dp list pane, a divider, and the secondary
 * column's top or [LocalNavigationSplitPlaceholder] when nothing is selected. Otherwise it shows one stack: the primary
 * column, with the secondary column on top while [NavigationSplit.compactColumn] is secondary. The
 * placeholder is not a navigation entry; it may be composed twice while a pane animates.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
public fun AppNavNavigationShell(
    target: NavigationProjection,
    report: (NavigationObservation) -> Unit,
    modalHost: @Composable (NavigationModalPresentation, @Composable () -> Unit) -> Unit,
    requestBack: (suspend () -> Unit)? = null,
    branchLabel: @Composable (NavigationBranch) -> Unit = { Text(it.title) },
    unavailableContent: @Composable (MountedEntry) -> Unit = { UnavailableContent() },
    animations: NavigationAnimations = DefaultAnimations,
    onBackFailure: (Throwable) -> Unit = { Log.e("AppNav", "Managed Back failed", it) },
    controls: @Composable () -> Unit = {},
) {
    val splitPlaceholder = LocalNavigationSplitPlaceholder.current
    val surface = remember(target.revision) { DisplayedSurface(target) }
    // A layout change waits for a Back gesture under way, so the callback that started it keeps its
    // enabled state and the Back it completes, and the new layout applies once the gesture ends.
    val primaryNavigation = remember { mutableStateOf<NavigationEventState<SceneInfo<MountedEntry>>?>(null) }
    val inBackGesture = primaryNavigation.value?.transitionState is NavigationEventTransitionState.InProgress
    val windowTwoPanes = showsBothColumns(LocalWindowInfo.current.containerDpSize)
    val layoutLatch = remember { LayoutLatch(windowTwoPanes) }
    val twoPanes = if (inBackGesture) layoutLatch.twoPanes else windowTwoPanes
    SideEffect { if (!inBackGesture) layoutLatch.twoPanes = windowTwoPanes }
    val origins = remember { NavigationOrigins() }
    val backScope = rememberCoroutineScope()
    var managedBackInFlight by remember { mutableStateOf(false) }
    val branchStates = rememberSaveableStateHolder()
    val retainedBranches = remember { mutableSetOf<String>() }
    SideEffect {
        // A zoom belongs to a one-pane selection. Side by side the selection shows beside its source, so drop
        // every origin there; a later fold must not replay a zoom.
        if (twoPanes && target.split != null) origins.drop(target) else origins.stage(target)
        val currentBranches = target.branches.mapTo(mutableSetOf(), NavigationBranch::branch)
        (retainedBranches - currentBranches).forEach(branchStates::removeState)
        retainedBranches.retainAll(currentBranches)
        retainedBranches.addAll(currentBranches)
    }
    val branch = surface.branch
    val primary = surface.branches[branch].orEmpty()
    val secondary = surface.secondary
    val compact = surface.compactColumn
    val modal = surface.modal
    val modalId = target.modalId.takeIf { modal.isNotEmpty() }.orEmpty()
    // A split hands Nav3 one flattened stack: the primary column, then the secondary column.
    val expectedPrimary = StackRender(target.revision, branch, primary + secondary, primary.size, compact)
        .takeIf { primary.isNotEmpty() }
    val expectedModal = StackRender(target.revision, modalId, modal).takeIf { modal.isNotEmpty() }
    var renderedPrimary by remember { mutableStateOf<StackRender?>(null) }
    var renderedModal by remember { mutableStateOf<StackRender?>(null) }
    val latestReport by rememberUpdatedState(report)
    val latestBackFailure by rememberUpdatedState(onBackFailure)
    val observation = NavigationObservation(
        target.revision,
        branch,
        primary.map(MountedEntry::entryId),
        modalId,
        modal.map(MountedEntry::entryId),
        secondary.map(MountedEntry::entryId),
        compact,
    )

    LaunchedEffect(observation, renderedPrimary, renderedModal) {
        if (renderedPrimary == expectedPrimary && renderedModal == expectedModal) {
            latestReport(observation)
            surface.hasReported = true
        }
    }
    LaunchedEffect(target, observation, renderedPrimary, renderedModal) {
        if (target.selectedBranch == branch &&
            target.branches.firstOrNull { it.branch == branch }?.entries.orEmpty() == primary &&
            target.split?.secondaryEntries.orEmpty() == secondary && target.split?.compactColumn == compact &&
            target.modalEntries == modal && renderedPrimary == expectedPrimary && renderedModal == expectedModal
        ) {
            origins.prune(target)
            surface.awaitingAcceptance = false
        }
    }
    fun canNavigate() = surface.canNavigate && !managedBackInFlight
    fun back(modalBack: Boolean) {
        if (!canNavigate() || !surface.hasBack(modalBack, twoPanes)) return
        if (requestBack == null) {
            if (modalBack) surface.popModal() else surface.popPrimary(twoPanes)
        } else {
            managedBackInFlight = true
            backScope.launch {
                try {
                    requestBack()
                } catch (failure: Throwable) {
                    if (failure is CancellationException && !currentCoroutineContext().isActive) throw failure
                    latestBackFailure(failure)
                } finally {
                    managedBackInFlight = false
                }
            }
        }
    }

    val modalShown = modal.isNotEmpty()
    val primaryHasBack = surface.hasBack(modalBack = false, twoPanes)
    val allowPrimaryPreview = !modalShown && primaryHasBack && canNavigate() && requestBack == null
    val allowModalPreview = modalShown && canNavigate() && requestBack == null && modal.size > 1
    // Consume Back within the shell while a managed request or a presentation is pending. A modal
    // host need not have its own window, so while a modal is shown the shell takes Back for it.
    BackHandler(
        enabled = if (modalShown) !allowModalPreview else !allowPrimaryPreview && (primaryHasBack || !canNavigate()),
    ) {
        back(modalBack = modalShown)
    }

    Scaffold(
        topBar = controls,
        bottomBar = {
            val selected = target.branches.indexOfFirst { it.branch == branch }
            if (target.isBranchHost && selected >= 0) {
                PrimaryScrollableTabRow(selectedTabIndex = selected, edgePadding = 0.dp) {
                    target.branches.forEach { tab ->
                        Tab(
                            selected = tab.branch == branch,
                            enabled = canNavigate() && modal.isEmpty(),
                            onClick = { if (canNavigate()) surface.selectBranch(tab.branch) },
                            text = { branchLabel(tab) },
                        )
                    }
                }
            }
        },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(padding)) {
            if (expectedPrimary == null) {
                SideEffect { renderedPrimary = null }
            } else {
                branchStates.SaveableStateProvider(branch) {
                    MountedStack(expectedPrimary, target.transition, animations, origins, allowPrimaryPreview,
                        { back(modalBack = false) }, unavailableContent, twoPanes, splitPlaceholder, primaryNavigation) {
                        renderedPrimary = it
                    }
                }
            }
        }
    }
    if (expectedModal == null) {
        SideEffect { renderedModal = null }
    } else {
        key(modalId) {
            val dismiss by rememberUpdatedState({ back(modalBack = true) })
            var mounted by remember { mutableStateOf(false) }
            DisposableEffect(Unit) {
                mounted = true
                onDispose { mounted = false }
            }
            // A modal ID names the declared modal, so a revision can replace its occurrence in place.
            // The root entry ID identifies the occurrence, which keeps a retired callback inert.
            val occurrence = modal.first().entryId
            val currentOccurrence by rememberUpdatedState(occurrence)
            modalHost(NavigationModalPresentation(modalId, modal.size, canNavigate()) {
                if (mounted && currentOccurrence == occurrence) dismiss()
            }) {
                MountedStack(expectedModal, target.transition, animations, origins, allowModalPreview,
                    { back(modalBack = true) }, unavailableContent) {
                    renderedModal = it
                }
            }
        }
    }
}

@Composable
private fun DefaultModalHost(
    presentation: NavigationModalPresentation,
    content: @Composable () -> Unit,
) {
    Dialog(
        onDismissRequest = presentation.onDismissRequest,
        properties = DialogProperties(dismissOnClickOutside = false),
    ) {
        Surface(shape = MaterialTheme.shapes.large) {
            Column(Modifier.fillMaxWidth()) {
                TextButton(
                    onClick = presentation.onDismissRequest,
                    enabled = presentation.canDismiss,
                ) {
                    Text(if (presentation.entryCount > 1) "Back" else "Dismiss")
                }
                Box(Modifier.fillMaxWidth().fillMaxHeight(0.8f)) {
                    content()
                }
            }
        }
    }
}

@Composable
private fun UnavailableContent() {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text("Screen unavailable")
    }
}

private class DisplayedSurface(target: NavigationProjection) {
    var branch by mutableStateOf(target.selectedBranch)
        private set
    var branches by mutableStateOf(target.branches.associate { it.branch to it.entries })
        private set
    var modal by mutableStateOf(target.modalEntries)
        private set
    var secondary by mutableStateOf(target.split?.secondaryEntries.orEmpty())
        private set
    /** The split's column on top in one pane, or null without a split. */
    var compactColumn by mutableStateOf(target.split?.compactColumn)
        private set
    var hasReported by mutableStateOf(false)
    var awaitingAcceptance by mutableStateOf(false)
    val canNavigate: Boolean get() = hasReported && !awaitingAcceptance

    private fun splitBack(twoPanes: Boolean): SplitBack? = compactColumn?.let {
        splitBack(branches[branch].orEmpty().size, secondary.size, it, twoPanes)
    }

    fun hasBack(modalBack: Boolean, twoPanes: Boolean): Boolean = when {
        modalBack -> modal.isNotEmpty()
        modal.isNotEmpty() -> false
        else -> splitBack(twoPanes)?.let { it != SplitBack.None } ?: (branches[branch].orEmpty().size > 1)
    }

    // The presenter validates each native gesture against the last accepted surface. A tab switch
    // followed immediately by Back must report two changes, even if Compose can coalesce frames.
    fun selectBranch(selected: String) {
        if (canNavigate && modal.isEmpty() && selected != branch) {
            awaitingAcceptance = true
            branch = selected
        }
    }

    fun popPrimary(twoPanes: Boolean) {
        val entries = branches[branch].orEmpty()
        if (!canNavigate || modal.isNotEmpty()) return
        when (splitBack(twoPanes)) {
            null -> if (entries.size > 1) {
                awaitingAcceptance = true
                branches = branches + (branch to entries.dropLast(1))
            }
            SplitBack.None -> Unit
            SplitBack.PopSecondary -> {
                awaitingAcceptance = true
                secondary = secondary.dropLast(1)
            }
            SplitBack.CloseSecondary -> {
                awaitingAcceptance = true
                secondary = emptyList()
                compactColumn = NavigationSplitColumn.Primary
            }
            SplitBack.PopPrimary -> {
                awaitingAcceptance = true
                branches = branches + (branch to entries.dropLast(1))
                secondary = emptyList()
                compactColumn = NavigationSplitColumn.Primary
            }
        }
    }

    fun popModal() {
        if (canNavigate && modal.isNotEmpty()) {
            awaitingAcceptance = true
            modal = modal.dropLast(1)
        }
    }
}

/** A rendered stack. For a split, [entries] is the flattened stack and [primaryCount] its primary column's size. */
private data class StackRender(
    val revision: Long,
    val stackId: String,
    val entries: List<MountedEntry>,
    val primaryCount: Int = entries.size,
    val compactColumn: NavigationSplitColumn? = null,
) {
    val isSplit: Boolean get() = compactColumn != null
    val primary: List<MountedEntry> get() = entries.take(primaryCount)
    val secondary: List<MountedEntry> get() = entries.drop(primaryCount)

    /** The stack a single pane presents: the primary column, then the secondary column while it is on top. */
    val presented: List<MountedEntry>
        get() = if (compactColumn == NavigationSplitColumn.Secondary) entries else primary

    /** The entries on screen once settled. Two panes show both columns' tops; one pane shows its top. */
    fun visible(twoPanes: Boolean): List<MountedEntry> = when {
        !isSplit -> listOfNotNull(entries.lastOrNull())
        twoPanes -> listOfNotNull(primary.lastOrNull(), secondary.lastOrNull())
        else -> listOfNotNull(presented.lastOrNull())
    }

    fun columnOf(index: Int): NavigationSplitColumn? = when {
        !isSplit -> null
        index < primaryCount -> NavigationSplitColumn.Primary
        else -> NavigationSplitColumn.Secondary
    }
}

private class LayoutLatch(var twoPanes: Boolean)

private class SceneRenderHistory {
    var shownRevision: Long? = null
    var settledStack: StackRender? = null
    var isStillSettled: () -> Boolean = { false }
}

@Composable
private fun MountedStack(
    stack: StackRender,
    presentation: NavigationPresentationTransition,
    animations: NavigationAnimations,
    origins: NavigationOrigins,
    allowBackPreview: Boolean,
    onBack: () -> Unit,
    unavailableContent: @Composable (MountedEntry) -> Unit,
    twoPanes: Boolean = false,
    splitPlaceholder: @Composable () -> Unit = {},
    navigationHolder: MutableState<NavigationEventState<SceneInfo<MountedEntry>>?>? = null,
    onSettled: (StackRender) -> Unit,
) {
    var lastReset by rememberSaveable { mutableLongStateOf(0) }
    val history = remember { SceneRenderHistory() }
    val newRevision = history.shownRevision?.let { it != stack.revision } == true
    val resetScene = newRevision && presentation.resetsOrigins &&
        (history.settledStack?.copy(revision = stack.revision) != stack || !history.isStillSettled())
    val epoch = if (resetScene) stack.revision else lastReset
    SideEffect {
        history.shownRevision = stack.revision
        lastReset = epoch
    }
    // Rebuild a changed maintenance scene without using obsolete animation metadata. Each reset
    // owns a distinct NavEntry saveable holder while the old and new compositions exchange places.
    key(epoch) {
        StackScene(stack, presentation, animations, origins, allowBackPreview, onBack, unavailableContent,
            twoPanes, splitPlaceholder, navigationHolder) { seen, isStillSettled ->
            history.settledStack = seen
            history.isStillSettled = isStillSettled
            onSettled(seen)
        }
    }
}

@Composable
private fun StackScene(
    stack: StackRender,
    presentation: NavigationPresentationTransition,
    animations: NavigationAnimations,
    origins: NavigationOrigins,
    allowBackPreview: Boolean,
    onBack: () -> Unit,
    unavailableContent: @Composable (MountedEntry) -> Unit,
    twoPanes: Boolean,
    splitPlaceholder: @Composable () -> Unit,
    navigationHolder: MutableState<NavigationEventState<SceneInfo<MountedEntry>>?>?,
    onSettled: (StackRender, () -> Boolean) -> Unit,
) {
    val currentStack by rememberUpdatedState(stack)
    val currentTwoPanes by rememberUpdatedState(twoPanes)
    val currentPresentation by rememberUpdatedState(presentation)
    val currentUnavailable by rememberUpdatedState(unavailableContent)
    val settled by rememberUpdatedState(onSettled)
    SharedTransitionLayout {
        val sharedScope = this
        val composed = remember { mutableStateMapOf<MountedEntry, Int>() }
        var backCancellations by remember { mutableLongStateOf(0L) }
        val ownNavigation = remember { mutableStateOf<NavigationEventState<SceneInfo<MountedEntry>>?>(null) }
        val navigationState = navigationHolder ?: ownNavigation
        val undecorated = remember(stack.entries, stack.primaryCount, presentation, animations) {
            stack.entries.mapIndexed { index, entry ->
                val column = stack.columnOf(index)
                val metadata = animations.metadataFor(entry, presentation) + (AppNavMountedEntry to entry) +
                    listOfNotNull(column?.let { AppNavSplitColumn to it })
                NavEntry(entry, contentKey = entry.entryId, metadata = metadata) {
                    val animationScope = LocalNavAnimatedContentScope.current
                    val transition = animationScope.transition
                    val expected = currentStack
                    val context = currentPresentation
                    DisposableEffect(entry) {
                        composed[entry] = composed.getOrDefault(entry, 0) + 1
                        onDispose {
                            val remaining = composed.getValue(entry) - 1
                            if (remaining == 0) composed.remove(entry) else composed[entry] = remaining
                        }
                    }
                    val expanded = expected.isSplit && currentTwoPanes
                    // A selection shown beside its source does not zoom, and an unfold drops a zoom already shown.
                    val origin = if (expanded) null else origins.pushFor(entry, context, expected.presented, expected.revision)
                    val scope = NavigationEntryScope(
                        entry, expected.revision, context, origin,
                        sharedScope, animationScope,
                        backProgress = {
                            (navigationState.value?.transitionState as? NavigationEventTransitionState.InProgress)
                                ?.latestEvent?.progress
                        },
                        backCancellations = { backCancellations },
                        splitColumn = column,
                        isSplitExpanded = expanded,
                    )
                    CompositionLocalProvider(LocalNavigationEntryScope provides scope) {
                        if (entry.mount == 0L) currentUnavailable(entry) else OahuMount(entry.mount)
                    }
                    // The visible top settles the stack; in two panes that is the detail pane's entry.
                    if (entry == expected.visible(currentTwoPanes).lastOrNull()) {
                        LaunchedEffect(expected, transition, currentTwoPanes) {
                            // Wait for the old scene and shared content before releasing mounts.
                            val isIdle = {
                                val visible = currentStack.visible(currentTwoPanes)
                                visible.lastOrNull() == entry && composed.size == visible.size && visible.all(composed::containsKey) &&
                                    transition.currentState == EnterExitState.Visible &&
                                    transition.targetState == EnterExitState.Visible &&
                                    !transition.isRunning && !sharedScope.isTransitionActive &&
                                    navigationState.value?.transitionState == NavigationEventTransitionState.Idle
                            }
                            snapshotFlow { isIdle() }.first { it }
                            if (currentStack == expected) settled(expected, isIdle)
                        }
                    }
                }
            }
        }
        val entries = rememberDecoratedNavEntries(
            entries = undecorated,
            entryDecorators = listOf(rememberSaveableStateHolderNavEntryDecorator()),
        )
        val compactColumn = stack.compactColumn
        val strategies = remember(compactColumn, twoPanes, splitPlaceholder) {
            listOfNotNull(
                compactColumn?.let { SplitSceneStrategy(twoPanes, it, splitPlaceholder) },
                SinglePaneSceneStrategy(),
            )
        }
        // Nav3 moves an entry shown by both scenes as a shared element. A split's entries change panes on a fold,
        // which must not animate, so a split renders each shared entry in the newer scene only.
        val scenes = rememberSceneState(entries, strategies,
            sharedTransitionScope = sharedScope.takeIf { compactColumn == null }, onBack = onBack)
        val navigation = rememberNavigationEventState(scenes)
        SideEffect { navigationState.value = navigation }
        NavigationBackHandler(
            state = navigation,
            isBackEnabled = allowBackPreview && scenes.currentScene.previousEntries.isNotEmpty(),
            onBackCancelled = { backCancellations++ },
            onBackCompleted = onBack,
        )
        NavDisplay(
            sceneState = scenes,
            navigationEventState = navigation,
            modifier = Modifier.fillMaxSize().let {
                if (animations.sizeTransform == null) it else it.wrapContentSize(Alignment.TopStart)
            },
            sizeTransform = animations.sizeTransform,
            // A fold or unfold is not navigation, and two-pane changes animate inside their panes.
            transitionSpec = {
                splitTransform() ?: run {
                    val transition = animations.transition?.invoke(this, presentationFor(currentPresentation, "replace", origins))
                        ?: defaultTransitionSpec<MountedEntry>()(this)
                    animations.withDisplaySizeHold(this, transition)
                }
            },
            popTransitionSpec = {
                splitTransform() ?: run {
                    val transition = animations.pop?.invoke(this, presentationFor(currentPresentation, "pop", origins))
                        ?: defaultPopTransitionSpec<MountedEntry>()(this)
                    animations.withDisplaySizeHold(this, transition)
                }
            },
            predictivePopTransitionSpec = { edge ->
                splitTransform() ?: run {
                    val transition = animations.predictivePop?.invoke(this, presentationFor(currentPresentation, "pop", origins), edge)
                        ?: defaultPredictivePopTransitionSpec<MountedEntry>()(this, edge)
                    animations.withDisplaySizeHold(this, transition)
                }
            },
        )
    }
}

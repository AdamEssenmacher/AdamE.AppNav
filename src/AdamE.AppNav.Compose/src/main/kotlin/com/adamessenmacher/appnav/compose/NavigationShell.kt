package com.adamessenmacher.appnav.compose

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
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.saveable.rememberSaveableStateHolder
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.navigation3.runtime.NavEntry
import androidx.navigation3.runtime.rememberDecoratedNavEntries
import androidx.navigation3.runtime.rememberSaveableStateHolderNavEntryDecorator
import androidx.navigation3.scene.NavigationBackHandler
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
import androidx.navigationevent.compose.NavigationEventState
import com.adamessenmacher.oahu.compose.OahuMount
import kotlinx.coroutines.flow.first

/** Renders the shell using the original positional controls argument. */
@Composable
public fun AppNavNavigationShell(
    target: NavigationProjection,
    report: (NavigationObservation) -> Unit,
    requestBack: (() -> Unit)?,
    branchLabel: @Composable (NavigationBranch) -> Unit,
    unavailableContent: @Composable (MountedEntry) -> Unit,
    content: @Composable () -> Unit,
) {
    AppNavNavigationShell(target, report, requestBack, branchLabel, unavailableContent, controls = content)
}

/**
 * Render the managed projection as a selected native stack, optionally overlaid by a modal stack.
 * A new revision supersedes any local edits. On Activity recreation the shell starts with the
 * current managed projection, so Nav3's saved state cannot replay obsolete stack keys.
 *
 * Set [requestBack] when AppNav must evaluate application Back policy before changing the UI.
 * In that mode the callback requests a managed navigation operation and its next projection
 * drives the stack change. The callback can also leave the projection unchanged to refuse Back.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
public fun AppNavNavigationShell(
    target: NavigationProjection,
    report: (NavigationObservation) -> Unit,
    requestBack: (() -> Unit)? = null,
    branchLabel: @Composable (NavigationBranch) -> Unit = { Text(it.title) },
    unavailableContent: @Composable (MountedEntry) -> Unit = {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text("Screen unavailable")
        }
    },
    animations: NavigationAnimations = NavigationAnimations(),
    controls: @Composable () -> Unit = {},
) {
    val surface = remember(target.revision) { DisplayedSurface(target) }
    val origins = remember { NavigationOrigins() }
    val branchStates = rememberSaveableStateHolder()
    val retainedBranches = remember { mutableSetOf<String>() }
    SideEffect {
        origins.stage(target)
        val currentBranches = target.branches.mapTo(mutableSetOf(), NavigationBranch::branch)
        (retainedBranches - currentBranches).forEach(branchStates::removeState)
        retainedBranches.retainAll(currentBranches)
        retainedBranches.addAll(currentBranches)
    }
    val branch = surface.branch
    val primary = surface.branches[branch].orEmpty()
    val modal = surface.modal
    val modalId = target.modalId.takeIf { modal.isNotEmpty() }.orEmpty()
    val expectedPrimary = StackRender(target.revision, branch, primary).takeIf { primary.isNotEmpty() }
    val expectedModal = StackRender(target.revision, modalId, modal).takeIf { modal.isNotEmpty() }
    var renderedPrimary by remember { mutableStateOf<StackRender?>(null) }
    var renderedModal by remember { mutableStateOf<StackRender?>(null) }
    val latestReport by rememberUpdatedState(report)
    val observation = NavigationObservation(
        target.revision,
        branch,
        primary.map(MountedEntry::entryId),
        modalId,
        modal.map(MountedEntry::entryId),
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
            target.modalEntries == modal && renderedPrimary == expectedPrimary && renderedModal == expectedModal
        ) {
            origins.prune(target)
            surface.awaitingAcceptance = false
        }
    }
    val backPrimary = {
        if (surface.canNavigate && surface.modal.isEmpty() && surface.branches[surface.branch].orEmpty().size > 1) {
            if (requestBack == null) surface.popPrimary() else requestBack()
        }
    }
    val backModal = {
        if (surface.canNavigate && surface.modal.isNotEmpty()) {
            if (requestBack == null) surface.popModal() else requestBack()
        }
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
                            enabled = surface.canNavigate && modal.isEmpty(),
                            onClick = { surface.selectBranch(tab.branch) },
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
                    MountedStack(expectedPrimary, target.transition, animations, origins, backPrimary, unavailableContent) {
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
            Dialog(
                onDismissRequest = backModal,
                properties = DialogProperties(dismissOnClickOutside = false),
            ) {
                Surface(shape = MaterialTheme.shapes.large) {
                    Column(Modifier.fillMaxWidth()) {
                        TextButton(
                            onClick = backModal,
                            enabled = surface.canNavigate,
                        ) {
                            Text(if (modal.size > 1) "Back" else "Dismiss")
                        }
                        Box(Modifier.fillMaxWidth().fillMaxHeight(0.8f)) {
                            MountedStack(expectedModal, target.transition, animations, origins, backModal, unavailableContent) {
                                renderedModal = it
                            }
                        }
                    }
                }
            }
        }
    }
}

private class DisplayedSurface(target: NavigationProjection) {
    var branch by mutableStateOf(target.selectedBranch)
        private set
    var branches by mutableStateOf(target.branches.associate { it.branch to it.entries })
        private set
    var modal by mutableStateOf(target.modalEntries)
        private set
    var hasReported by mutableStateOf(false)
    var awaitingAcceptance by mutableStateOf(false)
    val canNavigate: Boolean get() = hasReported && !awaitingAcceptance

    // The presenter validates each native gesture against the last accepted surface. A tab switch
    // followed immediately by Back must report two changes, even if Compose can coalesce frames.
    fun selectBranch(selected: String) {
        if (canNavigate && modal.isEmpty() && selected != branch) {
            awaitingAcceptance = true
            branch = selected
        }
    }

    fun popPrimary() {
        val entries = branches[branch].orEmpty()
        if (canNavigate && modal.isEmpty() && entries.size > 1) {
            awaitingAcceptance = true
            branches = branches + (branch to entries.dropLast(1))
        }
    }

    fun popModal() {
        if (canNavigate && modal.isNotEmpty()) {
            awaitingAcceptance = true
            modal = modal.dropLast(1)
        }
    }
}

private data class StackRender(
    val revision: Long,
    val stackId: String,
    val entries: List<MountedEntry>,
)

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
    onBack: () -> Unit,
    unavailableContent: @Composable (MountedEntry) -> Unit,
    onSettled: (StackRender) -> Unit,
) {
    var lastReset by rememberSaveable { mutableLongStateOf(0) }
    val history = remember { SceneRenderHistory() }
    val newRevision = history.shownRevision?.let { it != stack.revision } == true
    val resetScene = newRevision && presentation.resetsOrigins &&
        (history.settledStack?.entries != stack.entries || !history.isStillSettled())
    val epoch = if (resetScene) stack.revision else lastReset
    SideEffect {
        history.shownRevision = stack.revision
        lastReset = epoch
    }
    // Rebuild a changed maintenance scene without using obsolete animation metadata. Each reset
    // owns a distinct NavEntry saveable holder while the old and new compositions exchange places.
    key(epoch) {
        StackScene(stack, presentation, animations, origins, onBack, unavailableContent) { seen, isStillSettled ->
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
    onBack: () -> Unit,
    unavailableContent: @Composable (MountedEntry) -> Unit,
    onSettled: (StackRender, () -> Boolean) -> Unit,
) {
    val currentStack by rememberUpdatedState(stack)
    val currentPresentation by rememberUpdatedState(presentation)
    val currentUnavailable by rememberUpdatedState(unavailableContent)
    val settled by rememberUpdatedState(onSettled)
    SharedTransitionLayout {
        val sharedScope = this
        val composed = remember { mutableStateMapOf<MountedEntry, Int>() }
        var backCancellations by remember { mutableLongStateOf(0L) }
        val navigationState = remember { mutableStateOf<NavigationEventState<SceneInfo<MountedEntry>>?>(null) }
        val undecorated = remember(stack.entries, presentation, animations) {
            stack.entries.map { entry ->
                val metadata = animations.metadataFor(entry, presentation) + (AppNavMountedEntry to entry)
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
                    val scope = NavigationEntryScope(
                        entry, expected.revision, context, origins.pushFor(entry, context, expected.entries),
                        sharedScope, animationScope,
                        backProgress = {
                            (navigationState.value?.transitionState as? NavigationEventTransitionState.InProgress)
                                ?.latestEvent?.progress
                        },
                        backCancellations = { backCancellations },
                    )
                    CompositionLocalProvider(LocalNavigationEntryScope provides scope) {
                        if (entry.mount == 0L) currentUnavailable(entry) else OahuMount(entry.mount)
                    }
                    if (entry == expected.entries.lastOrNull()) {
                        LaunchedEffect(expected, transition) {
                            // Wait for the old scene and shared content before releasing mounts.
                            val isIdle = {
                                currentStack.entries.lastOrNull() == entry &&
                                    composed.size == 1 && composed.containsKey(entry) &&
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
        val strategies = remember { listOf(SinglePaneSceneStrategy<MountedEntry>()) }
        val scenes = rememberSceneState(entries, strategies, sharedTransitionScope = sharedScope, onBack = onBack)
        val navigation = rememberNavigationEventState(scenes)
        SideEffect { navigationState.value = navigation }
        NavigationBackHandler(
            scenes, navigation, onBackCancelled = { backCancellations++ }, onBackCompleted = onBack,
        )
        NavDisplay(
            sceneState = scenes,
            navigationEventState = navigation,
            modifier = Modifier.fillMaxSize().let {
                if (animations.sizeTransform == null) it else it.wrapContentSize(Alignment.TopStart)
            },
            sizeTransform = animations.sizeTransform,
            transitionSpec = {
                val transition = animations.transition?.invoke(this, presentationFor(currentPresentation, "replace", origins))
                    ?: defaultTransitionSpec<MountedEntry>()(this)
                animations.withDisplaySizeHold(this, transition)
            },
            popTransitionSpec = {
                val transition = animations.pop?.invoke(this, presentationFor(currentPresentation, "pop", origins))
                    ?: defaultPopTransitionSpec<MountedEntry>()(this)
                animations.withDisplaySizeHold(this, transition)
            },
            predictivePopTransitionSpec = { edge ->
                val transition = animations.predictivePop?.invoke(this, presentationFor(currentPresentation, "pop", origins), edge)
                    ?: defaultPredictivePopTransitionSpec<MountedEntry>()(this, edge)
                animations.withDisplaySizeHold(this, transition)
            },
        )
    }
}

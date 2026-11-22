package com.adamessenmacher.appnav.probe

import androidx.compose.animation.EnterExitState
import androidx.compose.animation.SizeTransform
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.snapshotFlow
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.navigation3.ui.NavDisplay
import androidx.navigationevent.NavigationEvent
import com.adamessenmacher.appnav.compose.LocalNavigationEntryScope
import com.adamessenmacher.appnav.compose.NavigationAnimations
import com.adamessenmacher.appnav.compose.NavigationPresentationTransition

private const val GAME_COVER = "game-cover"
private const val SCENE_RESIZE_MILLIS = 1200
private val CoverColor = Color(0xFFFF5C2C)
private val CoverShape = RoundedCornerShape(16.dp)

internal class GameActions(val command: (String) -> Unit, val trace: (String) -> Unit) {
    var pushRevision: Long? = null
    var previewRevision: Long? = null
    var cancellationsBeforePreview: Long = 0
    var predictiveTransformTraced = false
}
internal val LocalGameActions = staticCompositionLocalOf<GameActions?> { null }

private fun NavigationPresentationTransition.isGamePush() =
    operation == "push" && sourceKey == GAME_COVER && destinationScreen == "game"

private fun NavigationPresentationTransition.isGameReturn() =
    operation == "pop" && sourceKey == GAME_COVER && sourceScreen == "game" && destinationScreen == "home-detail"

internal fun gameAnimations(actions: GameActions) = NavigationAnimations(
    sizeTransform = SizeTransform(clip = false) { initial, target ->
        if (initial.height > 0 && target.height > 0 && initial != target) {
            actions.trace("display size transform from=${initial.width}x${initial.height} to=${target.width}x${target.height}")
        }
        // Outlast the visibility fades and the cover animation so the test checks Nav3 settlement.
        tween(SCENE_RESIZE_MILLIS)
    },
    entryMetadata = { entry, _ ->
        if (entry.entryId == "game") {
            // A null entry override must still reach the shell's forward and predictive specs.
            NavDisplay.transitionSpec { null } +
                NavDisplay.predictivePopTransitionSpec { null } +
                NavDisplay.popTransitionSpec {
                    fadeIn(tween(350)) togetherWith fadeOut(tween(350))
                }
        } else {
            emptyMap()
        }
    },
    transition = { context ->
        if (context.isGamePush()) fadeIn(tween(220)) togetherWith fadeOut(tween(560)) else null
    },
    pop = { context ->
        if (context.isGameReturn()) fadeIn(tween(350)) togetherWith fadeOut(tween(350)) else null
    },
    predictivePop = { context, edge ->
        if (context.isGameReturn()) {
            if (!actions.predictiveTransformTraced) {
                actions.predictiveTransformTraced = true
                val side = when (edge) {
                    NavigationEvent.EDGE_LEFT -> "left"
                    NavigationEvent.EDGE_RIGHT -> "right"
                    else -> "none"
                }
                actions.trace("game predictive transform from=${context.sourceEntryId} to=${context.destinationEntryId} key=${context.sourceKey} edge=$side")
            }
            fadeIn(tween(400)) togetherWith fadeOut(tween(400))
        } else null
    },
)

@Composable
internal fun GameControls() {
    val actions = LocalGameActions.current
    DisposableEffect(actions) {
        onDispose { actions?.trace?.invoke("game origin scene disposed") }
    }
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        TextButton(onClick = { actions?.command?.invoke("open-game") }) {
            GameCover(expanded = false)
            Text("Play Game", Modifier.padding(start = 12.dp))
        }
        TextButton(onClick = { actions?.command?.invoke("recreate-game") }) {
            Text("Recreate during Game push")
        }
    }
}

@Composable
internal fun GameCover(expanded: Boolean) {
    val entry = LocalNavigationEntryScope.current
    val actions = LocalGameActions.current
    val dimensions = Modifier.size(if (expanded) 220.dp else 68.dp)
    val sourceKey = if (expanded) entry?.origin?.takeIf { it.isGamePush() }?.sourceKey else GAME_COVER
    val modifier = if (entry == null || sourceKey == null) dimensions else with(entry.sharedTransitionScope) {
        val cover = rememberSharedContentState(sourceKey)
        if (expanded && actions != null) {
            val currentEntry by rememberUpdatedState(entry)
            val sceneLifecycle by LocalLifecycleOwner.current.lifecycle.currentStateFlow.collectAsState()
            LaunchedEffect(entry.revision, cover, actions) {
                snapshotFlow {
                    val scope = currentEntry
                    val transition = scope.animatedVisibilityScope.transition
                    val entering = transition.currentState != EnterExitState.Visible &&
                        transition.targetState == EnterExitState.Visible
                    CoverMotion(
                        isTransitionActive && cover.isMatchFound, scope.predictiveBackProgress,
                        scope.predictiveBackCancellationCount, sceneLifecycle == Lifecycle.State.RESUMED, entering,
                    )
                }.collect { (matched, progress, cancellations, resumed, entering) ->
                    if (actions.previewRevision == entry.revision && progress == null && resumed &&
                        cancellations > actions.cancellationsBeforePreview
                    ) {
                        actions.previewRevision = null
                        actions.trace("game shared predictive settled revision=${entry.revision}")
                    }
                    if (matched && progress != null && progress > 0f && actions.previewRevision != entry.revision) {
                        actions.previewRevision = entry.revision
                        actions.cancellationsBeforePreview = cancellations
                        actions.trace("game shared predictive preview revision=${entry.revision} progress=$progress")
                    }
                    if (matched && progress == null && actions.pushRevision != entry.revision &&
                        currentEntry.transition.isGamePush() && entering
                    ) {
                        actions.pushRevision = entry.revision
                        actions.predictiveTransformTraced = false
                        actions.trace("game shared active operation=push revision=${entry.revision}")
                        actions.command("game-push-in-progress")
                    }
                }
            }
        }
        // The artwork is the same on both screens. The native SharedTransitionLayout animates
        // its bounds while Navigation 3 owns entry insertion and predictive Back progress.
        Modifier.sharedElement(cover, entry.animatedVisibilityScope).then(dimensions)
    }
    Box(
        modifier.clip(CoverShape).background(CoverColor),
        contentAlignment = Alignment.Center,
    ) {
        Text("GAME", color = Color(0xFF10253B))
    }
}

private data class CoverMotion(
    val matched: Boolean,
    val progress: Float?,
    val cancellations: Long,
    val resumed: Boolean,
    val entering: Boolean,
)

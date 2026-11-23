package com.adamessenmacher.appnav.compose

import androidx.compose.animation.AnimatedContentTransitionScope
import androidx.compose.animation.ContentTransform
import androidx.compose.animation.EnterTransition
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleOut
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.width
import androidx.compose.material3.VerticalDivider
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import androidx.navigation3.runtime.NavEntry
import androidx.navigation3.runtime.get
import androidx.navigation3.scene.Scene
import androidx.navigation3.scene.SceneStrategy
import androidx.navigation3.scene.SceneStrategyScope
import androidx.navigation3.ui.LocalNavAnimatedContentScope
import androidx.navigation3.ui.NavDisplay
import androidx.navigationevent.NavigationEvent

/** The list pane's width when a split shows both columns. The detail pane takes the rest. */
internal val SplitListPaneWidth = 360.dp

/** A window at least this wide (expanded width) and this tall (not compact height) shows both columns. */
internal val SplitMinimumWindow = DpSize(840.dp, 480.dp)

internal fun showsBothColumns(window: DpSize): Boolean =
    window.width >= SplitMinimumWindow.width && window.height >= SplitMinimumWindow.height

internal const val AppNavSplitColumn = "com.adamessenmacher.appnav.compose.splitColumn"

internal val NavEntry<*>.splitColumn: NavigationSplitColumn?
    get() = metadata[AppNavSplitColumn] as? NavigationSplitColumn

/** What a native Back does to a split. It follows AppNav's planned Back, so a native pop never surprises core. */
internal enum class SplitBack { None, PopSecondary, CloseSecondary, PopPrimary }

/**
 * The Back a split takes from the column on top. In two panes Back at the secondary root is not intercepted while
 * the primary column is at its root, so the system leaves the app, as from Home on a phone. AppNav's planned Back
 * would close the column there; this is a deliberate deviation for Android's two-pane layout.
 */
internal fun splitBack(primary: Int, secondary: Int, compact: NavigationSplitColumn, twoPanes: Boolean): SplitBack =
    when {
        compact == NavigationSplitColumn.Secondary && secondary > 0 -> when {
            secondary > 1 -> SplitBack.PopSecondary
            !twoPanes || primary > 1 -> SplitBack.CloseSecondary
            else -> SplitBack.None
        }
        // A pop in the primary column also closes a default secondary column, as AppNav plans it.
        primary > 1 -> SplitBack.PopPrimary
        else -> SplitBack.None
    }

/** Which panes change between two two-pane scenes, and whether the change is a predictive Back preview. */
internal class SplitMotion {
    var listChanges by mutableStateOf(false)
    var detailChanges by mutableStateOf(false)
    var predictive by mutableStateOf(false)

    fun update(from: SplitTops?, to: SplitTops?, inPredictiveBack: Boolean) {
        val lists = from?.list != to?.list
        val details = from?.detail != to?.detail
        if (listChanges != lists) listChanges = lists
        if (detailChanges != details) detailChanges = details
        if (predictive != inPredictiveBack) predictive = inPredictiveBack
    }
}

internal data class SplitTops(val list: Any, val detail: Any?)

internal val Scene<MountedEntry>.splitTops: SplitTops?
    get() = (this as? TwoPaneSplitScene)?.let { SplitTops(it.list.contentKey, it.detail?.contentKey) }

/** The flattened back stack's arrangement: the primary column's entries, then the secondary column's. */
internal class SplitSceneStrategy(
    private val twoPanes: Boolean,
    private val compactColumn: NavigationSplitColumn,
    private val motion: SplitMotion,
    private val placeholder: @Composable () -> Unit,
) : SceneStrategy<MountedEntry> {
    override fun SceneStrategyScope<MountedEntry>.calculateScene(entries: List<NavEntry<MountedEntry>>): Scene<MountedEntry>? {
        val primary = entries.filter { it.splitColumn == NavigationSplitColumn.Primary }
        val secondary = entries.filter { it.splitColumn == NavigationSplitColumn.Secondary }
        if (primary.isEmpty()) return null
        val compact = if (secondary.isEmpty()) NavigationSplitColumn.Primary else compactColumn
        val previous = when (splitBack(primary.size, secondary.size, compact, twoPanes)) {
            SplitBack.PopSecondary -> entries.dropLast(1)
            SplitBack.CloseSecondary -> primary
            SplitBack.PopPrimary -> primary.dropLast(1)
            SplitBack.None -> emptyList()
        }
        if (twoPanes) return TwoPaneSplitScene(primary.last(), secondary.lastOrNull(), previous, motion, placeholder)
        val top = if (compact == NavigationSplitColumn.Secondary) secondary.last() else primary.last()
        // A default secondary column under a primary column on top stays in the stack, unrendered, so its saved
        // state survives until the window shows it again.
        return OnePaneSplitScene(top.contentKey, top, previous)
    }
}

/** One pane: the column on top's top entry, as the phone stack shows it. */
internal data class OnePaneSplitScene(
    override val key: Any,
    val entry: NavEntry<MountedEntry>,
    override val previousEntries: List<NavEntry<MountedEntry>>,
) : Scene<MountedEntry> {
    override val entries: List<NavEntry<MountedEntry>> = listOf(entry)
    override val metadata: Map<String, Any> get() = layoutGuarded(entry.metadata)
    override val content: @Composable () -> Unit = { entry.Content() }
}

/** Two panes: the primary column's top beside the secondary column's top, or the app's placeholder. */
internal class TwoPaneSplitScene(
    val list: NavEntry<MountedEntry>,
    val detail: NavEntry<MountedEntry>?,
    override val previousEntries: List<NavEntry<MountedEntry>>,
    private val motion: SplitMotion,
    private val placeholder: @Composable () -> Unit,
) : Scene<MountedEntry> {
    override val key: Any = SplitTops(list.contentKey, detail?.contentKey)
    override val entries: List<NavEntry<MountedEntry>> = listOfNotNull(list, detail)
    // Navigation 3 swaps whole scenes. Panes animate themselves, so the scene itself neither enters nor leaves.
    override val metadata: Map<String, Any> = PaneTransitions
    override val content: @Composable () -> Unit = {
        Row(Modifier.fillMaxSize()) {
            Pane(Modifier.width(SplitListPaneWidth), motion.listChanges) { list.Content() }
            VerticalDivider()
            Pane(Modifier.weight(1f), motion.detailChanges) { detail?.Content() ?: placeholder() }
        }
    }

    @Composable
    private fun Pane(modifier: Modifier, changes: Boolean, content: @Composable () -> Unit) {
        val scope = LocalNavAnimatedContentScope.current
        val enter = when {
            !changes -> EnterTransition.None
            motion.predictive -> fadeIn(spring(dampingRatio = 1f, stiffness = 1600f))
            else -> fadeIn(tween(PaneMotionMillis))
        }
        val exit = when {
            !changes -> ExitTransition.None
            motion.predictive -> scaleOut(targetScale = 0.7f)
            else -> fadeOut(tween(PaneMotionMillis))
        }
        Box(modifier.fillMaxHeight().clipToBounds()) {
            Box(with(scope) { Modifier.fillMaxSize().animateEnterExit(enter, exit) }) { content() }
        }
    }

    override fun equals(other: Any?): Boolean = other is TwoPaneSplitScene && list == other.list &&
        detail == other.detail && previousEntries == other.previousEntries

    override fun hashCode(): Int = (list.hashCode() * 31 + (detail?.hashCode() ?: 0)) * 31 + previousEntries.hashCode()
}

// Navigation 3's own defaults, applied to the pane that changes.
private const val PaneMotionMillis = 700

internal fun AnimatedContentTransitionScope<*>.isSplitLayoutChange(): Boolean {
    val initial = initialState
    val target = targetState
    return (initial is OnePaneSplitScene || initial is TwoPaneSplitScene ||
        target is OnePaneSplitScene || target is TwoPaneSplitScene) && initial?.let { it::class } != target?.let { it::class }
}

internal fun AnimatedContentTransitionScope<*>.isPaneChange(): Boolean =
    initialState is TwoPaneSplitScene && targetState is TwoPaneSplitScene

private val Instant = ContentTransform(EnterTransition.None, ExitTransition.None)

/** A fold or unfold swaps the layout at once; a pane change keeps both scenes while the panes animate. */
internal fun AnimatedContentTransitionScope<*>.splitTransform(): ContentTransform? = when {
    isSplitLayoutChange() -> Instant
    isPaneChange() -> ContentTransform(EnterTransition.None, ExitTransition.KeepUntilTransitionsFinished)
    else -> null
}

private val PaneTransitions: Map<String, Any> =
    NavDisplay.transitionSpec { splitTransform() } +
        NavDisplay.popTransitionSpec { splitTransform() } +
        NavDisplay.predictivePopTransitionSpec { _ -> splitTransform() }

// An entry's own transition must not animate a fold or unfold, which is not navigation.
private fun layoutGuarded(metadata: Map<String, Any>): Map<String, Any> {
    val forward: (AnimatedContentTransitionScope<Scene<*>>.() -> ContentTransform?)? = metadata[NavDisplay.TransitionKey]
    val pop: (AnimatedContentTransitionScope<Scene<*>>.() -> ContentTransform?)? = metadata[NavDisplay.PopTransitionKey]
    val predictive: (AnimatedContentTransitionScope<Scene<*>>.(@NavigationEvent.SwipeEdge Int) -> ContentTransform?)? =
        metadata[NavDisplay.PredictivePopTransitionKey]
    return buildMap {
        putAll(metadata)
        putAll(NavDisplay.transitionSpec { splitTransform() ?: forward?.invoke(this) })
        putAll(NavDisplay.popTransitionSpec { splitTransform() ?: pop?.invoke(this) })
        putAll(NavDisplay.predictivePopTransitionSpec { edge -> splitTransform() ?: predictive?.invoke(this, edge) })
    }
}

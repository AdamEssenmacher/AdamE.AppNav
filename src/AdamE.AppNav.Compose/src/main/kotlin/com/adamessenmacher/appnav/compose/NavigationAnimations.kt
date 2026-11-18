package com.adamessenmacher.appnav.compose

import androidx.compose.animation.AnimatedContentTransitionScope
import androidx.compose.animation.AnimatedVisibilityScope
import androidx.compose.animation.ContentTransform
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.SharedTransitionScope
import androidx.compose.animation.SizeTransform
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.navigation3.runtime.NavEntry
import androidx.navigation3.runtime.get
import androidx.navigation3.scene.Scene
import androidx.navigation3.ui.NavDisplay
import androidx.navigationevent.NavigationEvent

/** Optional native Nav3 animations. Returning null uses Nav3's normal animation. */
public class NavigationAnimations(
    /** Nav3's display-wide size animation. A non-null value lets the display measure each scene at its preferred size. */
    public val sizeTransform: SizeTransform?,
    public val entryMetadata: (MountedEntry, NavigationPresentationTransition) -> Map<String, Any> = { _, _ ->
        emptyMap()
    },
    public val transition: (AnimatedContentTransitionScope<Scene<MountedEntry>>.(NavigationPresentationTransition) -> ContentTransform?)? = null,
    public val pop: (AnimatedContentTransitionScope<Scene<MountedEntry>>.(NavigationPresentationTransition) -> ContentTransform?)? = null,
    public val predictivePop: (AnimatedContentTransitionScope<Scene<MountedEntry>>.(
        NavigationPresentationTransition, @NavigationEvent.SwipeEdge Int,
    ) -> ContentTransform?)? = null,
) {
    public constructor(
        entryMetadata: (MountedEntry, NavigationPresentationTransition) -> Map<String, Any> = { _, _ -> emptyMap() },
        transition: (AnimatedContentTransitionScope<Scene<MountedEntry>>.(NavigationPresentationTransition) -> ContentTransform?)? = null,
        pop: (AnimatedContentTransitionScope<Scene<MountedEntry>>.(NavigationPresentationTransition) -> ContentTransform?)? = null,
        predictivePop: (AnimatedContentTransitionScope<Scene<MountedEntry>>.(
            NavigationPresentationTransition, @NavigationEvent.SwipeEdge Int,
        ) -> ContentTransform?)? = null,
    ) : this(null, entryMetadata, transition, pop, predictivePop)

    public constructor() : this(sizeTransform = null)
}

// The display size animation lives on the parent AnimatedContent transition. Keep the
// outgoing entry mounted until that transition completes so the stack's witness can settle.
internal fun NavigationAnimations.withDisplaySizeHold(
    scope: AnimatedContentTransitionScope<*>,
    transform: ContentTransform,
): ContentTransform = if (sizeTransform == null) transform else with(scope) {
    ContentTransform(
        transform.targetContentEnter,
        transform.initialContentExit + ExitTransition.KeepUntilTransitionsFinished,
        transform.targetContentZIndex,
        transform.sizeTransform,
    )
}

internal fun NavigationAnimations.metadataFor(
    entry: MountedEntry,
    presentation: NavigationPresentationTransition,
): Map<String, Any> {
    val metadata = entryMetadata(entry, presentation)
    if (sizeTransform == null) return metadata

    val forward: (AnimatedContentTransitionScope<Scene<*>>.() -> ContentTransform?)? =
        metadata[NavDisplay.TransitionKey]
    val pop: (AnimatedContentTransitionScope<Scene<*>>.() -> ContentTransform?)? =
        metadata[NavDisplay.PopTransitionKey]
    val predictive = metadata[NavDisplay.PredictivePopTransitionKey]
    return buildMap {
        putAll(metadata)
        if (forward != null) {
            putAll(NavDisplay.transitionSpec { forward(this)?.let { withDisplaySizeHold(this, it) } })
        }
        if (pop != null) {
            putAll(NavDisplay.popTransitionSpec { pop(this)?.let { withDisplaySizeHold(this, it) } })
        }
        if (predictive != null) {
            putAll(NavDisplay.predictivePopTransitionSpec { edge ->
                predictive(this, edge)?.let { withDisplaySizeHold(this, it) }
            })
        }
    }
}

/** Native context available to an OAHU-mounted composable inside an AppNav stack. */
public class NavigationEntryScope internal constructor(
    public val entry: MountedEntry,
    public val revision: Long,
    public val transition: NavigationPresentationTransition,
    /** The push that introduced this mounted occurrence, while its source mount is still live. */
    public val origin: NavigationPresentationTransition?,
    public val sharedTransitionScope: SharedTransitionScope,
    public val animatedVisibilityScope: AnimatedVisibilityScope,
    private val backProgress: () -> Float?,
    private val backCancellations: () -> Long,
) {
    /** 0–1 while Android is dispatching predictive Back. The route remains committed during a preview. */
    public val predictiveBackProgress: Float? get() = backProgress()

    /** Number of cancellations reported by Nav3's Back handler in this stack scene. */
    public val predictiveBackCancellationCount: Long get() = backCancellations()

}

public val LocalNavigationEntryScope = staticCompositionLocalOf<NavigationEntryScope?> { null }

internal const val AppNavMountedEntry = "com.adamessenmacher.appnav.compose.mountedEntry"
private val maintenanceOperations = setOf("initial", "refresh", "rollback", "reattach")

internal val NavigationPresentationTransition.resetsOrigins: Boolean
    get() = operation in maintenanceOperations

internal class NavigationOrigins {
    private data class Origin(val source: MountedEntry, val push: NavigationPresentationTransition)
    private val origins = mutableStateMapOf<MountedEntry, Origin>()
    private var resetRevision = Long.MIN_VALUE

    fun stage(target: NavigationProjection) {
        val transition = target.transition
        if (transition.resetsOrigins && resetRevision != target.revision) {
            origins.clear()
            resetRevision = target.revision
        }
        val predecessors = predecessors(target)
        // Existing destinations must still be adjacent to the exact source. A departing destination
        // keeps its origin until Nav3 finishes animating it out.
        origins.entries.removeAll { (entry, origin) ->
            entry in predecessors && predecessors[entry] != origin.source
        }
        if (transition.operation != "push") return
        val stack = target.modalEntries.takeIf { it.isNotEmpty() }
            ?: target.branches.firstOrNull { it.branch == target.selectedBranch }?.entries.orEmpty()
        val (source, destination) = transition.matchingPush(stack) ?: return
        origins[destination] = Origin(source, transition)
    }

    fun prune(target: NavigationProjection) {
        val predecessors = predecessors(target)
        origins.entries.removeAll { (entry, origin) -> predecessors[entry] != origin.source }
    }

    fun pushFor(
        destination: MountedEntry,
        projection: NavigationPresentationTransition,
        stack: List<MountedEntry>,
    ): NavigationPresentationTransition? {
        if (projection.resetsOrigins) return null
        if (projection.matchingPush(stack)?.second == destination) return projection
        val origin = origins[destination] ?: return null
        val index = stack.indexOf(destination)
        return origin.push.takeIf { index < 0 || stack.getOrNull(index - 1) == origin.source }
    }

    fun reverse(from: NavEntry<MountedEntry>?, to: NavEntry<MountedEntry>?): NavigationPresentationTransition? {
        val originalDestination = from?.metadata?.get(AppNavMountedEntry) as? MountedEntry ?: return null
        val originalSource = to?.metadata?.get(AppNavMountedEntry) as? MountedEntry ?: return null
        val origin = origins[originalDestination]?.takeIf { it.source == originalSource } ?: return null
        return NavigationPresentationTransition(
            "pop", originalDestination.entryId, originalSource.entryId, origin.push.sourceKey,
            origin.push.destinationScreen, origin.push.sourceScreen,
        )
    }

    private fun predecessors(target: NavigationProjection): Map<MountedEntry, MountedEntry?> = buildMap {
        (target.branches.map(NavigationBranch::entries) + listOf(target.modalEntries)).forEach { stack ->
            stack.forEachIndexed { index, entry -> put(entry, stack.getOrNull(index - 1)) }
        }
    }
}

private fun NavigationPresentationTransition.matchingPush(
    stack: List<MountedEntry>,
): Pair<MountedEntry, MountedEntry>? {
    if (operation != "push") return null
    val source = stack.getOrNull(stack.lastIndex - 1) ?: return null
    val destination = stack.last()
    return (source to destination).takeIf {
        source.mount != 0L && destination.mount != 0L &&
            source.entryId == sourceEntryId && destination.entryId == destinationEntryId
    }
}

internal fun AnimatedContentTransitionScope<Scene<MountedEntry>>.presentationFor(
    projection: NavigationPresentationTransition,
    fallbackOperation: String,
    origins: NavigationOrigins,
): NavigationPresentationTransition {
    val from = initialState.entries.lastOrNull()
    val to = targetState.entries.lastOrNull()
    val source = from?.contentKey as? String ?: ""
    val destination = to?.contentKey as? String ?: ""
    val inverse = if (fallbackOperation == "pop") origins.reverse(from, to) else null
    if (projection.sourceEntryId == source && projection.destinationEntryId == destination &&
        (fallbackOperation != "pop" || projection.operation == "pop")
    ) {
        return projection.copy(
            sourceKey = projection.sourceKey.ifEmpty { inverse?.sourceKey.orEmpty() },
            sourceScreen = projection.sourceScreen.ifEmpty { inverse?.sourceScreen.orEmpty() },
            destinationScreen = projection.destinationScreen.ifEmpty { inverse?.destinationScreen.orEmpty() },
        )
    }

    return inverse ?: NavigationPresentationTransition(fallbackOperation, source, destination)
}

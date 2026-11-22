package com.adamessenmacher.appnav.compose

/** An AppNav route occurrence and its OAHU mount in the current Activity. */
public data class MountedEntry(val entryId: String, val mount: Long)

public data class NavigationBranch(
    val branch: String,
    val title: String,
    val entries: List<MountedEntry>,
)

/** Immutable snapshot of the selected branch, retained branch stacks and optional modal stack. */
public data class NavigationProjection(
    val revision: Long,
    val selectedBranch: String,
    val isBranchHost: Boolean,
    val branches: List<NavigationBranch>,
    val modalId: String,
    val modalEntries: List<MountedEntry>,
    val transition: NavigationPresentationTransition = NavigationPresentationTransition(),
)

/** Presentation context for the frame. Empty entry IDs, screen names and source key are absent. */
public data class NavigationPresentationTransition(
    val operation: String = "",
    val sourceEntryId: String = "",
    val destinationEntryId: String = "",
    val sourceKey: String = "",
    val sourceScreen: String = "",
    val destinationScreen: String = "",
)

/** The selected native stacks observed after Nav3 finishes the transition. */
public data class NavigationObservation(
    val revision: Long,
    val branch: String,
    val entries: List<String>,
    val modalId: String,
    val modalEntries: List<String>,
)

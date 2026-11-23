package com.adamessenmacher.appnav.compose

/** An AppNav route occurrence and its OAHU mount in the current Activity. */
public data class MountedEntry(val entryId: String, val mount: Long)

public data class NavigationBranch(
    val branch: String,
    val title: String,
    val entries: List<MountedEntry>,
)

/**
 * Immutable snapshot of the selected branch, retained branch stacks and optional modal stack.
 *
 * A window whose root is a split sets [split]. Its [branches] then hold only the primary column, which
 * [selectedBranch] names, and [isBranchHost] is false.
 */
public data class NavigationProjection(
    val revision: Long,
    val selectedBranch: String,
    val isBranchHost: Boolean,
    val branches: List<NavigationBranch>,
    val modalId: String,
    val modalEntries: List<MountedEntry>,
    val transition: NavigationPresentationTransition = NavigationPresentationTransition(),
    val split: NavigationSplit? = null,
)

/** One column of a split. [wireName] is the column's name on the OAHU wire. */
public enum class NavigationSplitColumn(public val wireName: String) {
    Primary("primary"),
    Secondary("secondary");

    public companion object {
        /** The column named on the wire, or null for an empty name, which means the window has no split. */
        public fun fromWireName(name: String): NavigationSplitColumn? = when (name) {
            "" -> null
            "primary" -> Primary
            "secondary" -> Secondary
            else -> throw IllegalArgumentException("Unknown split column '$name'.")
        }
    }
}

/**
 * A split at the window root. The projection's only branch is the primary column. [secondaryEntries] is the
 * secondary column's stack, empty when nothing is selected. [compactColumn] names the column a one-pane window
 * shows on top: [NavigationSplitColumn.Secondary] shows the secondary stack above the primary one.
 */
public data class NavigationSplit(
    val id: String,
    val secondaryId: String,
    val secondaryEntries: List<MountedEntry>,
    val compactColumn: NavigationSplitColumn,
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

/**
 * The selected native stacks observed after Nav3 finishes the transition.
 *
 * For a split, [branch] and [entries] are the primary column and [secondaryEntries] the secondary column.
 * [compactColumn] is the column a one-pane window shows on top; a two-pane window reports the staged one, so
 * folding and unfolding never change a reading. It is null without a split.
 */
public data class NavigationObservation(
    val revision: Long,
    val branch: String,
    val entries: List<String>,
    val modalId: String,
    val modalEntries: List<String>,
    val secondaryEntries: List<String> = emptyList(),
    val compactColumn: NavigationSplitColumn? = null,
)

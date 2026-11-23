/// The projection of one AppNav window, staged under a presentation revision.
///
/// A window whose root is a split sets `split`. Its `branches` then hold only the primary column, which
/// `selectedBranch` names, and `isBranchHost` is false.
public struct NavigationProjection: Equatable, Sendable {
    public let revision: Int64
    public let selectedBranch: String
    public let isBranchHost: Bool
    public let branches: [NavigationBranchProjection]
    public let modal: ModalProjection?
    public let transition: NavigationTransitionContext?
    public let split: SplitProjection?

    public init(revision: Int64, selectedBranch: String, isBranchHost: Bool,
                branches: [NavigationBranchProjection], modal: ModalProjection?,
                transition: NavigationTransitionContext? = nil, split: SplitProjection? = nil) {
        self.revision = revision
        self.selectedBranch = selectedBranch
        self.isBranchHost = isBranchHost
        self.branches = branches
        self.modal = modal
        self.transition = transition
        self.split = split
    }
}

/// One column of a split.
public enum NavigationSplitColumn: String, Sendable {
    case primary, secondary
}

/// A split at the window root, rendered with `NavigationSplitView`. The projection's only branch is the primary
/// column. `compactColumn` names the column a collapsed window shows on top: `.secondary` shows the secondary
/// stack above the primary one.
public struct SplitProjection: Equatable, Sendable {
    public let id: String
    public let secondary: NavigationBranchProjection?
    public let compactColumn: NavigationSplitColumn

    public init(id: String, secondary: NavigationBranchProjection?, compactColumn: NavigationSplitColumn) {
        self.id = id
        self.secondary = secondary
        self.compactColumn = compactColumn
    }
}

public struct NavigationBranchProjection: Equatable, Sendable {
    public let id: String
    public let title: String
    public let entries: [NavigationEntry]

    public init(id: String, title: String, entries: [NavigationEntry]) {
        self.id = id
        self.title = title
        self.entries = entries
    }
}

public struct ModalProjection: Equatable, Sendable {
    public let id: String
    public let entries: [NavigationEntry]

    public init(id: String, entries: [NavigationEntry]) {
        self.id = id
        self.entries = entries
    }
}

public struct NavigationEntry: Hashable, Sendable {
    public let id: String
    public let mount: Int64

    public init(id: String, mount: Int64) {
        self.id = id
        self.mount = mount
    }
}

/// A settled reading of the visible native navigation graph.
///
/// For a split, `branch` and `entries` are the primary column and `secondaryEntries` the secondary column.
/// `compactColumn` is the column a collapsed window shows on top; a window showing both columns reports the
/// staged one, so folding and unfolding never change a reading. It is nil without a split.
public struct NavigationReading: Equatable, Sendable {
    public let revision: Int64
    public let branch: String
    public let entries: [String]
    public let modalID: String?
    public let modalEntries: [String]
    public let secondaryEntries: [String]
    public let compactColumn: NavigationSplitColumn?

    public init(revision: Int64, branch: String, entries: [String], modalID: String?, modalEntries: [String],
                secondaryEntries: [String] = [], compactColumn: NavigationSplitColumn? = nil) {
        self.revision = revision
        self.branch = branch
        self.entries = entries
        self.modalID = modalID
        self.modalEntries = modalEntries
        self.secondaryEntries = secondaryEntries
        self.compactColumn = compactColumn
    }
}


/// Presentation metadata from AppNav. A source key belongs to the app's native view hierarchy.
public struct NavigationTransitionContext: Equatable, Sendable {
    public enum Operation: String, Sendable {
        case push, pop, replace, branch, modalPresent, modalDismiss, refresh, rollback, reattach, initial
    }

    public let operation: Operation
    public let sourceEntryID: String?
    public let destinationEntryID: String?
    public let sourceKey: String?
    public let sourceScreen: String?
    public let destinationScreen: String?

    public init(operation: Operation, sourceEntryID: String? = nil, destinationEntryID: String? = nil,
                sourceKey: String? = nil, sourceScreen: String? = nil, destinationScreen: String? = nil) {
        self.operation = operation
        self.sourceEntryID = sourceEntryID
        self.destinationEntryID = destinationEntryID
        self.sourceKey = sourceKey
        self.sourceScreen = sourceScreen
        self.destinationScreen = destinationScreen
    }
}

/// Identifies an entry and the presentation context relevant to its native destination.
public struct NavigationDestinationContext {
    public let entry: NavigationEntry
    /// The branch, or the split column's stack, that holds the entry.
    public let branchID: String?
    public let modalID: String?
    public let transition: NavigationTransitionContext?
    /// The split column that holds the entry, or nil outside a split.
    public let splitColumn: NavigationSplitColumn?

    public init(entry: NavigationEntry, branchID: String?, modalID: String?,
                transition: NavigationTransitionContext? = nil, splitColumn: NavigationSplitColumn? = nil) {
        self.entry = entry
        self.branchID = branchID
        self.modalID = modalID
        self.transition = transition
        self.splitColumn = splitColumn
    }
}

/// Optional counters for observing native rendering and interactive Back during integration tests.
public struct NavigationDiagnostics {
    public internal(set) var lastReading: NavigationReading?
    public internal(set) var stagedTops: [String] = []
    public internal(set) var gesturesBegan = 0
    public internal(set) var gesturesCancelled = 0
    public internal(set) var gesturesCompleted = 0
    public internal(set) var gestureReading: NavigationReading?
    /// Animated pushes UIKit gave back before they settled, each reported as the push and then a native Back.
    public internal(set) var pushesTakenBack = 0
    /// Pushes taken back by a Back gesture that was then cancelled, so the push carried on applying.
    public internal(set) var pushesRestored = 0
    /// Whether the window's split is collapsed into one stack, or nil without a split.
    public internal(set) var splitCollapsed: Bool?
    /// Times the shell put the compact column back after SwiftUI changed it while both columns showed.
    public internal(set) var compactColumnRepairs = 0

    public init(lastReading: NavigationReading? = nil, stagedTops: [String] = [], gesturesBegan: Int = 0,
                gesturesCancelled: Int = 0, gesturesCompleted: Int = 0, gestureReading: NavigationReading? = nil,
                pushesTakenBack: Int = 0, pushesRestored: Int = 0) {
        self.lastReading = lastReading
        self.stagedTops = stagedTops
        self.gesturesBegan = gesturesBegan
        self.gesturesCancelled = gesturesCancelled
        self.gesturesCompleted = gesturesCompleted
        self.gestureReading = gestureReading
        self.pushesTakenBack = pushesTakenBack
        self.pushesRestored = pushesRestored
    }
}

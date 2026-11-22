/// The projection of one AppNav window, staged under a presentation revision.
public struct NavigationProjection: Equatable, Sendable {
    public let revision: Int64
    public let selectedBranch: String
    public let isBranchHost: Bool
    public let branches: [NavigationBranchProjection]
    public let modal: ModalProjection?
    public let transition: NavigationTransitionContext?

    public init(revision: Int64, selectedBranch: String, isBranchHost: Bool,
                branches: [NavigationBranchProjection], modal: ModalProjection?,
                transition: NavigationTransitionContext? = nil) {
        self.revision = revision
        self.selectedBranch = selectedBranch
        self.isBranchHost = isBranchHost
        self.branches = branches
        self.modal = modal
        self.transition = transition
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
public struct NavigationReading: Equatable, Sendable {
    public let revision: Int64
    public let branch: String
    public let entries: [String]
    public let modalID: String?
    public let modalEntries: [String]

    public init(revision: Int64, branch: String, entries: [String], modalID: String?, modalEntries: [String]) {
        self.revision = revision
        self.branch = branch
        self.entries = entries
        self.modalID = modalID
        self.modalEntries = modalEntries
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
    public let branchID: String?
    public let modalID: String?
    public let transition: NavigationTransitionContext?

    public init(entry: NavigationEntry, branchID: String?, modalID: String?,
                transition: NavigationTransitionContext? = nil) {
        self.entry = entry
        self.branchID = branchID
        self.modalID = modalID
        self.transition = transition
    }
}

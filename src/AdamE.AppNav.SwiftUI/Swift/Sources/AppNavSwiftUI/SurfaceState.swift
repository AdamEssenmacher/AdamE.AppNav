// The navigation surface's state machine. It has no UIKit or SwiftUI dependency: NativeSurface feeds it
// projections, binding writes, gesture boundaries and per-frame samples of the native stacks, and applies
// what it returns. Every rule about when a revision settles, what a native write may change, and when the
// shell stops waiting lives here, so the rules can be tested on the host without a simulator.

enum StackKey: Hashable {
    /// A branch, or a split's primary column.
    case branch(String)
    case modal(String)
    /// A split's secondary column, named by its stack.
    case secondary(String)

    var isModal: Bool {
        if case .modal = self { return true }
        return false
    }

    var isSecondary: Bool {
        if case .secondary = self { return true }
        return false
    }
}

/// How UIKit lays out the window's split on a display frame.
struct SplitSample: Equatable {
    /// The split shows one stack: the primary column, with the secondary column above it when that is on top.
    var isCollapsed: Bool
}

struct ModalSelection: Identifiable, Equatable {
    let id: String
}

/// The top page of one native stack as UIKit shows it on a display frame.
struct StackSample: Equatable {
    var key: StackKey
    /// The entry mounted on the navigation controller's top view controller.
    var top: NavigationEntry
    /// The navigation controller's view controller count.
    var depth: Int
    /// The entry each controller was last seen showing, bottom first. Nil where it was never witnessed.
    var identities: [String?]
    /// The top is the navigation controller's visible view controller.
    var isVisible = true
    /// The top's view is in the sampled window.
    var isInWindow = true
    /// The centre of the navigation controller hit-tests to its own view.
    var isHittable = true
    /// The navigation controller or its top has a transition coordinator.
    var isTransitioning = false
}

struct NativeSample {
    var time: Double
    /// The stack key of every page marker attached to the window. Nil for a marker not yet configured.
    var markerKeys: [StackKey?]
    var tops: [StackSample]
    /// Any rendered page has a transition coordinator.
    var isTransitioning = false
    /// Every configured page marker attached to the window, as its stack and entry.
    var mounted: [MountedPage] = []
    /// The split's layout, or nil when the window has no split controller.
    var split: SplitSample?
}

/// A page whose marker is attached to the sampled window.
struct MountedPage: Equatable {
    var key: StackKey
    var entry: NavigationEntry
}

/// How the adapter should commit a change: nil leaves the ambient transaction alone.
enum InstallAnimation: Equatable {
    case none
    case standard
    /// Ask the app's transaction policy with this context.
    case policy(NavigationTransitionContext)
}

struct SurfaceOutcome {
    var install: InstallAnimation?
    var readings: [NavigationReading] = []
    var traces: [String] = []
    /// A collapsed split must leave its secondary column although the compact column binding already names the
    /// primary one, so changing the binding would not pop it. The adapter pops the compact navigation stack to the
    /// primary column's top, as a native Back would.
    var popsCollapsedSecondary = false
    /// After that pop, write the staged compact column again: SwiftUI then shows the staged secondary column, which
    /// it would not do while the binding names the primary column. See `resumeCompactColumn()`.
    var resumesCompactColumn = false
}

struct SurfaceState {
    enum Phase: Equatable {
        /// The last staged revision settled, or none has been staged.
        case idle
        /// A revision is installed and its native stack has not been witnessed yet. `quietSince` is when
        /// the native side last went still without matching it.
        case applying(quietSince: Double?)
        /// The shell stopped waiting for a revision that never appeared natively. Gestures are released.
        /// A later matching frame still settles it.
        case abandoned
    }

    /// A revision that arrived during a native gesture, kept until the gesture ends.
    struct HeldStage: Equatable {
        let projection: NavigationProjection
        let animatesSinglePushes: Bool
        let hasPolicy: Bool
    }

    // `sourceKey` is the stack holding the source. It differs from `key` only for a sheet root,
    // whose source is the top of the branch it covers.
    struct PushOrigin: Equatable {
        let key: StackKey
        let sourceKey: StackKey
        let source: NavigationEntry
        let context: NavigationTransitionContext
    }

    private struct Authority: Equatable {
        var paths: [StackKey: [NavigationEntry]]
        var selectedBranch: String
        var selectedModal: ModalSelection?
        var compactColumn: NavigationSplitColumn?
    }

    // An animated one-entry push that SwiftUI wrote out of the path before it settled.
    private struct TakenBack {
        let key: StackKey
        let staged: NavigationReading
        let shorter: NavigationReading
        let shorterEntries: [NavigationEntry]
        let restored: [NavigationEntry]
        let removed: NavigationEntry
        /// The push was a selection that put a split's secondary column on top of a collapsed window, and Back
        /// closed that column again.
        var closesColumn = false
    }

    private enum Undo: Equatable {
        /// Put the applying target back.
        case target
        /// Put back what the shell showed before a write it does not accept.
        case snapshot(Authority)
    }

    /// How long the native side may sit still without showing an applying revision before the shell gives up.
    var stallLimit = 2.0
    /// How many refused native writes the shell undoes for one revision before it lets the native state stand.
    var undoLimit = 3

    private(set) var target: NavigationProjection?
    private(set) var paths: [StackKey: [NavigationEntry]] = [:]
    private(set) var selectedBranch = ""
    private(set) var selectedModal: ModalSelection?
    private(set) var diagnostics = NavigationDiagnostics()
    private(set) var phase = Phase.idle
    private(set) var interaction: ObjectIdentifier?
    private(set) var held: HeldStage?
    private(set) var lastReading: NavigationReading?
    private(set) var pushOrigins: [NavigationEntry: PushOrigin] = [:]
    /// The value of NavigationSplitView's `preferredCompactColumn`. SwiftUI writes it too, sometimes while the column
    /// it names is not on top, so it is never a reading. See `setCompactColumn`.
    private(set) var compactColumn = NavigationSplitColumn.primary
    /// A secondary column the staged revision closes. The detail column keeps rendering it until the revision settles,
    /// so a collapsed window pops the page rather than a placeholder.
    private(set) var departingSecondary: NavigationBranchProjection?
    /// The split's layout on the last frame.
    private(set) var splitLayout: SplitSample?
    /// A compact column to write once UIKit has popped a stale secondary column.
    private(set) var pendingCompactColumn: NavigationSplitColumn?
    /// The column on top of a collapsed split on the last frame, or nil when it is not collapsed.
    private(set) var collapsedTop: NavigationSplitColumn?

    private var undo: Undo?
    private var undoCount = 0
    private var stagedTops: [String] = []
    private var candidate: NavigationReading?
    private var needsGestureVerification = false
    private var nextDiagnostic = 0.0
    // The one-entry push being animated for the staged revision, while UIKit can still take it back.
    private var animatedPush: VisibleStep?
    // That push after SwiftUI wrote it out of the path, until UIKit's stack settles without it or a cancelled
    // gesture writes it back.
    private var takenBack: TakenBack?
    // Whether the page that push adds has been seen mounted in the window.
    private var pushWitnessed = false
    // Whether the last settled frame still showed the taken-back page on top.
    private var takenBackKept = false
    /// The stack whose animated push can take a Back while it applies: its page has been seen and no Back has
    /// taken it back yet. Every other stack withholds Back until the revision settles.
    private(set) var backDuringPush: StackKey?
    // Whether the split's layout on the last frame matched the one before it.
    private var splitLayoutHeld = false

    var applying: Bool {
        if case .applying = phase { return true }
        return false
    }

    // MARK: Staging

    mutating func stage(_ proposed: NavigationProjection, animatesSinglePushes: Bool, hasPolicy: Bool,
                        now: Double) -> SurfaceOutcome {
        var out = SurfaceOutcome()
        // A native Back or sheet gesture already under way can't be withheld, and replacing the paths
        // beneath it would fight UIKit. Keep the latest projection and install it when the gesture ends.
        if interaction != nil {
            if held == nil { out.traces.append("holding rev=\(proposed.revision) until the native interaction ends") }
            held = HeldStage(projection: proposed, animatesSinglePushes: animatesSinglePushes, hasPolicy: hasPolicy)
            return out
        }
        held = nil
        let next = selectingKnownBranch(proposed, &out)
        let restaged = next.revision == target?.revision
        // The presenter republishes a revision when it accepts a native pop or tab change, and when only
        // the transition metadata moved. The user can also be a native step ahead of it: a second pop
        // before the first was accepted. In all of these the shell's stacks stand and nothing installs.
        if restaged, let target, Self.hasSameContent(next, target) || (!applying && nativeCouldHaveReached(from: next)) {
            prunePushOrigins(next)
            self.target = next
            return out
        }
        if restaged { out.traces.append("rev=\(next.revision) restaged with stacks native navigation could not have produced; installing directly") }
        // A restaged revision is not a step from a settled reading, so it installs without animation.
        let step = restaged ? nil : visibleStep(to: next)
        let singlePush = step?.isPush == true && (next.transition == nil || next.transition?.operation == .push)
        let sheetRoot = !restaged && next.transition?.operation == .modalPresent &&
            canAnimate(next, operation: .modalPresent, step: step)
        var animation = InstallAnimation.none
        if restaged {
            animation = .none
        } else if let context = next.transition, hasPolicy {
            if canAnimate(next, operation: context.operation, step: step) { animation = .policy(context) }
        } else if !hasPolicy, singlePush && animatesSinglePushes {
            // A supplied policy replaces this default, including for projections without transition metadata.
            animation = .standard
        }
        stagePushOrigins(next, push: singlePush ? step : nil, sheetRoot: sheetRoot)
        animatedPush = singlePush && animation != .none ? step : nil
        // A pending take-back can be dropped here. The presenter stages a new revision only after the router
        // operation that staged the old one has ended, and that operation can't end while a take-back is pending: the
        // take-back holds back the staged reading the operation waits for. So a new revision here is the rollback
        // after that operation timed out or was cancelled, or a reattach. Neither recorded the push, and each installs
        // the router's committed stack.
        clearTakeBack()
        phase = next.revision > 0 ? .applying(quietSince: nil) : .idle
        nextDiagnostic = now + 1
        candidate = nil
        lastReading = nil
        stagedTops = []
        needsGestureVerification = false
        undo = nil
        undoCount = 0
        let authority = Self.authority(of: next)
        paths = authority.paths
        selectedBranch = authority.selectedBranch
        selectedModal = authority.selectedModal
        if next.split != nil, next.split?.secondary == nil, let leaving = target?.split?.secondary ?? departingSecondary {
            departingSecondary = leaving
        } else {
            departingSecondary = nil
        }
        // While collapsed, SwiftUI can leave the binding on the primary column with the secondary root still on top.
        // It then neither pops that column for a staged primary column, nor shows a different secondary column, so
        // UIKit pops it first, as a native Back would.
        let strayOnTop = compactColumn == .primary && splitLayout?.isCollapsed == true && collapsedTop == .secondary
        let previousSecondary = target?.split?.secondary
        adoptCompactColumn(authority.compactColumn)
        pendingCompactColumn = nil
        if strayOnTop, let split = next.split {
            if split.compactColumn == .primary {
                out.popsCollapsedSecondary = true
                out.traces.append("rev=\(next.revision) closes the collapsed secondary column the binding already left; popping it natively")
            } else if let secondary = split.secondary, previousSecondary?.id != secondary.id ||
                        previousSecondary?.entries.first != secondary.entries.first {
                out.popsCollapsedSecondary = true
                out.resumesCompactColumn = true
                pendingCompactColumn = .secondary
                out.traces.append("rev=\(next.revision) replaces a collapsed secondary column the binding already left; popping it natively before showing \(secondary.id)")
            }
        }
        target = next
        out.install = animation
        return out
    }

    /// Writes the compact column a stage held back until UIKit popped the old secondary column.
    mutating func resumeCompactColumn() -> SurfaceOutcome {
        var out = SurfaceOutcome()
        guard let pending = pendingCompactColumn, target?.split?.compactColumn == pending else {
            pendingCompactColumn = nil
            return out
        }
        pendingCompactColumn = nil
        compactColumn = pending
        out.install = InstallAnimation.none
        return out
    }

    // Writing the compact column SwiftUI already shows on top of a collapsed split makes it push that column's root a
    // second time. The binding keeps its value then, even when SwiftUI changed it while the column stayed on top.
    private mutating func adoptCompactColumn(_ desired: NavigationSplitColumn?) {
        guard let desired else { return }
        if splitLayout?.isCollapsed == true, collapsedTop == desired { return }
        compactColumn = desired
    }

    /// The app's transaction policy returned no animation for the staged revision, so its push can't be taken back.
    mutating func installWasNotAnimated() {
        guard takenBack == nil else { return }
        animatedPush = nil
        pushWitnessed = false
        backDuringPush = nil
    }

    private mutating func clearTakeBack() {
        takenBack = nil
        takenBackKept = false
        pushWitnessed = false
        backDuringPush = nil
    }

    /// The revision held during a gesture, once no gesture is in flight.
    mutating func takeHeld() -> HeldStage? {
        guard interaction == nil, let stage = held else { return nil }
        held = nil
        return stage
    }

    private static func authority(of projection: NavigationProjection) -> Authority {
        var paths = Dictionary(uniqueKeysWithValues: projection.branches.map {
            (StackKey.branch($0.id), Array($0.entries.dropFirst()))
        })
        if let modal = projection.modal {
            paths[.modal(modal.id)] = Array(modal.entries.dropFirst())
        }
        if let secondary = projection.split?.secondary {
            paths[.secondary(secondary.id)] = Array(secondary.entries.dropFirst())
        }
        return Authority(paths: paths, selectedBranch: projection.selectedBranch,
                         selectedModal: projection.modal.map { ModalSelection(id: $0.id) },
                         compactColumn: projection.split?.compactColumn)
    }

    // Every reading must name a rendered branch. An unknown selection would leave no branch top to witness.
    // TabView shows the first branch, and a stack root renders whichever branch this leaves selected.
    private func selectingKnownBranch(_ projection: NavigationProjection,
                                      _ out: inout SurfaceOutcome) -> NavigationProjection {
        guard let first = projection.branches.first,
              !projection.branches.contains(where: { $0.id == projection.selectedBranch }) else { return projection }
        out.traces.append("unknown selected branch \(projection.selectedBranch) rev=\(projection.revision); using \(first.id)")
        return NavigationProjection(revision: projection.revision, selectedBranch: first.id,
                                    isBranchHost: projection.isBranchHost, branches: projection.branches,
                                    modal: projection.modal, transition: projection.transition, split: projection.split)
    }

    // What the shell shows is the projection after zero or more native pops, a sheet dismissal, a tab change, or
    // a collapsed window's Back out of a split's secondary column.
    private func nativeCouldHaveReached(from projection: NavigationProjection) -> Bool {
        if let split = projection.split {
            guard let current = target?.split, current.id == split.id else { return false }
            if let secondary = split.secondary {
                guard current.secondary?.id == secondary.id, split.compactColumn == current.compactColumn else { return false }
            } else if current.secondary != nil, split.compactColumn != .primary {
                return false
            }
        } else if target?.split != nil {
            return false
        }
        let authority = Self.authority(of: projection)
        guard projection.branches.contains(where: { $0.id == selectedBranch }),
              selectedModal == nil || selectedModal == authority.selectedModal else { return false }
        return authority.paths.allSatisfy { key, path in
            if key.isModal && selectedModal == nil { return true }
            return path.starts(with: paths[key] ?? [])
        }
    }

    private static func hasSameContent(_ lhs: NavigationProjection, _ rhs: NavigationProjection) -> Bool {
        lhs.selectedBranch == rhs.selectedBranch && lhs.isBranchHost == rhs.isBranchHost &&
            lhs.branches == rhs.branches && lhs.modal == rhs.modal && lhs.split == rhs.split
    }

    // MARK: Destination context

    func context(for entry: NavigationEntry, key: StackKey) -> NavigationDestinationContext {
        let transition = pushOrigins[entry].flatMap { $0.key == key ? $0.context : nil } ??
            (target?.transition?.destinationEntryID == entry.id ? target?.transition : nil)
        switch key {
        case .branch(let id):
            return NavigationDestinationContext(entry: entry, branchID: id, modalID: nil, transition: transition,
                                                splitColumn: target?.split == nil ? nil : .primary)
        case .modal(let id):
            return NavigationDestinationContext(entry: entry, branchID: nil, modalID: id, transition: transition)
        case .secondary(let id):
            return NavigationDestinationContext(entry: entry, branchID: id, modalID: nil, transition: transition,
                                                splitColumn: .secondary)
        }
    }

    func matchedPushContext(for entry: NavigationEntry, key: StackKey) -> NavigationTransitionContext? {
        guard let origin = pushOrigins[entry], origin.key == key else { return nil }
        return origin.context
    }

    private mutating func stagePushOrigins(_ next: NavigationProjection, push: VisibleStep?, sheetRoot: Bool) {
        switch next.transition?.operation {
        case .initial, .refresh, .rollback, .reattach:
            pushOrigins.removeAll()
        default:
            let nextIDs = Set((next.branches.flatMap(\.entries) + (next.modal?.entries ?? []) +
                               (next.split?.secondary?.entries ?? [])).map(\.id))
            // A removed controller can still be animating out. Preserve its exact match until
            // settlement if the source still belongs to its stack or is departing alongside it.
            pushOrigins = pushOrigins.filter { destination, origin in
                if corresponds(origin, destination: destination, in: next) { return true }
                guard !nextIDs.contains(destination.id),
                      let previous = target, corresponds(origin, destination: destination, in: previous)
                else { return false }
                return !nextIDs.contains(origin.source.id) ||
                    entries(for: origin.sourceKey, in: next)?.contains(origin.source) == true
            }
        }
        guard let context = next.transition else { return }
        // A selection zooms only over a collapsed primary column. Side by side, its source row stays on screen, and
        // SwiftUI hides a matched source while its destination shows.
        if let push, !push.changesColumn || splitLayout?.isCollapsed == true,
           let source = push.old.last, let destination = push.new.last,
           source.mount > 0, destination.mount > 0,
           context.sourceEntryID == source.id, context.destinationEntryID == destination.id {
            pushOrigins[destination] = PushOrigin(key: push.key, sourceKey: push.sourceKey, source: source, context: context)
        } else if sheetRoot, let modal = next.modal, let root = modal.entries.first,
                  let branch = next.branches.first(where: { $0.id == next.selectedBranch }),
                  let source = branch.entries.last, source.mount > 0, root.mount > 0,
                  context.sourceEntryID == source.id, context.destinationEntryID == root.id {
            // A root-only sheet presented from the settled branch top can zoom from a source on that page.
            pushOrigins[root] = PushOrigin(key: .modal(modal.id), sourceKey: .branch(branch.id),
                                           source: source, context: context)
        }
    }

    private mutating func prunePushOrigins(_ projection: NavigationProjection) {
        pushOrigins = pushOrigins.filter { destination, origin in
            corresponds(origin, destination: destination, in: projection)
        }
    }

    private func corresponds(_ origin: PushOrigin, destination: NavigationEntry,
                             in projection: NavigationProjection) -> Bool {
        guard let entries = entries(for: origin.key, in: projection),
              let destinationIndex = entries.firstIndex(of: destination) else { return false }
        if destinationIndex > 0 {
            return origin.sourceKey == origin.key && entries[destinationIndex - 1] == origin.source
        }
        // A secondary column's root matches while it is selected over the primary column's top, its source.
        if origin.key.isSecondary {
            return projection.split?.compactColumn == .secondary &&
                self.entries(for: origin.sourceKey, in: projection)?.last == origin.source
        }
        // A sheet root matches while its source is still the top of the branch the sheet covers.
        guard origin.key.isModal, case .branch(let branchID) = origin.sourceKey,
              projection.selectedBranch == branchID else { return false }
        return self.entries(for: origin.sourceKey, in: projection)?.last == origin.source
    }

    private func entries(for key: StackKey, in projection: NavigationProjection) -> [NavigationEntry]? {
        switch key {
        case .branch(let id):
            return projection.branches.first(where: { $0.id == id })?.entries
        case .modal(let id):
            return projection.modal?.id == id ? projection.modal?.entries : nil
        case .secondary(let id):
            return projection.split?.secondary.flatMap { $0.id == id ? $0.entries : nil }
        }
    }

    // MARK: Animation eligibility

    private struct VisibleStep {
        let key: StackKey
        /// The stack holding the step's source. It differs from `key` only for a split selection, whose source is the
        /// primary column's top and whose destination is the secondary column's root.
        var sourceKey: StackKey
        let old: [NavigationEntry]
        let new: [NavigationEntry]

        init(key: StackKey, sourceKey: StackKey? = nil, old: [NavigationEntry], new: [NavigationEntry]) {
            self.key = key
            self.sourceKey = sourceKey ?? key
            self.old = old
            self.new = new
        }

        /// The step opens or closes a split's secondary column over its primary column.
        var changesColumn: Bool { sourceKey != key }

        var isPush: Bool { !old.isEmpty && new.count == old.count + 1 && new.starts(with: old) }
        var isPop: Bool { !new.isEmpty && old.count == new.count + 1 && old.starts(with: new) }
    }

    private func settledBranch(to next: NavigationProjection) -> VisibleStep? {
        guard let prior = target, phase == .idle, interaction == nil, let reading = lastReading,
              prior.isBranchHost == next.isBranchHost,
              prior.branches.map(\.id) == next.branches.map(\.id),
              selectedBranch == next.selectedBranch, prior.selectedBranch == next.selectedBranch,
              prior.split == next.split,
              reading.branch == selectedBranch, reading.modalID == prior.modal?.id,
              selectedModal?.id == prior.modal?.id,
              let old = prior.branches.first(where: { $0.id == selectedBranch }),
              let new = next.branches.first(where: { $0.id == selectedBranch }),
              reading.entries == old.entries.map(\.id),
              paths[.branch(selectedBranch)] == Array(old.entries.dropFirst()),
              prior.branches.filter({ $0.id != selectedBranch }) == next.branches.filter({ $0.id != selectedBranch })
        else { return nil }
        return VisibleStep(key: .branch(selectedBranch), old: old.entries, new: new.entries)
    }

    private func visibleStep(to next: NavigationProjection) -> VisibleStep? {
        if next.split != nil || target?.split != nil, next.split != target?.split { return splitStep(to: next) }
        guard let prior = target, let branch = settledBranch(to: next) else { return nil }
        if prior.modal == nil && next.modal == nil { return branch }
        guard prior.branches == next.branches, let old = prior.modal, let new = next.modal,
              old.id == new.id, lastReading?.modalEntries == old.entries.map(\.id),
              paths[.modal(old.id)] == Array(old.entries.dropFirst()) else { return nil }
        return VisibleStep(key: .modal(old.id), old: old.entries, new: new.entries)
    }

    // A step that changes a settled split's columns, read as a collapsed window shows it: the primary stack with the
    // secondary stack above it while that is on top. Selecting over the primary column pushes the secondary root, and
    // closing a one-page secondary column pops it.
    private func splitStep(to next: NavigationProjection) -> VisibleStep? {
        guard let prior = target, let old = prior.split, let new = next.split, old.id == new.id,
              phase == .idle, interaction == nil, let reading = lastReading,
              prior.modal == nil, next.modal == nil, selectedModal == nil, reading.modalID == nil,
              let primaryOld = prior.branches.first, let primaryNew = next.branches.first, primaryOld.id == primaryNew.id,
              reading.entries == primaryOld.entries.map(\.id),
              reading.secondaryEntries == (old.secondary?.entries.map(\.id) ?? []),
              reading.compactColumn == old.compactColumn,
              paths[.branch(primaryOld.id)] == Array(primaryOld.entries.dropFirst())
        else { return nil }
        if let secondary = old.secondary, paths[.secondary(secondary.id)] != Array(secondary.entries.dropFirst()) {
            return nil
        }
        let primaryKey = StackKey.branch(primaryOld.id)
        if primaryOld.entries != primaryNew.entries {
            guard old.secondary == new.secondary, old.compactColumn == new.compactColumn else { return nil }
            return VisibleStep(key: primaryKey, old: primaryOld.entries, new: primaryNew.entries)
        }
        switch (old.secondary, new.secondary) {
        case let (before?, after?) where before.id == after.id && old.compactColumn == .secondary &&
            new.compactColumn == .secondary:
            return VisibleStep(key: .secondary(after.id), old: before.entries, new: after.entries)
        case let (_, after?) where old.compactColumn == .primary && new.compactColumn == .secondary &&
            after.entries.count == 1:
            return VisibleStep(key: .secondary(after.id), sourceKey: primaryKey, old: primaryOld.entries,
                               new: primaryOld.entries + after.entries)
        case let (before?, nil) where old.compactColumn == .secondary && before.entries.count == 1:
            return VisibleStep(key: .secondary(before.id), sourceKey: primaryKey, old: primaryOld.entries + before.entries,
                               new: primaryOld.entries)
        default:
            return nil
        }
    }

    private func canAnimate(_ next: NavigationProjection, operation: NavigationTransitionContext.Operation,
                            step: VisibleStep?) -> Bool {
        switch operation {
        case .push:
            return step?.isPush == true
        case .pop:
            return step?.isPop == true
        case .modalPresent:
            // A sheet with prebuilt history must install that history directly.
            return settledBranch(to: next) != nil && target?.branches == next.branches &&
                target?.modal == nil && next.modal?.entries.count == 1
        case .modalDismiss:
            guard settledBranch(to: next) != nil, target?.branches == next.branches,
                  let modal = target?.modal, lastReading?.modalEntries == modal.entries.map(\.id),
                  paths[.modal(modal.id)] == Array(modal.entries.dropFirst()) else { return false }
            return next.modal == nil
        case .replace, .branch, .refresh, .rollback, .reattach, .initial:
            return false
        }
    }

    // MARK: Gestures

    /// An interactive transition registered its completion. A newer one replaces the one being tracked.
    mutating func interactionBegan(_ token: ObjectIdentifier) {
        guard interaction != token else { return }
        interaction = token
        diagnostics.gesturesBegan += 1
    }

    /// Returns true when a held revision is waiting to be staged.
    mutating func interactionEnded(_ token: ObjectIdentifier, cancelled: Bool) -> Bool {
        guard interaction == token else { return false }
        if cancelled { diagnostics.gesturesCancelled += 1 } else { diagnostics.gesturesCompleted += 1 }
        interaction = nil
        needsGestureVerification = true
        return held != nil
    }

    // MARK: Native writes

    // SwiftUI reports what UIKit already did. Refusing a write would leave the two disagreeing with
    // nothing left to change, so a write the shell does not accept is taken and then undone on the next
    // quiet frame. That makes the gesture gates in NavigationShell a courtesy: a native change that slips
    // past them is corrected here.

    mutating func setPath(_ next: [NavigationEntry], for key: StackKey) -> SurfaceOutcome {
        var out = SurfaceOutcome()
        let current = paths[key] ?? []
        guard next != current else { return out }
        let shorter = next.count < current.count && current.starts(with: next)
        // A cancelled interaction may restore the previously witnessed suffix.
        let witnessed: [String]? = switch key {
        case .branch(let id) where lastReading?.branch == id: lastReading?.entries
        case .modal(let id) where lastReading?.modalID == id: lastReading?.modalEntries
        case .secondary(let id) where target?.split?.secondary?.id == id: lastReading?.secondaryEntries
        default: nil
        }
        let restore = witnessed?.dropFirst().elementsEqual(next.map(\.id)) == true
        if !applying && (shorter || restore) {
            paths[key] = next
            return out
        }
        if applying, let taken = takenBack, taken.key == key, next == taken.restored {
            // The Back gesture was cancelled and SwiftUI wrote the push back: it carries on applying.
            restoreTakenBack(&out, installs: false)
            return out
        }
        if applying, takenBack == nil, undo == nil, shorter, let push = animatedPush, push.key == key,
           current == Array(push.new.dropFirst()), pushWitnessed, let staged = stagedReading() {
            // A Back caught a push the shell was still applying, such as a slow swipe during the zoom, or a
            // multi-entry pop from the Back-history menu, which showing the Back button also enables.
            // UIKit has usually removed the page by the time SwiftUI writes, so this relies on having seen it.
            // Undoing the write would make SwiftUI push the page again. Take the shorter path for good, and
            // report the push and the shorter stack together once UIKit's stack settles without the page.
            takenBack = TakenBack(key: key, staged: staged, shorter: staged.replacing(key, with: next),
                                  shorterEntries: [push.new[0]] + next, restored: current,
                                  removed: push.new[push.new.count - 1])
            takenBackKept = false
            // A further Back before UIKit settles is not accepted, and is undone like any other.
            backDuringPush = nil
            paths[key] = next
            out.traces.append("native Back took back the push of \(push.new[push.new.count - 1].id) rev=\(staged.revision)")
            return out
        }
        // A stack the target no longer renders is on its way out; its last writes mean nothing.
        guard let target, entries(for: key, in: target) != nil else { return out }
        // A write that moves a taken-back stack anywhere else ends the take-back. The undo below puts the
        // staged target back and the push carries on applying.
        if takenBack?.key == key { clearTakeBack() }
        noteUnacceptedWrite("path \(key)", &out)
        paths[key] = next
        return out
    }

    /// SwiftUI wrote NavigationSplitView's `preferredCompactColumn`. The value is always kept: refusing it makes SwiftUI
    /// push the secondary root again. It is never a reading either. SwiftUI sets the primary column when the secondary
    /// stack's path empties, even while the secondary root stays on top, so readings come from UIKit's stacks alone.
    /// A Back that takes back an animated selection is the one write that counts, and only once UIKit's stack agrees.
    mutating func setCompactColumn(_ next: NavigationSplitColumn) -> SurfaceOutcome {
        var out = SurfaceOutcome()
        guard next != compactColumn else { return out }
        if next == .primary, applying, takenBack == nil, undo == nil, let push = animatedPush, push.changesColumn,
           push.new.count > push.old.count, pushWitnessed, let staged = stagedReading() {
            takenBack = TakenBack(key: push.key, staged: staged, shorter: staged.closingSecondary(),
                                  shorterEntries: [], restored: [], removed: push.new[push.new.count - 1],
                                  closesColumn: true)
            takenBackKept = false
            backDuringPush = nil
            out.traces.append("native Back took back the selection of \(push.new[push.new.count - 1].id) rev=\(staged.revision)")
        } else if next == .secondary, applying, takenBack?.closesColumn == true {
            // The Back gesture was cancelled and SwiftUI put the secondary column back on top.
            restoreTakenBack(&out, installs: false)
        }
        out.traces.append("SwiftUI set the compact column to \(next.rawValue) rev=\(target?.revision ?? 0)")
        compactColumn = next
        return out
    }

    mutating func selectBranch(_ next: String) -> SurfaceOutcome {
        var out = SurfaceOutcome()
        guard next != selectedBranch, target?.branches.contains(where: { $0.id == next }) == true else { return out }
        if !applying && selectedModal == nil {
            selectedBranch = next
            return out
        }
        noteUnacceptedWrite("branch \(next)", &out)
        selectedBranch = next
        return out
    }

    mutating func dismissModal() -> SurfaceOutcome {
        var out = SurfaceOutcome()
        guard selectedModal != nil else { return out }
        if !applying {
            selectedModal = nil
            return out
        }
        noteUnacceptedWrite("modal dismissal", &out)
        selectedModal = nil
        return out
    }

    // Arms the undo for a write the shell does not accept. Past the limit the native state stands: the
    // shell stops waiting for the revision instead of fighting the user.
    private mutating func noteUnacceptedWrite(_ what: String, _ out: inout SurfaceOutcome) {
        if undoCount >= undoLimit {
            if applying { abandon("native \(what) kept overriding it", &out) }
            return
        }
        if undo == nil {
            undo = applying ? .target : .snapshot(Authority(paths: paths, selectedBranch: selectedBranch,
                                                             selectedModal: selectedModal, compactColumn: nil))
            out.traces.append("native \(what) is not accepted rev=\(target?.revision ?? 0); restoring on the next quiet frame")
        }
    }

    private mutating func abandon(_ reason: String, _ out: inout SurfaceOutcome) {
        out.traces.append("abandoning rev=\(target?.revision ?? 0): \(reason). Releasing native Back and dismissal.")
        phase = .abandoned
        undo = nil
        candidate = nil
        animatedPush = nil
        departingSecondary = nil
        clearTakeBack()
    }

    /// Whether native Back is withheld on `key` while a revision applies. An animated one-entry push whose
    /// page has been seen is the exception: the shell can take it back.
    func withholdsBack(_ key: StackKey) -> Bool {
        applying && backDuringPush != key
    }

    // MARK: Gates

    func isWaitingForTab(_ key: StackKey) -> Bool {
        phase == .idle && target?.isBranchHost == true && key == .branch(selectedBranch) &&
            (lastReading?.branch != selectedBranch || target?.selectedBranch != selectedBranch)
    }

    var modalTop: NavigationEntry? {
        guard let modal = target?.modal, selectedModal?.id == modal.id else { return nil }
        return paths[.modal(modal.id)]?.last ?? modal.entries.first
    }

    // MARK: Frames

    mutating func sample(_ sample: NativeSample) -> SurfaceOutcome {
        var out = SurfaceOutcome()
        let quiet = !sample.isTransitioning && interaction == nil
        var qualified = false
        noteSplitLayout(sample)
        if applying, let push = animatedPush, !pushWitnessed,
           sample.mounted.contains(where: { $0.entry == push.new.last && $0.key == push.key }) {
            pushWitnessed = true
            backDuringPush = push.key
        }
        if let pending = undo, quiet {
            restore(pending, &out)
        } else {
            qualified = witness(sample, &out)
        }
        if !qualified { candidate = nil }
        repairCompactColumn(quiet: quiet, &out)
        // A frame that matches the shell's own stacks ends any tug-of-war with native writes.
        if qualified, !applying { undoCount = 0 }
        // An applying revision always ends: it settles, or the native side sits still without showing it
        // for `stallLimit` and the shell stops waiting.
        if case .applying(let quietSince) = phase {
            if qualified || !quiet {
                phase = .applying(quietSince: nil)
            } else if let quietSince {
                if sample.time - quietSince >= stallLimit { abandon("the native stack never showed it", &out) }
            } else {
                phase = .applying(quietSince: sample.time)
            }
        }
        return out
    }

    // Records the split's layout. A layout counts once it holds across two frames: during a fold or a rotation UIKit
    // passes through intermediate sizes, and size classes flicker.
    private mutating func noteSplitLayout(_ sample: NativeSample) {
        splitLayoutHeld = sample.split != nil && sample.split == splitLayout
        splitLayout = sample.split
        diagnostics.splitCollapsed = target?.split == nil ? nil : sample.split?.isCollapsed
        if splitLayoutHeld, sample.split?.isCollapsed == false {
            // Unfolded, the selected row is on screen beside its game. Drop the selection's zoom so SwiftUI shows the
            // row's cover again; Back from the game no longer zooms into a row the user can already see.
            pushOrigins = pushOrigins.filter { _, origin in !(origin.key.isSecondary && origin.sourceKey != origin.key) }
        }
        guard sample.split?.isCollapsed == true, let primary = target?.branches.first, target?.split != nil else {
            collapsedTop = nil
            return
        }
        if let top = sample.tops.first(where: { $0.key == .branch(primary.id) || $0.key.isSecondary }) {
            collapsedTop = top.key.isSecondary ? .secondary : .primary
        }
    }

    // SwiftUI can change the compact column while the secondary root stays on top. A window that later collapses would
    // then show only the primary column. While both columns show, putting the staged column back changes nothing on
    // screen, so the shell does it on a quiet frame.
    private mutating func repairCompactColumn(quiet: Bool, _ out: inout SurfaceOutcome) {
        guard let split = target?.split, let layout = splitLayout, !layout.isCollapsed, splitLayoutHeld, quiet,
              undo == nil, takenBack == nil, compactColumn != split.compactColumn else { return }
        compactColumn = split.compactColumn
        diagnostics.compactColumnRepairs += 1
        if out.install == nil { out.install = InstallAnimation.none }
        out.traces.append("restored the compact column \(split.compactColumn.rawValue) while both columns show rev=\(target?.revision ?? 0)")
    }

    private mutating func restore(_ pending: Undo, _ out: inout SurfaceOutcome) {
        let authority: Authority
        switch pending {
        case .snapshot(let snapshot): authority = snapshot
        case .target:
            guard let target else { undo = nil; return }
            authority = Self.authority(of: target)
        }
        undo = nil
        undoCount += 1
        paths = authority.paths
        selectedBranch = authority.selectedBranch
        selectedModal = authority.selectedModal
        adoptCompactColumn(authority.compactColumn)
        out.install = InstallAnimation.none
        out.traces.append("restored the shell's stacks after a native write rev=\(target?.revision ?? 0)")
    }

    // Reads the sampled stacks against the target. Returns true when the frame produced a reading.
    private mutating func witness(_ sample: NativeSample, _ out: inout SurfaceOutcome) -> Bool {
        guard let target, target.revision > 0 else { return false }

        // A window without a root can still present a sheet; only sheet pages may remain attached.
        if target.branches.isEmpty, !sample.markerKeys.allSatisfy({ $0?.isModal == true }) { return false }

        if applying && sample.time >= nextDiagnostic {
            nextDiagnostic = sample.time + 2
            let tops = sample.tops.map { top in
                "\(top.key):\(top.top.id):depth=\(top.depth):visible=\(top.isVisible):hit=\(top.isHittable):transition=\(top.isTransitioning)"
            }.joined(separator: ";")
            out.traces.append("waiting rev=\(target.revision) branch=\(selectedBranch) modal=\(selectedModal?.id ?? "-") attached=\(sample.markerKeys.count) tops=[\(tops)]")
        }

        if let split = target.split { return witnessSplit(sample, target: target, split: split, &out) }

        let currentModal = selectedModal.flatMap { selection in target.modal?.id == selection.id ? target.modal : nil }
        let modalMarkers = sample.markerKeys.filter { $0?.isModal == true }
        let branch = target.branches.first(where: { $0.id == selectedBranch })
        let branchTop = branch.flatMap { branch in sample.tops.first(where: { $0.key == .branch(branch.id) }) }
        guard target.branches.isEmpty || branchTop != nil else { return false }

        let modalTop: StackSample?
        if let currentModal {
            let key = StackKey.modal(currentModal.id)
            guard modalMarkers.allSatisfy({ $0 == key }),
                  let visible = sample.tops.first(where: { $0.key == key && $0.isHittable })
            else { return false }
            modalTop = visible
        } else {
            // The old sheet must leave before the uncovered branch can be acknowledged.
            guard modalMarkers.isEmpty, branchTop?.isHittable ?? true else { return false }
            modalTop = nil
        }

        if applying, let top = modalTop ?? branchTop, stagedTops.last != top.top.id { stagedTops.append(top.top.id) }
        guard branchTop?.isTransitioning != true, modalTop?.isTransitioning != true, interaction == nil
        else { return false }
        if let taken = takenBack, let top = modalTop ?? branchTop, top.key == taken.key, top.top == taken.removed {
            // UIKit's stack settled with the taken-back page still on top across two frames, so the Back that took it
            // was cancelled. A UIKit cancellation alone can't tell this apart from UIKit giving the push back, so only
            // the settled stack decides.
            if takenBackKept { restoreTakenBack(&out, installs: true) } else { takenBackKept = true }
            return false
        }
        takenBackKept = false

        let branchEntries: [String]
        if let branch, let branchTop {
            guard let entries = read(branchTop, desired: desired(branch.entries, key: .branch(branch.id)),
                                     covered: modalTop != nil)
            else { return false }
            if isWaitingForTab(branchTop.key), entries.count < branch.entries.count {
                // There is no independent witness for a selection followed by a premature pop.
                // Reassert the intact newly selected stack so AppNav can commit the tab change.
                paths[branchTop.key] = Array(branch.entries.dropFirst())
                out.install = InstallAnimation.none
                return false
            }
            branchEntries = entries
        } else {
            branchEntries = []
        }

        let modalEntries: [String]
        if let currentModal, let modalTop {
            guard let entries = read(modalTop, desired: desired(currentModal.entries, key: .modal(currentModal.id)))
            else { return false }
            modalEntries = entries
        } else {
            modalEntries = []
        }

        if applying && (branch.map({ target.selectedBranch != $0.id }) == true || target.modal?.id != currentModal?.id) {
            return false
        }
        settle(NavigationReading(revision: target.revision, branch: branch?.id ?? "", entries: branchEntries,
                                 modalID: currentModal?.id, modalEntries: modalEntries), &out)
        return true
    }

    // A split shows its columns side by side, or collapses them into one navigation stack: the primary column, with the
    // secondary column above it while that is on top. Collapsed, the one stack is read whole. Side by side, each column
    // is read on its own; a primary column the window hides settles on its path, like an unselected tab. Either way the
    // reading names the same columns, so folding and unfolding never report a change.
    private mutating func witnessSplit(_ sample: NativeSample, target: NavigationProjection, split: SplitProjection,
                                       _ out: inout SurfaceOutcome) -> Bool {
        guard let layout = sample.split, splitLayoutHeld, let primary = target.branches.first,
              let primaryRoot = primary.entries.first else { return false }
        let primaryKey = StackKey.branch(primary.id)
        let closing = takenBack?.closesColumn == true
        let secondary = closing ? nil : split.secondary
        let secondaryKey = secondary.map { StackKey.secondary($0.id) }
        let desiredCompact = closing ? NavigationSplitColumn.primary : split.compactColumn

        let currentModal = selectedModal.flatMap { selection in target.modal?.id == selection.id ? target.modal : nil }
        let modalMarkers = sample.markerKeys.filter { $0?.isModal == true }
        let columnTops = sample.tops.filter { $0.key == primaryKey || ($0.key == secondaryKey && secondaryKey != nil) }
        let modalTop: StackSample?
        if let currentModal {
            let key = StackKey.modal(currentModal.id)
            guard modalMarkers.allSatisfy({ $0 == key }),
                  let visible = sample.tops.first(where: { $0.key == key && $0.isHittable })
            else { return false }
            modalTop = visible
        } else {
            // The old sheet must leave before the columns can be acknowledged. An overlaid column is dimmed, so one
            // hittable column is enough.
            guard modalMarkers.isEmpty, columnTops.isEmpty || columnTops.contains(where: \.isHittable) else { return false }
            modalTop = nil
        }
        if applying, let top = modalTop ?? columnTops.last, stagedTops.last != top.top.id { stagedTops.append(top.top.id) }
        guard !columnTops.contains(where: \.isTransitioning), modalTop?.isTransitioning != true, interaction == nil
        else { return false }
        if let taken = takenBack, let top = sample.tops.first(where: { $0.key == taken.key }), top.top == taken.removed {
            // As for a stack push: UIKit settled with the taken-back page on top, so its Back was cancelled.
            if takenBackKept { restoreTakenBack(&out, installs: true) } else { takenBackKept = true }
            return false
        }
        takenBackKept = false

        let covered = modalTop != nil
        let primaryEntries = [primaryRoot] + (paths[primaryKey] ?? [])
        var primaryIDs: [String]
        var secondaryIDs: [String]
        var compact: NavigationSplitColumn
        if layout.isCollapsed {
            guard columnTops.count == 1, let top = columnTops.first, covered || top.isVisible, top.isInWindow
            else { return false }
            if let secondary, let secondaryKey, top.key == secondaryKey {
                let secondaryEntries = [secondary.entries[0]] + (paths[secondaryKey] ?? [])
                guard matches(top, primaryEntries + secondaryEntries),
                      applying ? primary.entries == primaryEntries && desired(secondary.entries, key: secondaryKey) ==
                        secondaryEntries && desiredCompact == .secondary
                      : primary.entries.starts(with: primaryEntries) && secondary.entries.starts(with: secondaryEntries)
                else { return false }
                (primaryIDs, secondaryIDs, compact) = (primaryEntries.map(\.id), secondaryEntries.map(\.id), .secondary)
            } else {
                guard top.key == primaryKey, matches(top, primaryEntries),
                      applying ? primary.entries == primaryEntries : primary.entries.starts(with: primaryEntries)
                else { return false }
                if desiredCompact == .secondary {
                    // The secondary column is staged on top, so it must show before the revision settles. Once
                    // settled, a collapsed window that shows the primary column alone has closed it natively.
                    if applying { return false }
                    (primaryIDs, secondaryIDs, compact) = (primaryEntries.map(\.id), [], .primary)
                } else {
                    let hidden = secondary.map { [$0.entries[0]] + (paths[.secondary($0.id)] ?? []) } ?? []
                    (primaryIDs, secondaryIDs, compact) = (primaryEntries.map(\.id), hidden.map(\.id), .primary)
                }
            }
        } else {
            if let top = columnTops.first(where: { $0.key == primaryKey }), top.isInWindow {
                guard let entries = read(top, desired: primary.entries, covered: covered) else { return false }
                primaryIDs = entries
            } else {
                guard applying ? primary.entries == primaryEntries : primary.entries.starts(with: primaryEntries)
                else { return false }
                primaryIDs = primaryEntries.map(\.id)
            }
            if let secondary, let secondaryKey {
                guard let top = columnTops.first(where: { $0.key == secondaryKey }),
                      let entries = read(top, desired: desired(secondary.entries, key: secondaryKey), covered: covered)
                else { return false }
                secondaryIDs = entries
            } else {
                secondaryIDs = []
            }
            compact = desiredCompact
        }

        var modalEntries: [String] = []
        if let currentModal, let modalTop {
            guard let entries = read(modalTop, desired: desired(currentModal.entries, key: .modal(currentModal.id)))
            else { return false }
            modalEntries = entries
        }
        if applying && target.modal?.id != currentModal?.id { return false }
        settle(NavigationReading(revision: target.revision, branch: primary.id, entries: primaryIDs,
                                 modalID: currentModal?.id, modalEntries: modalEntries, secondaryEntries: secondaryIDs,
                                 compactColumn: compact), &out)
        return true
    }

    // One collapsed navigation stack holding exactly `entries`, with every witnessed controller in place.
    private func matches(_ top: StackSample, _ entries: [NavigationEntry]) -> Bool {
        guard top.depth == entries.count, top.top == entries.last else { return false }
        for (witnessed, entry) in zip(top.identities, entries) {
            if let witnessed, witnessed != entry.id { return false }
        }
        return true
    }

    private func read(_ top: StackSample, desired: [NavigationEntry], covered: Bool = false) -> [String]? {
        // UIKit reports the presented sheet as the underlying navigation controller's visible controller.
        // Its own top still needs to be mounted in the same window and match the retained path.
        guard let root = desired.first, covered || top.isVisible, top.isInWindow else { return nil }
        // SwiftUI creates hidden destinations lazily. Verify controller count, mounted top,
        // and every controller identity that could previously be witnessed.
        let entries = [root] + (paths[top.key] ?? [])
        guard top.depth == entries.count, top.top == entries.last,
              applying ? desired == entries : desired.starts(with: entries) else { return nil }
        for (witnessed, entry) in zip(top.identities, entries) {
            if let witnessed, witnessed != entry.id { return nil }
        }
        return entries.map(\.id)
    }

    // A reading counts once two consecutive frames agree on it.
    private mutating func settle(_ reading: NavigationReading, _ out: inout SurfaceOutcome) {
        guard reading != lastReading || needsGestureVerification else { return }
        guard candidate == reading else { candidate = reading; return }
        if needsGestureVerification {
            diagnostics.gestureReading = reading
            needsGestureVerification = false
        }
        guard reading != lastReading else { return }
        if let taken = takenBack {
            guard reading == taken.shorter else { return }
            // The page was on screen, so AppNav records the push and then a native Back. Both are reported from
            // this one frame. The presenter submits the Back before the push's operation releases the router, and
            // the router admits it ahead of any operation queued behind the push.
            takenBack = nil
            diagnostics.pushesTakenBack += 1
            commit(taken.staged, &out)
            // UIKit's stack has settled without the page, so its push origin has nothing left to animate.
            pushOrigins[taken.removed] = nil
        }
        commit(reading, &out)
    }

    private mutating func commit(_ reading: NavigationReading, _ out: inout SurfaceOutcome) {
        candidate = nil
        animatedPush = nil
        clearTakeBack()
        lastReading = reading
        if phase != .idle, let target {
            diagnostics.stagedTops = stagedTops
            prunePushOrigins(target)
        }
        phase = .idle
        undoCount = 0
        departingSecondary = nil
        diagnostics.lastReading = reading
        let split = reading.compactColumn.map { " secondary=\(reading.secondaryEntries) compact=\($0.rawValue)" } ?? ""
        out.traces.append("settled rev=\(reading.revision) \(reading.branch) \(reading.entries) modal=\(reading.modalID ?? "-") \(reading.modalEntries)\(split)")
        out.readings.append(reading)
    }

    // MARK: Taken-back pushes

    /// Puts a taken-back push into the path again once its Back gesture is cancelled, whether SwiftUI wrote the
    /// longer path itself or UIKit's stack settled with the page still on top.
    private mutating func restoreTakenBack(_ out: inout SurfaceOutcome, installs: Bool) {
        guard let taken = takenBack else { return }
        takenBack = nil
        takenBackKept = false
        diagnostics.pushesRestored += 1
        backDuringPush = taken.key
        if taken.closesColumn {
            guard compactColumn != .secondary else { return }
            compactColumn = .secondary
            if installs { out.install = InstallAnimation.none }
            return
        }
        guard paths[taken.key] != taken.restored else { return }
        paths[taken.key] = taken.restored
        if installs { out.install = InstallAnimation.none }
    }

    /// The stack a reading must show: the staged one, or the shorter one once a Back took the push back.
    private func desired(_ entries: [NavigationEntry], key: StackKey) -> [NavigationEntry] {
        guard let taken = takenBack, taken.key == key else { return entries }
        return taken.shorterEntries
    }

    /// The staged revision as AppNav projected it.
    private func stagedReading() -> NavigationReading? {
        guard let target else { return nil }
        // A window without a root reports an empty branch, as `witness` does.
        let branch = target.branches.first(where: { $0.id == target.selectedBranch })
        if branch == nil && !target.branches.isEmpty { return nil }
        return NavigationReading(revision: target.revision, branch: branch?.id ?? "",
                                 entries: branch?.entries.map(\.id) ?? [], modalID: target.modal?.id,
                                 modalEntries: target.modal?.entries.map(\.id) ?? [],
                                 secondaryEntries: target.split?.secondary?.entries.map(\.id) ?? [],
                                 compactColumn: target.split?.compactColumn)
    }
}

private extension NavigationReading {
    /// The same reading with the stack under `key` cut back to its root plus `path`.
    func replacing(_ key: StackKey, with path: [NavigationEntry]) -> NavigationReading {
        switch key {
        case .branch:
            NavigationReading(revision: revision, branch: branch, entries: Array(entries.prefix(1)) + path.map(\.id),
                              modalID: modalID, modalEntries: modalEntries, secondaryEntries: secondaryEntries,
                              compactColumn: compactColumn)
        case .modal:
            NavigationReading(revision: revision, branch: branch, entries: entries, modalID: modalID,
                              modalEntries: Array(modalEntries.prefix(1)) + path.map(\.id),
                              secondaryEntries: secondaryEntries, compactColumn: compactColumn)
        case .secondary:
            NavigationReading(revision: revision, branch: branch, entries: entries, modalID: modalID,
                              modalEntries: modalEntries,
                              secondaryEntries: Array(secondaryEntries.prefix(1)) + path.map(\.id),
                              compactColumn: compactColumn)
        }
    }

    /// The same reading after a collapsed window's Back closed the split's secondary column.
    func closingSecondary() -> NavigationReading {
        NavigationReading(revision: revision, branch: branch, entries: entries, modalID: modalID,
                          modalEntries: modalEntries, secondaryEntries: [], compactColumn: .primary)
    }
}

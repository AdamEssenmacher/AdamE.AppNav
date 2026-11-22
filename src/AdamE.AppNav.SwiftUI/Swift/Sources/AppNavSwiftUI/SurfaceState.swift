// The navigation surface's state machine. It has no UIKit or SwiftUI dependency: NativeSurface feeds it
// projections, binding writes, gesture boundaries and per-frame samples of the native stacks, and applies
// what it returns. Every rule about when a revision settles, what a native write may change, and when the
// shell stops waiting lives here, so the rules can be tested on the host without a simulator.

enum StackKey: Hashable {
    case branch(String)
    case modal(String)

    var isModal: Bool {
        if case .modal = self { return true }
        return false
    }
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
    }

    // An animated one-entry push that SwiftUI wrote out of the path before it settled.
    private struct TakenBack {
        let key: StackKey
        let staged: NavigationReading
        let shorter: NavigationReading
        let shorterEntries: [NavigationEntry]
        let restored: [NavigationEntry]
        let removed: NavigationEntry
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
        target = next
        out.install = animation
        return out
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
        return Authority(paths: paths, selectedBranch: projection.selectedBranch,
                         selectedModal: projection.modal.map { ModalSelection(id: $0.id) })
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
                                    modal: projection.modal, transition: projection.transition)
    }

    // What the shell shows is the projection after zero or more native pops, a sheet dismissal, or a tab change.
    private func nativeCouldHaveReached(from projection: NavigationProjection) -> Bool {
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
            lhs.branches == rhs.branches && lhs.modal == rhs.modal
    }

    // MARK: Destination context

    func context(for entry: NavigationEntry, key: StackKey) -> NavigationDestinationContext {
        let transition = pushOrigins[entry].flatMap { $0.key == key ? $0.context : nil } ??
            (target?.transition?.destinationEntryID == entry.id ? target?.transition : nil)
        switch key {
        case .branch(let id):
            return NavigationDestinationContext(entry: entry, branchID: id, modalID: nil, transition: transition)
        case .modal(let id):
            return NavigationDestinationContext(entry: entry, branchID: nil, modalID: id, transition: transition)
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
            let nextIDs = Set((next.branches.flatMap(\.entries) + (next.modal?.entries ?? [])).map(\.id))
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
        if let push, let source = push.old.last, let destination = push.new.last,
           source.mount > 0, destination.mount > 0,
           context.sourceEntryID == source.id, context.destinationEntryID == destination.id {
            pushOrigins[destination] = PushOrigin(key: push.key, sourceKey: push.key, source: source, context: context)
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
        }
    }

    // MARK: Animation eligibility

    private struct VisibleStep {
        let key: StackKey
        let old: [NavigationEntry]
        let new: [NavigationEntry]

        var isPush: Bool { !old.isEmpty && new.count == old.count + 1 && new.starts(with: old) }
        var isPop: Bool { !new.isEmpty && old.count == new.count + 1 && old.starts(with: new) }
    }

    private func settledBranch(to next: NavigationProjection) -> VisibleStep? {
        guard let prior = target, phase == .idle, interaction == nil, let reading = lastReading,
              prior.isBranchHost == next.isBranchHost,
              prior.branches.map(\.id) == next.branches.map(\.id),
              selectedBranch == next.selectedBranch, prior.selectedBranch == next.selectedBranch,
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
        guard let prior = target, let branch = settledBranch(to: next) else { return nil }
        if prior.modal == nil && next.modal == nil { return branch }
        guard prior.branches == next.branches, let old = prior.modal, let new = next.modal,
              old.id == new.id, lastReading?.modalEntries == old.entries.map(\.id),
              paths[.modal(old.id)] == Array(old.entries.dropFirst()) else { return nil }
        return VisibleStep(key: .modal(old.id), old: old.entries, new: new.entries)
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
                                                             selectedModal: selectedModal))
            out.traces.append("native \(what) is not accepted rev=\(target?.revision ?? 0); restoring on the next quiet frame")
        }
    }

    private mutating func abandon(_ reason: String, _ out: inout SurfaceOutcome) {
        out.traces.append("abandoning rev=\(target?.revision ?? 0): \(reason). Releasing native Back and dismissal.")
        phase = .abandoned
        undo = nil
        candidate = nil
        animatedPush = nil
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
        diagnostics.lastReading = reading
        out.traces.append("settled rev=\(reading.revision) \(reading.branch) \(reading.entries) modal=\(reading.modalID ?? "-") \(reading.modalEntries)")
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
                                 modalEntries: target.modal?.entries.map(\.id) ?? [])
    }
}

private extension NavigationReading {
    /// The same reading with the stack under `key` cut back to its root plus `path`.
    func replacing(_ key: StackKey, with path: [NavigationEntry]) -> NavigationReading {
        switch key {
        case .branch:
            NavigationReading(revision: revision, branch: branch, entries: Array(entries.prefix(1)) + path.map(\.id),
                              modalID: modalID, modalEntries: modalEntries)
        case .modal:
            NavigationReading(revision: revision, branch: branch, entries: entries, modalID: modalID,
                              modalEntries: Array(modalEntries.prefix(1)) + path.map(\.id))
        }
    }
}

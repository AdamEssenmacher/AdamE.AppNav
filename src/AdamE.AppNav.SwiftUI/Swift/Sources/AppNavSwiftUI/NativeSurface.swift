import SwiftUI
import UIKit

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

// Resolve each rendered destination's native controller through the responder chain.
// Offscreen controllers keep the identity witnessed while they were visible.
@MainActor
final class MarkerView: UIView {
    weak var surface: NativeSurface?
    var key: StackKey?
    var entry: NavigationEntry?

    override func didMoveToWindow() {
        super.didMoveToWindow()
        if window != nil { surface?.add(self) }
    }
}

struct PageMarker: UIViewRepresentable {
    let surface: NativeSurface
    let key: StackKey
    let entry: NavigationEntry

    func makeUIView(context: Context) -> MarkerView {
        let view = MarkerView()
        view.isUserInteractionEnabled = false
        return view
    }

    func updateUIView(_ view: MarkerView, context: Context) {
        view.surface = surface
        view.key = key
        view.entry = entry
        surface.add(view)
    }
}

// Sample display frames without changing SwiftUI's delegates or gestures.
@MainActor
final class FrameView: UIView {
    weak var surface: NativeSurface?
    private var link: CADisplayLink?

    override func didMoveToWindow() {
        super.didMoveToWindow()
        stop()
        if window != nil {
            let link = CADisplayLink(target: self, selector: #selector(displayTick))
            link.add(to: .main, forMode: .common)
            self.link = link
        }
    }

    func stop() {
        link?.invalidate()
        link = nil
    }

    @objc private func displayTick() { surface?.frame(window: window) }
}

struct FrameObserver: UIViewRepresentable {
    let surface: NativeSurface
    let report: (NavigationReading) -> Void
    let trace: (String) -> Void

    func makeUIView(context: Context) -> FrameView {
        let view = FrameView()
        view.isUserInteractionEnabled = false
        return view
    }
    func updateUIView(_ view: FrameView, context: Context) {
        view.surface = surface
        surface.updateCallbacks(report: report, trace: trace)
    }
    static func dismantleUIView(_ view: FrameView, coordinator: Void) { view.stop() }
}

@MainActor
final class NativeSurface: ObservableObject {
    @Published private(set) var target: NavigationProjection?
    @Published private var paths: [StackKey: [NavigationEntry]] = [:]
    @Published private var selectedBranch = ""
    @Published private var selectedModal: ModalSelection?
    @Published private(set) var diagnostics = NavigationDiagnostics()

    private struct PushOrigin {
        let key: StackKey
        let source: NavigationEntry
        let context: NavigationTransitionContext
    }

    private var pushOrigins: [NavigationEntry: PushOrigin] = [:]
    private var report: ((NavigationReading) -> Void)?
    private var trace: ((String) -> Void)?
    private let markers = NSHashTable<MarkerView>.weakObjects()
    private let identities = NSMapTable<UIViewController, NSString>.weakToStrongObjects()
    private var applying = false
    private var stagedTops: [String] = []
    private var lastReading: NavigationReading?
    private var candidate: NavigationReading?
    private var trackedInteraction: ObjectIdentifier?
    private var needsGestureVerification = false
    private var nextDiagnostic = 0.0
    // The one-entry push being animated for the staged revision, while UIKit can still take it back.
    private var animatedPush: VisibleStep?
    // That push after SwiftUI wrote it out of the path, until UIKit's stack settles without it or a cancelled
    // gesture writes it back.
    private var takenBack: TakenBack?
    // Whether the page that push adds has been seen mounted in the window.
    private var pushWitnessed = false

    private struct TakenBack {
        let key: StackKey
        let staged: NavigationReading
        let shorter: NavigationReading
        let shorterEntries: [NavigationEntry]
        let restored: [NavigationEntry]
        let removed: NavigationEntry
    }

    func updateCallbacks(report: @escaping (NavigationReading) -> Void, trace: @escaping (String) -> Void) {
        self.report = report
        self.trace = trace
    }

    func stage(_ next: NavigationProjection, animatesSinglePushes: Bool,
               transactionAnimation: ((NavigationTransitionContext) -> Animation?)?) {
        if next.revision == target?.revision {
            prunePushOrigins(next)
            target = next
            return
        }
        let step = visibleStep(to: next)
        let singlePush = step?.isPush == true && (next.transition == nil || next.transition?.operation == .push)
        var animation: Animation?
        if let context = next.transition, let transactionAnimation {
            if canAnimate(next, operation: context.operation, step: step) {
                animation = transactionAnimation(context)
            }
        } else if singlePush && animatesSinglePushes {
            animation = .default
        }
        stagePushOrigins(next, push: singlePush ? step : nil)
        animatedPush = singlePush && animation != nil ? step : nil
        takenBack = nil
        pushWitnessed = false
        applying = next.revision > 0
        nextDiagnostic = CACurrentMediaTime() + 1
        candidate = nil
        lastReading = nil
        stagedTops = []
        trackedInteraction = nil
        needsGestureVerification = false
        var transaction = Transaction(animation: animation)
        transaction.disablesAnimations = animation == nil
        withTransaction(transaction) {
            paths = Dictionary(uniqueKeysWithValues: next.branches.map {
                (.branch($0.id), Array($0.entries.dropFirst()))
            })
            if let modal = next.modal {
                paths[.modal(modal.id)] = Array(modal.entries.dropFirst())
            }
            selectedBranch = next.selectedBranch
            selectedModal = next.modal.map { ModalSelection(id: $0.id) }
            target = next
        }
    }

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

    private func stagePushOrigins(_ next: NavigationProjection, push: VisibleStep?) {
        switch next.transition?.operation {
        case .initial, .refresh, .rollback, .reattach:
            pushOrigins.removeAll()
        default:
            let nextIDs = Set((next.branches.flatMap(\.entries) + (next.modal?.entries ?? [])).map(\.id))
            // A removed controller can still be animating out. Preserve its exact match until
            // settlement if the source still belongs to that stack or is departing alongside it.
            pushOrigins = pushOrigins.filter { destination, origin in
                if corresponds(origin, destination: destination, in: next) { return true }
                guard !nextIDs.contains(destination.id),
                      let previous = target, corresponds(origin, destination: destination, in: previous)
                else { return false }
                return !nextIDs.contains(origin.source.id) ||
                    entries(for: origin.key, in: next)?.contains(origin.source) == true
            }
        }
        guard let push, let context = next.transition,
              let source = push.old.last, let destination = push.new.last,
              source.mount > 0, destination.mount > 0,
              context.sourceEntryID == source.id, context.destinationEntryID == destination.id else { return }
        pushOrigins[destination] = PushOrigin(key: push.key, source: source, context: context)
    }

    private func prunePushOrigins(_ projection: NavigationProjection) {
        pushOrigins = pushOrigins.filter { destination, origin in
            corresponds(origin, destination: destination, in: projection)
        }
    }

    private func corresponds(_ origin: PushOrigin, destination: NavigationEntry,
                             in projection: NavigationProjection) -> Bool {
        guard let entries = entries(for: origin.key, in: projection),
              let destinationIndex = entries.firstIndex(of: destination), destinationIndex > 0 else { return false }
        return entries[destinationIndex - 1] == origin.source
    }

    private func entries(for key: StackKey, in projection: NavigationProjection) -> [NavigationEntry]? {
        switch key {
        case .branch(let id):
            return projection.branches.first(where: { $0.id == id })?.entries
        case .modal(let id):
            return projection.modal?.id == id ? projection.modal?.entries : nil
        }
    }

    func matchedPushContext(for entry: NavigationEntry, key: StackKey) -> NavigationTransitionContext? {
        guard let origin = pushOrigins[entry], origin.key == key else { return nil }
        return origin.context
    }

    private struct VisibleStep {
        let key: StackKey
        let old: [NavigationEntry]
        let new: [NavigationEntry]

        var isPush: Bool { !old.isEmpty && new.count == old.count + 1 && new.starts(with: old) }
        var isPop: Bool { !new.isEmpty && old.count == new.count + 1 && old.starts(with: new) }
    }

    private func settledBranch(to next: NavigationProjection) -> VisibleStep? {
        guard let prior = target, !applying, trackedInteraction == nil, let reading = lastReading,
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

    func add(_ marker: MarkerView) { markers.add(marker) }

    var selection: Binding<String> {
        Binding(get: { self.selectedBranch }, set: { next in
            guard !self.applying, self.selectedModal == nil,
                  self.target?.branches.contains(where: { $0.id == next }) == true else { return }
            self.selectedBranch = next
        })
    }

    var modal: Binding<ModalSelection?> {
        Binding(get: { self.selectedModal }, set: { next in
            guard !self.applying, next == nil else { return }
            self.selectedModal = nil
        })
    }

    func isWaitingForTab(_ key: StackKey) -> Bool {
        !applying && target?.isBranchHost == true && key == .branch(selectedBranch) &&
            (lastReading?.branch != selectedBranch || target?.selectedBranch != selectedBranch)
    }

    var modalTop: NavigationEntry? {
        guard let modal = target?.modal, selectedModal?.id == modal.id else { return nil }
        return paths[.modal(modal.id)]?.last ?? modal.entries.first
    }

    func path(for key: StackKey) -> Binding<[NavigationEntry]> {
        Binding(get: { self.paths[key] ?? [] }, set: { next in
            let current = self.paths[key] ?? []
            guard next != current else { return }
            let shorter = next.count < current.count && current.starts(with: next)
            // A cancelled interaction may restore the previously witnessed suffix.
            let witnessed: [String]? = switch key {
            case .branch(let id) where self.lastReading?.branch == id: self.lastReading?.entries
            case .modal(let id) where self.lastReading?.modalID == id: self.lastReading?.modalEntries
            default: nil
            }
            let restore = witnessed?.dropFirst().elementsEqual(next.map(\.id)) == true
            if self.applying, let taken = self.takenBack, taken.key == key, next == taken.restored {
                // The Back gesture was cancelled: the push carries on applying.
                self.restoreTakenBack()
                return
            }
            if self.applying, self.takenBack == nil, shorter, let push = self.animatedPush, push.key == key,
               current == Array(push.new.dropFirst()), next == Array(push.old.dropFirst()),
               self.pushWitnessed, let staged = self.stagedReading() {
                // A Back gesture caught a push the shell was still applying, such as a slow swipe during the zoom.
                // UIKit has usually removed the page by the time SwiftUI writes, so this relies on having seen it.
                // Ignoring the write would make SwiftUI push the page again. Take the shorter path now, and report
                // the push and the shorter stack together once UIKit's stack settles without the page.
                let shorterEntries = [push.new[0]] + next
                self.takenBack = TakenBack(key: key, staged: staged, shorter: staged.replacing(key, with: next),
                                           shorterEntries: shorterEntries, restored: current, removed: push.new[push.new.count - 1])
                self.paths[key] = next
                return
            }
            if !self.applying && (shorter || restore) {
                self.paths[key] = next
            }
        })
    }

    func frame(window: UIWindow?) {
        var qualifying = false
        defer { if !qualifying { candidate = nil } }
        guard let window, let target, target.revision > 0 else { return }

        let attached = markers.allObjects.filter { $0.window === window }
        if let push = animatedPush, !pushWitnessed,
           attached.contains(where: { $0.entry == push.new.last && $0.key == push.key }) {
            pushWitnessed = true
        }
        if target.branches.isEmpty {
            guard attached.isEmpty else { return }
            qualifying = true
            settle(NavigationReading(revision: target.revision, branch: "", entries: [], modalID: nil, modalEntries: []))
            return
        }

        let topControllers = attached.compactMap { marker -> StackTop? in
            guard let (nav, controller) = owner(of: marker), let entry = marker.entry, let key = marker.key else {
                return nil
            }
            // During an interactive zoom UIKit can hit-test a transition container and both
            // pages can be on screen. Observe the active navigation controller before filtering
            // markers down to a settled, hittable top; only settle after its completion.
            if key == .branch(selectedBranch) || selectedModal.map({ key == .modal($0.id) }) == true,
               let transition = nav.transitionCoordinator ?? controller.transitionCoordinator,
               transition.isInteractive {
                track(transition)
            }
            identities.setObject(entry.id as NSString, forKey: controller)
            guard nav.topViewController === controller, !marker.isHidden else { return nil }
            return StackTop(key: key, entry: entry, nav: nav, controller: controller)
        }

        if applying && CACurrentMediaTime() >= nextDiagnostic {
            nextDiagnostic = CACurrentMediaTime() + 2
            let tops = topControllers.map { top in
                "\(top.key):\(top.entry.id):depth=\(top.nav.viewControllers.count):visible=\(top.nav.visibleViewController === top.controller):hit=\(hitTest(top.nav, in: window)):transition=\(top.nav.transitionCoordinator != nil || top.controller.transitionCoordinator != nil)"
            }.joined(separator: ";")
            trace?("waiting rev=\(target.revision) branch=\(selectedBranch) modal=\(selectedModal?.id ?? "-") attached=\(attached.count) tops=[\(tops)]")
        }

        let currentModal = selectedModal.flatMap { selection in target.modal?.id == selection.id ? target.modal : nil }
        let modalMarkers = attached.filter { $0.key?.isModal == true }
        guard
            let branch = target.branches.first(where: { $0.id == selectedBranch }),
            let branchTop = topControllers.first(where: { $0.key == .branch(branch.id) })
        else { return }

        let modalTop: StackTop?
        if let currentModal {
            let key = StackKey.modal(currentModal.id)
            guard modalMarkers.allSatisfy({ $0.key == key }),
                  let visible = topControllers.first(where: { $0.key == key && hitTest($0.nav, in: window) })
            else { return }
            modalTop = visible
        } else {
            // The old sheet must leave before the uncovered branch can be acknowledged.
            guard modalMarkers.isEmpty, hitTest(branchTop.nav, in: window) else { return }
            modalTop = nil
        }

        let top = modalTop ?? branchTop
        if applying, stagedTops.last != top.entry.id { stagedTops.append(top.entry.id) }
        guard transitioned(branchTop), modalTop.map(transitioned) ?? true, trackedInteraction == nil,
              let branchEntries = read(branchTop, desired: desired(branch.entries, key: .branch(branch.id)), in: window,
                                       covered: modalTop != nil) else { return }

        if isWaitingForTab(branchTop.key), branchEntries.count < branch.entries.count {
            // There is no independent witness for a selection followed by a premature pop.
            // Reassert the intact newly selected stack so AppNav can commit the tab change.
            var transaction = Transaction(animation: nil)
            transaction.disablesAnimations = true
            withTransaction(transaction) {
                paths[branchTop.key] = Array(branch.entries.dropFirst())
            }
            return
        }

        let modalEntries: [String]
        if let currentModal, let modalTop {
            guard let entries = read(modalTop, desired: desired(currentModal.entries, key: .modal(currentModal.id)),
                                     in: window) else { return }
            modalEntries = entries
        } else {
            modalEntries = []
        }

        if applying && (target.selectedBranch != branch.id || target.modal?.id != currentModal?.id) { return }
        qualifying = true
        settle(NavigationReading(revision: target.revision, branch: branch.id, entries: branchEntries,
                                 modalID: currentModal?.id, modalEntries: modalEntries))
    }

    private struct StackTop {
        let key: StackKey
        let entry: NavigationEntry
        let nav: UINavigationController
        let controller: UIViewController
    }

    private func transitioned(_ top: StackTop) -> Bool {
        if let transition = top.nav.transitionCoordinator ?? top.controller.transitionCoordinator {
            track(transition)
            return false
        }
        return true
    }

    private func read(_ top: StackTop, desired: [NavigationEntry], in window: UIWindow,
                      covered: Bool = false) -> [String]? {
        // UIKit reports the presented sheet as the underlying navigation controller's visible controller.
        // Its own top still needs to be mounted in the same window and match the retained path.
        guard let root = desired.first, covered || top.nav.visibleViewController === top.controller,
              top.controller.viewIfLoaded?.window === window else { return nil }
        // SwiftUI creates hidden destinations lazily. Verify controller count, mounted top,
        // and every controller identity that could previously be witnessed.
        let entries = [root] + (paths[top.key] ?? [])
        guard top.nav.viewControllers.count == entries.count, top.entry == entries.last,
              applying ? desired == entries : desired.starts(with: entries) else { return nil }
        for (controller, entry) in zip(top.nav.viewControllers, entries) {
            if let witnessed = identities.object(forKey: controller) as String?, witnessed != entry.id { return nil }
        }
        return entries.map(\.id)
    }

    private func owner(of marker: MarkerView) -> (UINavigationController, UIViewController)? {
        var responder: UIResponder? = marker
        while let current = responder {
            if let controller = current as? UIViewController, let nav = controller.navigationController,
               nav.viewControllers.contains(where: { $0 === controller }) { return (nav, controller) }
            responder = current.next
        }
        return nil
    }

    private func hitTest(_ nav: UINavigationController, in window: UIWindow) -> Bool {
        guard let view = nav.viewIfLoaded, view.window === window else { return false }
        let middle = view.convert(CGPoint(x: view.bounds.midX, y: view.bounds.midY), to: window)
        return window.hitTest(middle, with: nil)?.isDescendant(of: view) == true
    }

    private func settle(_ reading: NavigationReading) {
        guard reading != lastReading || needsGestureVerification else { return }
        guard candidate == reading else { candidate = reading; return }
        if needsGestureVerification {
            diagnostics.gestureReading = reading
            needsGestureVerification = false
        }
        guard reading != lastReading else { return }
        if let taken = takenBack {
            guard reading == taken.shorter else { return }
            // The page was on screen, so AppNav records the push and then a native Back. Both are reported
            // together, so no router operation is admitted between them.
            takenBack = nil
            diagnostics.pushesTakenBack += 1
            commit(taken.staged)
            // UIKit's stack has settled without the page, so its push origin has nothing left to animate.
            pushOrigins[taken.removed] = nil
        }
        commit(reading)
    }

    /// Writes a taken-back push into the path again once its Back gesture is cancelled, whether SwiftUI wrote the
    /// longer path itself or UIKit only reported the cancellation.
    private func restoreTakenBack() {
        guard let taken = takenBack else { return }
        takenBack = nil
        diagnostics.pushesRestored += 1
        guard paths[taken.key] != taken.restored else { return }
        var transaction = Transaction(animation: nil)
        transaction.disablesAnimations = true
        withTransaction(transaction) {
            paths[taken.key] = taken.restored
        }
    }

    /// The stack a reading must show: the staged one, or the shorter one once a Back gesture took the push back.
    private func desired(_ entries: [NavigationEntry], key: StackKey) -> [NavigationEntry] {
        guard let taken = takenBack, taken.key == key else { return entries }
        return taken.shorterEntries
    }

    /// The staged revision as AppNav projected it.
    private func stagedReading() -> NavigationReading? {
        guard let target, let branch = target.branches.first(where: { $0.id == target.selectedBranch }) else { return nil }
        return NavigationReading(revision: target.revision, branch: branch.id, entries: branch.entries.map(\.id),
                                 modalID: target.modal?.id, modalEntries: target.modal?.entries.map(\.id) ?? [])
    }

    private func commit(_ reading: NavigationReading) {
        candidate = nil
        animatedPush = nil
        takenBack = nil
        pushWitnessed = false
        lastReading = reading
        if applying, let target {
            diagnostics.stagedTops = stagedTops
            prunePushOrigins(target)
        }
        applying = false
        diagnostics.lastReading = reading
        trace?("settled rev=\(reading.revision) \(reading.branch) \(reading.entries) modal=\(reading.modalID ?? "-") \(reading.modalEntries)")
        report?(reading)
    }

    private func track(_ transition: any UIViewControllerTransitionCoordinator) {
        let token = ObjectIdentifier(transition as AnyObject)
        guard transition.isInteractive, trackedInteraction != token else { return }
        trackedInteraction = token
        let registered = transition.animate(alongsideTransition: nil) { [weak self] context in
            guard let self, trackedInteraction == token else { return }
            if context.isCancelled {
                diagnostics.gesturesCancelled += 1
                // UIKit kept the page. SwiftUI may not write the longer path back, so restore it here.
                restoreTakenBack()
            } else {
                diagnostics.gesturesCompleted += 1
            }
            trackedInteraction = nil
            needsGestureVerification = true
        }
        if registered {
            diagnostics.gesturesBegan += 1
        } else {
            trackedInteraction = nil
        }
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

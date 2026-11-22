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
    // Published so gesture gates in the shell re-render as soon as an apply starts or settles.
    @Published private(set) var applying = false
    private var stagedTops: [String] = []
    private var lastReading: NavigationReading?
    private var candidate: NavigationReading?
    private var trackedInteraction: ObjectIdentifier?
    private var needsGestureVerification = false
    private var nextDiagnostic = 0.0

    func updateCallbacks(report: @escaping (NavigationReading) -> Void, trace: @escaping (String) -> Void) {
        self.report = report
        self.trace = trace
    }

    func stage(_ proposed: NavigationProjection, animatesSinglePushes: Bool,
               transactionAnimation: ((NavigationTransitionContext) -> Animation?)?) {
        let next = selectingKnownBranch(proposed)
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
        } else if transactionAnimation == nil, singlePush && animatesSinglePushes {
            // A supplied policy replaces this default, including for projections without transition metadata.
            animation = .default
        }
        stagePushOrigins(next, push: singlePush ? step : nil)
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

    // Every reading must name a rendered branch. An unknown selection would leave no branch top to witness,
    // so the surface would never settle and would withhold Back indefinitely. TabView shows the first branch.
    private func selectingKnownBranch(_ projection: NavigationProjection) -> NavigationProjection {
        guard let first = projection.branches.first,
              !projection.branches.contains(where: { $0.id == projection.selectedBranch }) else { return projection }
        trace?("unknown selected branch \(projection.selectedBranch) rev=\(projection.revision); using \(first.id)")
        return NavigationProjection(revision: projection.revision, selectedBranch: first.id,
                                    isBranchHost: projection.isBranchHost, branches: projection.branches,
                                    modal: projection.modal, transition: projection.transition)
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
        // A window without a root can still present a sheet; only sheet pages may remain attached.
        if target.branches.isEmpty, !attached.allSatisfy({ $0.key?.isModal == true }) { return }

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
        let branch = target.branches.first(where: { $0.id == selectedBranch })
        let branchTop = branch.flatMap { branch in topControllers.first(where: { $0.key == .branch(branch.id) }) }
        guard target.branches.isEmpty || branchTop != nil else { return }

        let modalTop: StackTop?
        if let currentModal {
            let key = StackKey.modal(currentModal.id)
            guard modalMarkers.allSatisfy({ $0.key == key }),
                  let visible = topControllers.first(where: { $0.key == key && hitTest($0.nav, in: window) })
            else { return }
            modalTop = visible
        } else {
            // The old sheet must leave before the uncovered branch can be acknowledged.
            guard modalMarkers.isEmpty, branchTop.map({ hitTest($0.nav, in: window) }) ?? true else { return }
            modalTop = nil
        }

        if applying, let top = modalTop ?? branchTop, stagedTops.last != top.entry.id { stagedTops.append(top.entry.id) }
        guard branchTop.map(transitioned) ?? true, modalTop.map(transitioned) ?? true, trackedInteraction == nil
        else { return }

        let branchEntries: [String]
        if let branch, let branchTop {
            guard let entries = read(branchTop, desired: branch.entries, in: window, covered: modalTop != nil)
            else { return }
            if isWaitingForTab(branchTop.key), entries.count < branch.entries.count {
                // There is no independent witness for a selection followed by a premature pop.
                // Reassert the intact newly selected stack so AppNav can commit the tab change.
                var transaction = Transaction(animation: nil)
                transaction.disablesAnimations = true
                withTransaction(transaction) {
                    paths[branchTop.key] = Array(branch.entries.dropFirst())
                }
                return
            }
            branchEntries = entries
        } else {
            branchEntries = []
        }

        let modalEntries: [String]
        if let currentModal, let modalTop {
            guard let entries = read(modalTop, desired: currentModal.entries, in: window) else { return }
            modalEntries = entries
        } else {
            modalEntries = []
        }

        if applying && (branch.map({ target.selectedBranch != $0.id }) == true || target.modal?.id != currentModal?.id) {
            return
        }
        qualifying = true
        settle(NavigationReading(revision: target.revision, branch: branch?.id ?? "", entries: branchEntries,
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
            if context.isCancelled { diagnostics.gesturesCancelled += 1 } else { diagnostics.gesturesCompleted += 1 }
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

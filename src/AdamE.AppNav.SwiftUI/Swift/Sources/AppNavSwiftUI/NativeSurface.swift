#if canImport(UIKit)
import SwiftUI
import UIKit

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

// Adapts UIKit and SwiftUI to SurfaceState. It samples the native stacks on each display frame, forwards
// binding writes and gesture boundaries, and commits what the state machine decides. It holds no
// navigation rules of its own.
@MainActor
final class NativeSurface: ObservableObject {
    private var state = SurfaceState()
    private var report: ((NavigationReading) -> Void)?
    private var trace: ((String) -> Void)?
    private var policy: ((NavigationTransitionContext) -> Animation?)?
    private let markers = NSHashTable<MarkerView>.weakObjects()
    private let identities = NSMapTable<UIViewController, NSString>.weakToStrongObjects()

    var target: NavigationProjection? { state.target }
    var diagnostics: NavigationDiagnostics { state.diagnostics }
    var applying: Bool { state.applying }
    var modalTop: NavigationEntry? { state.modalTop }

    func isWaitingForTab(_ key: StackKey) -> Bool { state.isWaitingForTab(key) }

    func context(for entry: NavigationEntry, key: StackKey) -> NavigationDestinationContext {
        state.context(for: entry, key: key)
    }

    func matchedPushContext(for entry: NavigationEntry, key: StackKey) -> NavigationTransitionContext? {
        state.matchedPushContext(for: entry, key: key)
    }

    func updateCallbacks(report: @escaping (NavigationReading) -> Void, trace: @escaping (String) -> Void) {
        self.report = report
        self.trace = trace
    }

    func add(_ marker: MarkerView) { markers.add(marker) }

    func stage(_ proposed: NavigationProjection, animatesSinglePushes: Bool,
               transactionAnimation: ((NavigationTransitionContext) -> Animation?)?) {
        policy = transactionAnimation
        // A gesture can begin between display frames. Look for one now so the state machine holds this
        // revision instead of installing it underneath the gesture.
        if let interaction = activeInteraction() { track(interaction) }
        apply {
            $0.stage(proposed, animatesSinglePushes: animatesSinglePushes, hasPolicy: transactionAnimation != nil,
                     now: CACurrentMediaTime())
        }
    }

    var selection: Binding<String> {
        Binding(get: { self.state.selectedBranch }, set: { next in self.apply { $0.selectBranch(next) } })
    }

    var modal: Binding<ModalSelection?> {
        Binding(get: { self.state.selectedModal }, set: { next in
            guard next == nil else { return }
            self.apply { $0.dismissModal() }
        })
    }

    func path(for key: StackKey) -> Binding<[NavigationEntry]> {
        Binding(get: { self.state.paths[key] ?? [] }, set: { next in self.apply { $0.setPath(next, for: key) } })
    }

    func frame(window: UIWindow?) {
        guard let window else { return }
        let attached = markers.allObjects.filter { $0.window === window }
        var pages: [(marker: MarkerView, nav: UINavigationController, controller: UIViewController,
                     key: StackKey, entry: NavigationEntry)] = []
        var isTransitioning = false
        for marker in attached {
            guard let (nav, controller) = owner(of: marker), let entry = marker.entry, let key = marker.key else {
                continue
            }
            if let transition = nav.transitionCoordinator ?? controller.transitionCoordinator {
                isTransitioning = true
                // During an interactive zoom UIKit can hit-test a transition container and both
                // pages can be on screen. Report the gesture before any top is read.
                if key == .branch(state.selectedBranch) || state.selectedModal.map({ key == .modal($0.id) }) == true {
                    track(transition)
                }
            }
            identities.setObject(entry.id as NSString, forKey: controller)
            pages.append((marker, nav, controller, key, entry))
        }
        let tops = pages.compactMap { page -> StackSample? in
            guard page.nav.topViewController === page.controller, !page.marker.isHidden else { return nil }
            return StackSample(
                key: page.key, top: page.entry, depth: page.nav.viewControllers.count,
                identities: page.nav.viewControllers.map { identities.object(forKey: $0) as String? },
                isVisible: page.nav.visibleViewController === page.controller,
                isInWindow: page.controller.viewIfLoaded?.window === window,
                isHittable: hitTest(page.nav, in: window),
                isTransitioning: (page.nav.transitionCoordinator ?? page.controller.transitionCoordinator) != nil)
        }
        let sample = NativeSample(time: CACurrentMediaTime(), markerKeys: attached.map(\.key), tops: tops,
                                  isTransitioning: isTransitioning)
        apply { $0.sample(sample) }
    }

    // Runs one state transition and commits it. SwiftUI is told only when something it renders changed,
    // so the per-frame bookkeeping does not invalidate the shell.
    private func apply(_ transition: (inout SurfaceState) -> SurfaceOutcome) {
        var next = state
        let outcome = transition(&next)
        let commit = {
            if Rendered(next) != Rendered(self.state) { self.objectWillChange.send() }
            self.state = next
        }
        if let install = outcome.install {
            let animation: Animation? = switch install {
            case .none: nil
            case .standard: .default
            case .policy(let context): policy?(context)
            }
            var transaction = Transaction(animation: animation)
            transaction.disablesAnimations = animation == nil
            withTransaction(transaction, commit)
        } else {
            commit()
        }
        outcome.traces.forEach { trace?($0) }
        outcome.readings.forEach { report?($0) }
    }

    private struct Rendered: Equatable {
        let target: NavigationProjection?
        let paths: [StackKey: [NavigationEntry]]
        let selectedBranch: String
        let selectedModal: ModalSelection?
        let applying: Bool
        let idle: Bool
        let lastReading: NavigationReading?
        let pushOrigins: [NavigationEntry: SurfaceState.PushOrigin]
        let stagedTops: [String]
        let gestures: [Int]
        let gestureReading: NavigationReading?

        init(_ state: SurfaceState) {
            target = state.target
            paths = state.paths
            selectedBranch = state.selectedBranch
            selectedModal = state.selectedModal
            applying = state.applying
            idle = state.phase == .idle
            lastReading = state.lastReading
            pushOrigins = state.pushOrigins
            stagedTops = state.diagnostics.stagedTops
            gestures = [state.diagnostics.gesturesBegan, state.diagnostics.gesturesCancelled,
                        state.diagnostics.gesturesCompleted]
            gestureReading = state.diagnostics.gestureReading
        }
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

    private func track(_ transition: any UIViewControllerTransitionCoordinator) {
        let token = ObjectIdentifier(transition as AnyObject)
        guard transition.isInteractive, state.interaction != token else { return }
        let registered = transition.animate(alongsideTransition: nil) { [weak self] context in
            guard let self else { return }
            var resumes = false
            apply {
                resumes = $0.interactionEnded(token, cancelled: context.isCancelled)
                return SurfaceOutcome()
            }
            if resumes { resumeHeldStage() }
        }
        if registered {
            apply {
                $0.interactionBegan(token)
                return SurfaceOutcome()
            }
        }
    }

    // An interactive transition on any rendered page, including one that began before the frame sampler saw it.
    private func activeInteraction() -> (any UIViewControllerTransitionCoordinator)? {
        for marker in markers.allObjects where marker.window != nil {
            guard let (nav, controller) = owner(of: marker),
                  let transition = nav.transitionCoordinator ?? controller.transitionCoordinator,
                  transition.isInteractive else { continue }
            return transition
        }
        return nil
    }

    private func resumeHeldStage() {
        // Let SwiftUI write the finished gesture back to its bindings before the held revision replaces them.
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            var held: SurfaceState.HeldStage?
            apply {
                held = $0.takeHeld()
                return SurfaceOutcome()
            }
            if let held {
                stage(held.projection, animatesSinglePushes: held.animatesSinglePushes,
                      transactionAnimation: held.hasPolicy ? policy : nil)
            }
        }
    }
}
#endif

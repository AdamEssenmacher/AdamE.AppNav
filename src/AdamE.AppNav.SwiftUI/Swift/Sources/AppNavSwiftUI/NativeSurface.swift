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
    var guardsBack: ((NavigationEntry) -> Bool)? = nil

    func makeUIView(context: Context) -> FrameView {
        let view = FrameView()
        view.isUserInteractionEnabled = false
        return view
    }
    func updateUIView(_ view: FrameView, context: Context) {
        view.surface = surface
        surface.updateCallbacks(report: report, trace: trace)
        surface.guardsBack = guardsBack
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
    /// The app's Back guard. A collapsed split shows its secondary root over the primary column, and SwiftUI keeps
    /// that root's system Back and edge swipe however the page hides its Back button, so the surface enforces it.
    var guardsBack: ((NavigationEntry) -> Bool)?
    private weak var guardedStack: UINavigationController?
    private weak var guardedPage: UIViewController?

    var target: NavigationProjection? { state.target }
    /// The secondary column the detail column renders: the staged one, or one a staged revision is closing.
    /// Whether the secondary root has a page behind it: the split is collapsed on its last held layout.
    var secondaryRootHasBack: Bool { state.heldCollapsed == true }
    var renderedSecondary: NavigationBranchProjection? { state.target?.split?.secondary ?? state.departingSecondary }
    var diagnostics: NavigationDiagnostics { state.diagnostics }
    var applying: Bool { state.applying }
    var modalTop: NavigationEntry? { state.modalTop }

    func isWaitingForTab(_ key: StackKey) -> Bool { state.isWaitingForTab(key) }

    func withholdsBack(_ key: StackKey) -> Bool { state.withholdsBack(key) }

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

    /// The split's `preferredCompactColumn`. Every write is kept; SurfaceState decides what it means.
    var compactColumn: Binding<NavigationSplitColumn> {
        Binding(get: { self.state.compactColumn }, set: { next in self.apply { $0.setCompactColumn(next) } })
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
                if key == .branch(state.selectedBranch) || key.isSecondary ||
                    state.selectedModal.map({ key == .modal($0.id) }) == true {
                    track(transition)
                }
            }
            identities.setObject(entry.id as NSString, forKey: controller)
            pages.append((marker, nav, controller, key, entry))
        }
        guardCollapsedSecondaryRoot(pages.map { ($0.nav, $0.controller, $0.key, $0.entry) })
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
        var sample = NativeSample(time: CACurrentMediaTime(), markerKeys: attached.map(\.key), tops: tops,
                                  isTransitioning: isTransitioning,
                                  mounted: pages.map { MountedPage(key: $0.key, entry: $0.entry) })
        if state.target?.split != nil {
            // A hidden sidebar beside an empty detail column has no page in the window, so look from the root too.
            let split = pages.lazy.compactMap { $0.nav.splitViewController }.first ?? Self.split(in: window.rootViewController)
            if let split {
                if split.transitionCoordinator != nil { sample.isTransitioning = true }
                sample.split = SplitSample(isCollapsed: split.isCollapsed)
            }
        }
        apply { $0.sample(sample) }
    }

    // Hides the system Back and turns off the edge swipe of a collapsed split's navigation stack while its top is a
    // guarded secondary root, so only the app's Back, which asks AppNav, leaves it. Restores the swipe otherwise.
    private func guardCollapsedSecondaryRoot(_ pages: [(UINavigationController, UIViewController, StackKey, NavigationEntry)]) {
        var guarded: (UINavigationController, UIViewController)?
        if let guardsBack, let root = state.target?.split?.secondary?.entries.first, state.heldCollapsed == true {
            for (nav, controller, key, entry) in pages where key.isSecondary && entry == root &&
                nav.topViewController === controller && nav.splitViewController?.isCollapsed == true && guardsBack(entry) {
                guarded = (nav, controller)
            }
        }
        if let previous = guardedStack, previous !== guarded?.0 || guardedPage !== guarded?.1 {
            Self.setBackGestures(of: previous, page: guardedPage, enabled: true)
            guardedStack = nil
            guardedPage = nil
            trace?("restored the native Back gestures of the collapsed split")
        }
        guard let (nav, controller) = guarded else { return }
        if !controller.navigationItem.hidesBackButton { controller.navigationItem.hidesBackButton = true }
        if guardedStack !== nav {
            Self.setBackGestures(of: nav, page: controller, enabled: false)
            guardedStack = nav
            guardedPage = controller
            trace?("guarding the collapsed secondary root: native Back gestures off")
        }
    }

    // The edge swipe, and from iOS 26 the swipe anywhere on the content, pop the navigation stack. A root that arrived
    // with a zoom transition also carries UIKit's interactive zoom dismissal (edge pan, swipe down, pinch) on its own
    // view, which pops it too; UIKit names those recognizers, and the public zoom options are out of SwiftUI's reach.
    private static func setBackGestures(of nav: UINavigationController, page: UIViewController?, enabled: Bool) {
        nav.interactivePopGestureRecognizer?.isEnabled = enabled
        if #available(iOS 26, *) { nav.interactiveContentPopGestureRecognizer?.isEnabled = enabled }
        for recognizer in page?.viewIfLoaded?.gestureRecognizers ?? []
        where recognizer.name?.hasPrefix("com.apple.UIKit.ZoomInteractiveDismiss") == true {
            recognizer.isEnabled = enabled
        }
    }

    private static func split(in root: UIViewController?) -> UISplitViewController? {
        var queue = root.map { [$0] } ?? []
        while !queue.isEmpty {
            let controller = queue.removeFirst()
            if let split = controller as? UISplitViewController { return split }
            queue += controller.children
        }
        return nil
    }

    // Runs one state transition and commits it. SwiftUI is told only when something it renders changed,
    // so the per-frame bookkeeping does not invalidate the shell.
    private func apply(_ transition: (inout SurfaceState) -> SurfaceOutcome) {
        var next = state
        let outcome = transition(&next)
        var animation: Animation?
        if let install = outcome.install {
            animation = switch install {
            case .none: nil
            case .standard: .default
            case .policy(let context): policy?(context)
            }
            // A policy that declines to animate leaves nothing UIKit could take back.
            if case .policy = install, animation == nil { next.installWasNotAnimated() }
        }
        let commit = {
            if Rendered(next) != Rendered(self.state) { self.objectWillChange.send() }
            self.state = next
        }
        if outcome.install != nil {
            var transaction = Transaction(animation: animation)
            transaction.disablesAnimations = animation == nil
            withTransaction(transaction, commit)
        } else {
            commit()
        }
        outcome.traces.forEach { trace?($0) }
        outcome.readings.forEach { report?($0) }
        if outcome.popsCollapsedSecondary { popCollapsedSecondary(animated: outcome.animatesNativePop) }
    }

    // SwiftUI already holds the primary column as the compact column, so only UIKit can pop the secondary column of a
    // collapsed split. Pop the one navigation stack to the topmost controller it holds for the primary column, as a
    // native Back would. SurfaceState asks again on later quiet frames until UIKit shows the primary column.
    private func popCollapsedSecondary(animated: Bool) {
        guard let primary = state.target?.branches.first, state.target?.split != nil else { return }
        var stack: UINavigationController?
        var destination: (index: Int, controller: UIViewController)?
        for marker in markers.allObjects where marker.key == .branch(primary.id) {
            guard let (nav, controller) = owner(of: marker), nav.splitViewController?.isCollapsed == true,
                  let index = nav.viewControllers.firstIndex(where: { $0 === controller }) else { continue }
            if stack == nil { stack = nav }
            guard nav === stack else { continue }
            if index > destination?.index ?? -1 { destination = (index, controller) }
        }
        guard let nav = stack, let destination, nav.transitionCoordinator == nil,
              nav.topViewController !== destination.controller else { return }
        trace?("popping the collapsed secondary column to \(identities.object(forKey: destination.controller) ?? "?")")
        nav.popToViewController(destination.controller, animated: animated)
    }

    private struct Rendered: Equatable {
        let target: NavigationProjection?
        let paths: [StackKey: [NavigationEntry]]
        let selectedBranch: String
        let selectedModal: ModalSelection?
        let compactColumn: NavigationSplitColumn
        let departingSecondary: NavigationBranchProjection?
        let applying: Bool
        let idle: Bool
        let lastReading: NavigationReading?
        let pushOrigins: [NavigationEntry: SurfaceState.PushOrigin]
        let stagedTops: [String]
        let gestures: [Int]
        let gestureReading: NavigationReading?
        let backDuringPush: StackKey?
        let splitCollapsed: Bool?
        let compactColumnRepairs: Int
        let heldCollapsed: Bool?

        init(_ state: SurfaceState) {
            target = state.target
            paths = state.paths
            selectedBranch = state.selectedBranch
            selectedModal = state.selectedModal
            compactColumn = state.compactColumn
            departingSecondary = state.departingSecondary
            applying = state.applying
            idle = state.phase == .idle
            lastReading = state.lastReading
            pushOrigins = state.pushOrigins
            stagedTops = state.diagnostics.stagedTops
            gestures = [state.diagnostics.gesturesBegan, state.diagnostics.gesturesCancelled,
                        state.diagnostics.gesturesCompleted, state.diagnostics.pushesTakenBack,
                        state.diagnostics.pushesRestored]
            backDuringPush = state.backDuringPush
            splitCollapsed = state.diagnostics.splitCollapsed
            compactColumnRepairs = state.diagnostics.compactColumnRepairs
            heldCollapsed = state.heldCollapsed
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

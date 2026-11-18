import SwiftUI
import UIKit
import os

private let gameLog = Logger(subsystem: "com.adamessenmacher.appnav.swiftuiprobe", category: "GameProbe")

struct GameProbeContext: Sendable {
    let namespace: Namespace.ID
    let revision: Int64
    let hasGame: Bool
    let status: String
    let open: @MainActor @Sendable () -> Void
    let openCrossFade: @MainActor @Sendable () -> Void
    let pushReview: @MainActor @Sendable () -> Void
    let trace: @MainActor @Sendable (String) -> Void
}

extension EnvironmentValues {
    @Entry var gameProbe: GameProbeContext? = nil
}

enum GameProbeKind: String {
    case managed
    case native

    var sourceKey: String { self == .managed ? "game-cover" : "native-game-cover" }
    var title: String { self == .managed ? "Play with AppNav" : "Play with NavigationLink" }
    var openIdentifier: String { self == .managed ? "probe-open-game" : "probe-native-game" }
    var pageIdentifier: String { self == .managed ? "page-game" : "page-native-game" }
}

private let homeBackground = Color(red: 16 / 255, green: 37 / 255, blue: 59 / 255)
private let coverBackground = Color(red: 1, green: 92 / 255, blue: 44 / 255)

struct GameRows: View {
    private enum Selection {
        case zoom
        case crossFade
    }

    let context: GameProbeContext
    @State private var selected: Selection?

    var body: some View {
        List {
            Text("home-detail")
                .font(.largeTitle)
                .accessibilityIdentifier("page-home-detail")
                .listRowBackground(homeBackground)

            Button {
                guard selected == nil else { return }
                selected = .zoom
                gameLog.notice("game managed tapped")
                context.trace("game managed tapped")
                context.open()
            } label: {
                row(.managed)
            }
            .buttonStyle(ManagedGameRowStyle(selected: selected == .zoom))
            .accessibilityIdentifier(GameProbeKind.managed.openIdentifier)
            .listRowInsets(EdgeInsets())
            .listRowBackground(homeBackground)

            NavigationLink {
                GameDestination(kind: .native, context: context)
            } label: {
                row(.native)
            }
            .accessibilityIdentifier(GameProbeKind.native.openIdentifier)
            .listRowBackground(homeBackground)

            Button {
                guard selected == nil else { return }
                selected = .crossFade
                context.trace("game crossFade tapped")
                context.openCrossFade()
            } label: {
                Text("Play with Native Transition")
                    .font(.headline)
                    .frame(minHeight: 48)
            }
            .buttonStyle(ManagedGameRowStyle(selected: selected == .crossFade))
            .accessibilityIdentifier("probe-open-game-crossfade")
            .listRowInsets(EdgeInsets())
            .listRowBackground(homeBackground)
        }
        .listStyle(.plain)
        .scrollContentBackground(.hidden)
        .background(homeBackground)
        .foregroundStyle(.white)
        .environment(\.colorScheme, .dark)
        .navigationTitle("home-detail")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { selected = nil }
        .onChange(of: context.revision) {
            if !context.hasGame { selected = nil }
        }
        .onChange(of: context.status) {
            if context.status.hasPrefix("FAULT") { selected = nil }
        }
    }

    private func row(_ kind: GameProbeKind) -> some View {
        HStack(spacing: 16) {
            cover(kind)
            Text(kind.title)
                .font(.headline)
            Spacer(minLength: 4)
            if kind == .managed {
                Image(systemName: "chevron.right")
                    .font(.footnote.bold())
                    .foregroundStyle(.secondary)
            }
        }
        .frame(minHeight: 92)
        .contentShape(Rectangle())
    }

    @ViewBuilder
    private func cover(_ kind: GameProbeKind) -> some View {
        let tile = GameCover()
            .frame(width: 76, height: 76)
            .clipShape(RoundedRectangle(cornerRadius: 8))
        if #available(iOS 18, *) {
            tile.matchedTransitionSource(id: kind.sourceKey, in: context.namespace)
        } else {
            tile
        }
    }
}

private struct ManagedGameRowStyle: ButtonStyle {
    let selected: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .padding(.horizontal, 20)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background {
                if configuration.isPressed || selected {
                    Color.white.opacity(0.17)
                }
            }
            .contentShape(Rectangle())
    }
}

struct GameDestination: View {
    let kind: GameProbeKind
    let context: GameProbeContext

    @ViewBuilder
    var body: some View {
        if kind == .native {
            nativeContent
        } else {
            content
        }
    }

    @ViewBuilder
    private var nativeContent: some View {
        #if compiler(>=6.4)
        if #available(iOS 27, *), ProcessInfo.processInfo.arguments.contains("--probe-native-crossfade") {
            content.modifier(GameChrome(kind: kind, trace: context.trace))
                .navigationTransition(.crossFade)
        } else {
            nativeZoomContent
        }
        #else
        nativeZoomContent
        #endif
    }

    @ViewBuilder
    private var nativeZoomContent: some View {
        if #available(iOS 18, *) {
            content.modifier(GameChrome(kind: kind, trace: context.trace))
                .navigationTransition(.zoom(sourceID: kind.sourceKey, in: context.namespace))
        } else {
            content.modifier(GameChrome(kind: kind, trace: context.trace))
        }
    }

    private var content: some View {
        ZStack {
            coverBackground
            VStack(spacing: 16) {
                Image(systemName: "gamecontroller.fill")
                    .font(.system(size: 84, weight: .bold))
                    .accessibilityHidden(true)
                Text("Game")
                    .font(.largeTitle.bold())
                    .accessibilityIdentifier(kind.pageIdentifier)
            }
            .foregroundStyle(homeBackground)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(GameFrameProbe(channel: "content", kind: kind, trace: context.trace))
        .onAppear {
            gameLog.notice("game \(kind.rawValue, privacy: .public) appear")
            context.trace("game \(kind.rawValue) appear")
        }
    }
}

struct GameChrome: ViewModifier {
    let kind: GameProbeKind
    let trace: @MainActor @Sendable (String) -> Void

    func body(content: Content) -> some View {
        content
            .navigationTitle("Game")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Text("Ready")
                        .accessibilityIdentifier("probe-game-toolbar")
                        .background(GameFrameProbe(channel: "toolbar", kind: kind, trace: trace))
                }
            }
    }
}

private struct GameCover: View {
    var body: some View {
        ZStack {
            coverBackground
            Image(systemName: "gamecontroller.fill")
                .font(.system(size: 28, weight: .bold))
                .foregroundStyle(homeBackground)
        }
        .accessibilityHidden(true)
    }
}

private struct GameFrameProbe: UIViewRepresentable {
    let channel: String
    let kind: GameProbeKind
    let trace: @MainActor @Sendable (String) -> Void

    func makeUIView(context: Context) -> GameFrameView { GameFrameView() }

    func updateUIView(_ view: GameFrameView, context: Context) {
        view.configure(prefix: "game \(kind.rawValue) \(channel)", trace: trace)
    }

    static func dismantleUIView(_ view: GameFrameView, coordinator: Void) { view.stop() }
}

struct FadeSheetFrameProbe: UIViewRepresentable {
    let trace: @MainActor @Sendable (String) -> Void

    func makeUIView(context: Context) -> GameFrameView { GameFrameView() }

    func updateUIView(_ view: GameFrameView, context: Context) {
        view.configure(prefix: "sheet fade content", trace: trace)
    }

    static func dismantleUIView(_ view: GameFrameView, coordinator: Void) { view.stop() }
}

// Capture early UIKit state without changing its delegates. The display-link samples precede
// frame commits; use the simulator recording to verify what was actually drawn.
@MainActor
final class GameFrameView: UIView {
    private var prefix = ""
    private var trace: (@MainActor (String) -> Void)?
    private var link: CADisplayLink?
    private var frames = 0
    private var watchedTransition = false

    func configure(prefix: String, trace: @escaping @MainActor (String) -> Void) {
        self.prefix = prefix
        self.trace = trace
        isUserInteractionEnabled = false
        startIfNeeded()
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        if window == nil {
            stop()
            frames = 0
            watchedTransition = false
        } else {
            startIfNeeded()
        }
    }

    private func startIfNeeded() {
        guard window != nil, trace != nil, link == nil, frames == 0 else { return }
        let link = CADisplayLink(target: self, selector: #selector(sample(_:)))
        link.add(to: .main, forMode: .common)
        self.link = link
    }

    func stop() {
        link?.invalidate()
        link = nil
    }

    @objc private func sample(_ displayLink: CADisplayLink) {
        guard let window else { stop(); return }
        frames += 1
        let controller = owner()
        let transition = controller?.transitionCoordinator ?? controller?.navigationController?.transitionCoordinator
        let boundsInWindow = convert(bounds, to: window)
        let sampledLayers = transition.map { animationEvidence(in: $0.containerView) } ?? ""
        let motionEvidence = transition.map { sheetMotion(in: $0.containerView) } ?? ""
        let message = "\(prefix) frame=\(frames) tick=\(displayLink.timestamp) t=\(CACurrentMediaTime()) " +
            "bounds=\(boundsInWindow) animated=\(transition?.isAnimated == true) interactive=\(transition?.isInteractive == true) " +
            sampledLayers + motionEvidence
        gameLog.notice("\(message, privacy: .public)")
        trace?(message)
        if let transition, !watchedTransition {
            watchedTransition = true
            let framePrefix = prefix
            let log = trace
            transition.animate(alongsideTransition: nil) { context in
                let message = "\(framePrefix) completed t=\(CACurrentMediaTime()) cancelled=\(context.isCancelled)"
                gameLog.notice("\(message, privacy: .public)")
                log?(message)
            }
        }
        if frames == 4 { stop() }
    }

    private func sheetMotion(in container: UIView) -> String {
        guard prefix == "sheet fade content" else { return "" }
        // Find the presented view in the coordinator's container. This excludes the
        // separate dimming and snapshot layers, which may also animate their opacity.
        var sheet: UIView = self
        while let parent = sheet.superview, parent !== container { sheet = parent }
        guard sheet.superview === container, let presented = sheet.layer.presentation() else { return "" }

        let opacity = presented.opacity
        let offset = presented.frame.minY - sheet.layer.frame.minY
        let motion: String
        if abs(offset) < 2, opacity > 0.015, opacity < 0.98 {
            motion = "fade"
        } else if offset > 8, opacity >= 0.98 {
            motion = "slide"
        } else {
            motion = "undetermined"
        }
        return " sheetMotion=\(motion) sheetOpacity=\(opacity) sheetOffsetY=\(offset)"
    }

    private func animationEvidence(in container: UIView) -> String {
        guard ProcessInfo.processInfo.arguments.contains("--probe-animation-style"),
              prefix.hasSuffix(" content"),
              let root = container.layer.presentation() else { return "" }
        var wideLayers: [String] = []
        func sample(_ layer: CALayer, depth: Int) {
            let frame = root.convert(layer.bounds, from: layer)
            if frame.width >= root.bounds.width * 0.72, frame.height >= root.bounds.height * 0.25,
               frame.minX.isFinite, frame.minY.isFinite {
                wideLayers.append("\(depth):\(layer.opacity):\(Int(frame.minX.rounded())):\(Int(frame.minY.rounded()))")
            }
            for child in layer.sublayers ?? [] { sample(child, depth: depth + 1) }
        }
        sample(root, depth: 0)
        return "wideLayers=" + wideLayers.joined(separator: ",")
    }

    private func owner() -> UIViewController? {
        var responder: UIResponder? = self
        while let current = responder {
            if let controller = current as? UIViewController { return controller }
            responder = current.next
        }
        return nil
    }
}

import OahuRuntime
import SwiftUI

/// A SwiftUI window driven by AppNav projections and real UIKit transition observations.
public struct NavigationShell<EmptyContent: View, Footer: View>: View {
    private let projection: NavigationProjection
    private let report: (NavigationReading) -> Void
    private let trace: (String) -> Void
    private let animatesSinglePushes: Bool
    private let back: NavigationBackControl?
    private let customization: NavigationCustomization?
    private let emptyContent: () -> EmptyContent
    private let footer: (NavigationDiagnostics) -> Footer
    @StateObject private var surface = NativeSurface()

    public init(projection: NavigationProjection, report: @escaping (NavigationReading) -> Void,
                trace: @escaping (String) -> Void = { _ in },
                animatesSinglePushes: Bool = false,
                back: NavigationBackControl? = nil,
                customization: NavigationCustomization? = nil,
                @ViewBuilder emptyContent: @escaping () -> EmptyContent,
                @ViewBuilder footer: @escaping (NavigationDiagnostics) -> Footer) {
        self.projection = projection
        self.report = report
        self.trace = trace
        self.animatesSinglePushes = animatesSinglePushes
        self.back = back
        self.customization = customization
        self.emptyContent = emptyContent
        self.footer = footer
    }

    public var body: some View {
        VStack(spacing: 0) {
            if let target = surface.target {
                if target.isBranchHost {
                    TabView(selection: surface.selection) {
                        ForEach(target.branches, id: \.id) { branch in
                            stack(branch.entries, key: .branch(branch.id))
                                .tabItem { Text(branch.title) }
                                .tag(branch.id)
                        }
                    }
                } else if let branch = target.branches.first {
                    stack(branch.entries, key: .branch(branch.id))
                } else {
                    emptyContent()
                }
            } else {
                emptyContent()
            }
            footer(surface.diagnostics)
        }
        .sheet(item: surface.modal) { selection in
            if let modal = surface.target?.modal, modal.id == selection.id {
                sheet(modal)
            }
        }
        .background(FrameObserver(surface: surface, report: report, trace: trace))
        .onChange(of: projection, initial: true) { _, next in
            surface.stage(next, animatesSinglePushes: animatesSinglePushes,
                          transactionAnimation: customization?.transactionAnimation)
        }
    }

    @ViewBuilder
    private func sheet(_ modal: ModalProjection) -> some View {
        let key = StackKey.modal(modal.id)
        let content = stack(modal.entries, key: key, presentsRoot: true)
            .interactiveDismissDisabled(surface.modalTop.map { back?.intercepts($0) == true } ?? false)
        if let root = modal.entries.first, let customization {
            let context = surface.context(for: root, key: key)
            applyTransition(customization.transition(context), to: content, context: context, key: key)
        } else {
            content
        }
    }

    private func stack(_ entries: [NavigationEntry], key: StackKey, presentsRoot: Bool = false) -> some View {
        NavigationStack(path: surface.path(for: key)) {
            if let root = entries.first {
                page(root, key: key, appliesTransition: !presentsRoot)
                    .navigationDestination(for: NavigationEntry.self) { page($0, key: key) }
            }
        }
    }

    private func page(_ entry: NavigationEntry, key: StackKey, appliesTransition: Bool = true) -> some View {
        let intercept = back?.intercepts(entry) == true
        let context = surface.context(for: entry, key: key)
        return styledDestination(context, key: key, appliesTransition: appliesTransition)
        .background(PageMarker(surface: surface, key: key, entry: entry))
        .navigationBarBackButtonHidden(intercept || surface.isWaitingForTab(key))
        .toolbar {
            if intercept, let back {
                ToolbarItem(placement: .topBarLeading) {
                    Button {
                        if !surface.isWaitingForTab(key) { back.request() }
                    } label: {
                        Label("Back", systemImage: "chevron.backward")
                    }
                    .disabled(surface.isWaitingForTab(key))
                    .accessibilityIdentifier("appnav-request-back")
                }
            }
        }
    }

    @ViewBuilder
    private func styledDestination(_ context: NavigationDestinationContext, key: StackKey, appliesTransition: Bool) -> some View {
        let content = OahuMountView(id: context.entry.mount) {
            ContentUnavailableView("Screen unavailable", systemImage: "rectangle.slash")
        }
        if let customization {
            let decorated = customization.chrome?(context, AnyView(content)) ?? AnyView(content)
            if appliesTransition {
                applyTransition(customization.transition(context), to: decorated, context: context, key: key)
            } else {
                decorated
            }
        } else {
            content
        }
    }

    @ViewBuilder
    private func applyTransition(
        _ style: NavigationDestinationTransition, to content: some View,
        context: NavigationDestinationContext, key: StackKey
    ) -> some View {
        let push = surface.matchedPushContext(for: context.entry, key: key)
        switch style {
        case .automatic:
            content
        case let .native(transition):
            if #available(iOS 18, *),
               !transition.requiresSource || push?.sourceKey != nil,
               let nativeStyle = transition.value as? any NavigationTransition {
                #if compiler(>=6.4)
                if #available(iOS 27, *) {
                    content.navigationTransition(AnyNavigationTransition(nativeStyle))
                } else {
                    legacyNativeTransition(nativeStyle, on: content)
                }
                #else
                legacyNativeTransition(nativeStyle, on: content)
                #endif
            } else {
                content
            }
        case let .zoom(sourceID, namespace):
            if #available(iOS 18, *), push?.sourceKey != nil {
                content.navigationTransition(.zoom(sourceID: sourceID, in: namespace))
            } else {
                content
            }
        }
    }

    @available(iOS 18, *)
    private func legacyNativeTransition(_ style: some NavigationTransition, on content: some View) -> AnyView {
        AnyView(content.navigationTransition(style))
    }
}

extension NavigationShell where EmptyContent == EmptyView, Footer == EmptyView {
    public init(projection: NavigationProjection, report: @escaping (NavigationReading) -> Void,
                animatesSinglePushes: Bool = false, back: NavigationBackControl? = nil,
                customization: NavigationCustomization? = nil) {
        self.init(projection: projection, report: report, animatesSinglePushes: animatesSinglePushes, back: back,
                  customization: customization, emptyContent: { EmptyView() }, footer: { _ in EmptyView() })
    }
}

/// Routes Back on selected screens through the app's AppNav Back request. The app also owns
/// any dismissal buttons its screens add.
public struct NavigationBackControl {
    let intercepts: (NavigationEntry) -> Bool
    let request: () -> Void

    public init(intercepts: @escaping (NavigationEntry) -> Bool, request: @escaping () -> Void) {
        self.intercepts = intercepts
        self.request = request
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
}

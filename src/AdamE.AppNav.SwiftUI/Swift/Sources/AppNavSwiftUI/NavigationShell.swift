#if canImport(UIKit)
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
    @State private var presentedModalStyle: NavigationModalPresentation?

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
                } else if let split = target.split, let primary = target.branches.first {
                    splitView(split, primary: primary)
                } else if let branch = target.branches.first(where: { $0.id == target.selectedBranch }) {
                    // The surface only witnesses the selected branch, so a stack root renders that branch.
                    // Its id gives each branch its own NavigationStack instead of rebinding one in place.
                    stack(branch.entries, key: .branch(branch.id))
                        .id(branch.id)
                } else {
                    emptyContent()
                }
            } else {
                emptyContent()
            }
            footer(surface.diagnostics)
        }
        .sheet(item: modalSelection(for: .sheet), onDismiss: { modalDidDismiss(.sheet) }) {
            modalContent($0, style: .sheet)
        }
        .fullScreenCover(item: modalSelection(for: .fullScreenCover),
                         onDismiss: { modalDidDismiss(.fullScreenCover) }) {
            modalContent($0, style: .fullScreenCover)
        }
        .background(FrameObserver(surface: surface, report: report, trace: trace, guardsBack: back?.intercepts))
        .modifier(StageProjection(projection: projection) { next in
            surface.stage(next, animatesSinglePushes: animatesSinglePushes,
                          transactionAnimation: customization?.transactionAnimation)
        })
    }

    private func modalSelection(for style: NavigationModalPresentation) -> Binding<ModalSelection?> {
        guard let modal = surface.target?.modal, modalStyle(for: modal) == style,
              presentedModalStyle == nil || presentedModalStyle == style else {
            return .constant(nil)
        }
        return Binding(get: {
            guard let current = surface.modal.wrappedValue, current.id == modal.id else { return nil }
            return current
        }, set: { next in
            // A dismissed presenter must not clear the selection of a replacement modal.
            if surface.modal.wrappedValue?.id == modal.id { surface.modal.wrappedValue = next }
        })
    }

    private func modalStyle(for modal: ModalProjection) -> NavigationModalPresentation {
        customization?.modalPresentation(modal) ?? .sheet
    }

    private func modalDidDismiss(_ style: NavigationModalPresentation) {
        guard presentedModalStyle == style else { return }
        if let modal = surface.target?.modal, surface.modal.wrappedValue?.id == modal.id,
           modalStyle(for: modal) == style {
            // SwiftUI can replace a modal with another using the same presenter.
            return
        }
        // Wait for the previous presenter to finish dismissing before activating the other one.
        presentedModalStyle = nil
    }

    @ViewBuilder
    private func modalContent(_ selection: ModalSelection, style: NavigationModalPresentation) -> some View {
        if let modal = surface.target?.modal, modal.id == selection.id {
            modalStack(modal)
                .onAppear { presentedModalStyle = style }
        } else {
            // The target can move on before the presenter builds its content. The presenter is still on screen,
            // so record it; otherwise the other presenter could activate before this one dismisses.
            Color.clear
                .onAppear { presentedModalStyle = style }
        }
    }

    @ViewBuilder
    private func modalStack(_ modal: ModalProjection) -> some View {
        let key = StackKey.modal(modal.id)
        let content = stack(modal.entries, key: key, presentsRoot: true)
            // While a revision applies, the modal binding refuses dismissal. A swipe that UIKit
            // completed anyway would leave the surface waiting for a sheet that is gone.
            .interactiveDismissDisabled(surface.applying || surface.modalTop.map { back?.intercepts($0) == true } ?? false)
        if let root = modal.entries.first, let customization {
            let context = surface.context(for: root, key: key)
            applyTransition(customization.transition(context), to: content, context: context, key: key)
        } else {
            content
        }
    }

    // `preferredCompactColumn` needs iOS 17. An iOS 16 build shows the primary column alone, so a split there never
    // settles a secondary column on top; splits need iOS 17.
    @ViewBuilder
    private func splitView(_ split: SplitProjection, primary: NavigationBranchProjection) -> some View {
        if #available(iOS 17, *) {
            systemSplitView(split, primary: primary)
        } else {
            stack(primary.entries, key: .branch(primary.id))
                .id(primary.id)
        }
    }

    // A split renders with the system NavigationSplitView and its automatic column visibility. The Show Sidebar button
    // stays: removing it while the sidebar is hidden leaves no way back to the primary column. The compact column
    // decides what a collapsed window shows on top.
    @available(iOS 17, *)
    private func systemSplitView(_ split: SplitProjection, primary: NavigationBranchProjection) -> some View {
        NavigationSplitView(preferredCompactColumn: compactColumn) {
            stack(primary.entries, key: .branch(primary.id))
        } detail: {
            if let secondary = surface.renderedSecondary, let root = secondary.entries.first {
                // A different selection gets its own NavigationStack rather than rebinding one in place.
                secondaryStack(secondary, root: root)
                    .id(SecondaryIdentity(stack: secondary.id, root: root.id))
            } else {
                emptyContent()
            }
        }
        .id(split.id)
    }

    @available(iOS 17, *)
    private var compactColumn: Binding<NavigationSplitViewColumn> {
        let column = surface.compactColumn
        return Binding(get: { column.wrappedValue == .secondary ? .detail : .sidebar },
                       set: { column.wrappedValue = $0 == .sidebar ? .primary : .secondary })
    }

    // The secondary column's pages use their own destination type. A collapsed split merges both columns into one
    // navigation stack, and a shared type would let the primary column's destination build a secondary page.
    private func secondaryStack(_ secondary: NavigationBranchProjection, root: NavigationEntry) -> some View {
        let key = StackKey.secondary(secondary.id)
        let path = surface.path(for: key)
        return NavigationStack(path: Binding(get: { path.wrappedValue.map(SecondaryPage.init) },
                                             set: { path.wrappedValue = $0.map(\.entry) })) {
            // Collapsed, the root sits over the primary column with the system Back, so a guarded root replaces it
            // like any guarded page. Side by side it has nothing behind it.
            page(root, key: key, isBranchRoot: !surface.secondaryRootHasBack)
                .navigationDestination(for: SecondaryPage.self) { page($0.entry, key: key) }
        }
    }

    private func stack(_ entries: [NavigationEntry], key: StackKey, presentsRoot: Bool = false) -> some View {
        NavigationStack(path: surface.path(for: key)) {
            if let root = entries.first {
                page(root, key: key, appliesTransition: !presentsRoot, isBranchRoot: !presentsRoot)
                    .navigationDestination(for: NavigationEntry.self) { page($0, key: key) }
            }
        }
    }

    private func page(_ entry: NavigationEntry, key: StackKey, appliesTransition: Bool = true,
                      isBranchRoot: Bool = false) -> some View {
        // A branch root has nothing behind it, so it gets no replacement Back button. A sheet root keeps
        // it as the managed close affordance.
        let intercept = !isBranchRoot && back?.intercepts(entry) == true
        let context = surface.context(for: entry, key: key)
        return styledDestination(context, key: key, appliesTransition: appliesTransition)
        .background(PageMarker(surface: surface, key: key, entry: entry))
        // Hiding the button also disables the edge swipe and the Back-history menu.
        .navigationBarBackButtonHidden(intercept || surface.withholdsBack(key) || surface.isWaitingForTab(key))
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
        let content = OahuMountView(id: context.entry.mount) { UnavailableScreen() }
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

private struct SecondaryIdentity: Hashable {
    let stack: String
    let root: String
}

private struct SecondaryPage: Hashable {
    let entry: NavigationEntry
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

/// Stages the projection when the shell appears and again whenever it changes.
private struct StageProjection: ViewModifier {
    let projection: NavigationProjection
    let stage: (NavigationProjection) -> Void

    func body(content: Content) -> some View {
        if #available(iOS 17, *) {
            content.onChange(of: projection, initial: true) { _, next in stage(next) }
        } else {
            content.modifier(LegacyStageProjection(projection: projection, stage: stage))
        }
    }
}

/// iOS 16 staging. It has no initial change action. A second appearance is harmless: staging a revision that is
/// already staged only refreshes its metadata.
/// iOS 16 can run the change action captured by the previous body, so its `stage` would apply the previous
/// animation policy. Both actions call through `latest`, which each body refreshes.
private struct LegacyStageProjection: ViewModifier {
    let projection: NavigationProjection
    let stage: (NavigationProjection) -> Void
    // `@StateObject` builds its value once per shell; `@State` would build and discard one on every init.
    @StateObject private var latest = LatestStage()

    func body(content: Content) -> some View {
        let latest = latest
        let _ = latest.stage = stage
        content
            .onAppear { latest.stage?(projection) }
            .onChange(of: projection) { next in latest.stage?(next) }
    }
}

/// The stage action from the most recent body, for iOS 16's change action. It publishes nothing, so holding it
/// never invalidates the view.
@MainActor
private final class LatestStage: ObservableObject {
    var stage: ((NavigationProjection) -> Void)?
}

/// What a destination shows while its mount is unavailable.
private struct UnavailableScreen: View {
    var body: some View {
        if #available(iOS 17, *) {
            ContentUnavailableView("Screen unavailable", systemImage: "rectangle.slash")
        } else {
            VStack(spacing: 8) {
                Image(systemName: "rectangle.slash")
                    .font(.largeTitle)
                    .foregroundStyle(.secondary)
                Text("Screen unavailable")
                    .font(.headline)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }
}
#endif

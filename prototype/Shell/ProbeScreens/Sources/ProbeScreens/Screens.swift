@_spi(Testing) import AppNavSwiftUI
import OahuRuntime
import SwiftUI

public struct PageScreen: View {
    let model: PageModel

    public init(model: PageModel) { self.model = model }

    public var body: some View {
        if let splitProbe, model.title == "home" {
            SplitHome(context: splitProbe)
        } else if let splitProbe, ["game-a", "game-b"].contains(model.title) {
            SplitGame(title: model.title, context: splitProbe)
        } else if let gameProbe, model.title == "home-detail" {
            GameRows(context: gameProbe)
        } else if let gameProbe, model.title == "game" {
            GameDestination(kind: .managed, context: gameProbe)
        } else {
            defaultPage
        }
    }

    private var defaultPage: some View {
        VStack(spacing: 20) {
            Text(model.title)
                .font(.largeTitle)
                .accessibilityIdentifier("page-" + model.title)
            if model.title == "edit", let gameProbe {
                if #available(iOS 18, *) {
                    reviewButton(gameProbe).matchedTransitionSource(id: "review-cover", in: gameProbe.namespace)
                } else {
                    reviewButton(gameProbe)
                }
            }
            if Self.probesFullScreen, let gameProbe, ["fade-sheet", "review"].contains(model.title) {
                Text(gameProbe.status).accessibilityIdentifier("probe-modal-status")
                Button("Replace modal") {
                    gameProbe.replaceModal(model.title == "fade-sheet" ? "open-modal" : "open-fade-sheet")
                }
                .accessibilityIdentifier(model.title == "fade-sheet" ? "probe-replace-with-sheet" : "probe-replace-with-cover")
            }
            if model.title == "fade-sheet" {
                Button("Dismiss") { dismiss() }
                    .accessibilityIdentifier("probe-dismiss-fade-sheet")
            }
        }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background {
            if model.title == "fade-sheet", let gameProbe {
                Color.orange
                    .background(FadeSheetFrameProbe(trace: gameProbe.trace))
            }
        }
            .overlay {
            if Self.probesFullScreen && model.title == "fade-sheet" {
                GeometryReader { geometry in
                    Color.clear.accessibilityElement()
                        .accessibilityLabel("Modal bounds")
                        .accessibilityIdentifier("probe-modal-bounds")
                        .frame(width: geometry.size.width, height: geometry.size.height)
                }
                .ignoresSafeArea()
                .allowsHitTesting(false)
            }
        }
        .navigationTitle(model.title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                if model.title == "edit" {
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Done") { dismiss() }
                    }
                }
            }
    }

    private func reviewButton(_ context: GameProbeContext) -> some View {
        Button("Review", action: context.pushReview)
            .buttonStyle(.borderedProminent)
            .accessibilityIdentifier("probe-push-review")
    }

    private static let probesFullScreen = ProcessInfo.processInfo.arguments.contains("--probe-full-screen")

    @Environment(\.dismiss) private var dismiss
    @Environment(\.gameProbe) private var gameProbe
    @Environment(\.splitProbe) private var splitProbe
}

public struct ShellScreen: View {
    let model: ShellModel
    @Namespace private var gameZoom
    @State private var animationEvidence = "Waiting for native animation"
    @State private var releasedReview = false
    @State private var gesturesBegan = 0
    @State private var showsHostCover = false
    @State private var showsCenterOverlay = false
    @State private var callbackGeneration = 0
    @State private var reportGeneration = -1
    @State private var traceGeneration = -1
    @State private var clearsPushTransition = false
    @State private var guardsGame = SplitProbeContext.guardsGame

    private var testsImmediatePush: Bool {
        ProcessInfo.processInfo.arguments.contains("--probe-disable-game-push-animation")
    }

    private var testsCallbackReplacement: Bool {
        ProcessInfo.processInfo.arguments.contains("--probe-replace-callbacks")
    }

    private var supportsNativeCrossFade: Bool {
        #if compiler(>=6.4)
        if #available(iOS 27, *) { return true }
        #endif
        return false
    }

    public init(model: ShellModel) { self.model = model }

    private var projection: NavigationProjection {
        let target = model.target.projection
        guard clearsPushTransition else { return target }
        // Drop the transient metadata while preserving the staged revision and all mounts.
        return NavigationProjection(revision: target.revision, selectedBranch: target.selectedBranch,
                                    isBranchHost: target.isBranchHost, branches: target.branches, modal: target.modal,
                                    split: target.split)
    }

    public var body: some View {
        let generation = callbackGeneration
        NavigationShell(projection: projection, report: { reading in
            if testsCallbackReplacement { reportGeneration = generation }
            report(reading)
        }, trace: { message in
            if testsCallbackReplacement && message.hasPrefix("settled ") { traceGeneration = generation }
            model.recordTrace(message)
        },
                        back: NavigationBackControl(intercepts: { $0.id == "home-detail" || (guardsGame && ["game-a", "game"].contains($0.id)) },
                                                    request: { model.runCommand("request-back") }),
                        customization: customization) {
            VStack {
                Spacer()
                Text("No active branch").accessibilityIdentifier("probe-empty")
                Spacer()
            }
        } footer: { diagnostics in
            VStack {
                Text(model.status).accessibilityIdentifier("probe-status")
                Text(evidence(diagnostics.lastReading)).accessibilityIdentifier("probe-native")
                Text(diagnostics.stagedTops.joined(separator: ",")).accessibilityIdentifier("probe-presented")
                Text(gestures(diagnostics)).accessibilityIdentifier("probe-gesture")
                if SplitProbeContext.isEnabled {
                    Text(splitLayout(diagnostics)).accessibilityIdentifier("probe-split-layout")
                }
                if SplitProbeContext.guardsGame && guardsGame {
                    Button("Stop guarding") { guardsGame = false }
                        .accessibilityIdentifier("probe-stop-guarding")
                }
                if ProcessInfo.processInfo.arguments.contains("--probe-animation-style") {
                    Text(animationEvidence).accessibilityIdentifier("probe-animation-style")
                    Text(supportsNativeCrossFade ? "crossFade" : "automatic")
                        .accessibilityIdentifier("probe-crossfade-capability")
                }
                if testsCallbackReplacement {
                    Text("callbacks=\(generation) report=\(reportGeneration) trace=\(traceGeneration)")
                        .accessibilityIdentifier("probe-callback-generations")
                    Button("Replace callbacks") { callbackGeneration += 1 }
                        .accessibilityIdentifier("probe-replace-callbacks")
                }
                if testsImmediatePush && !clearsPushTransition {
                    Button("Clear push metadata") { clearsPushTransition = true }
                        .accessibilityIdentifier("probe-clear-push-metadata")
                }
                if Self.holdsGameSettlement {
                    Text("taken back=\(diagnostics.pushesTakenBack) restored=\(diagnostics.pushesRestored)").accessibilityIdentifier("probe-taken-back")
                        .onChange(of: diagnostics.gesturesBegan, initial: true) { _, count in gesturesBegan = count }
                }
                HStack {
                    Button("Branch deep link") { model.runCommand("deep-link") }
                        .accessibilityIdentifier("probe-link")
                    Button("Open sheet") { model.runCommand("open-modal") }
                        .accessibilityIdentifier("probe-open-modal")
                    Button("Allow Back") { model.runCommand("allow-back") }
                        .accessibilityIdentifier("probe-allow-back")
                }
                Button("Open fade sheet") { model.runCommand("open-fade-sheet") }
                    .accessibilityIdentifier("probe-open-fade-sheet")
                if ProcessInfo.processInfo.arguments.contains("--probe-host-presentation") {
                    Button("Push behind alert") { model.runCommand("push-behind-alert") }
                        .accessibilityIdentifier("probe-push-behind-alert")
                    Button("Show host cover") { showsHostCover = true }
                        .accessibilityIdentifier("probe-show-host-cover")
                }
                if ProcessInfo.processInfo.arguments.contains("--probe-center-overlay") {
                    HStack {
                        Button(showsCenterOverlay ? "Hide center overlay" : "Show center overlay") {
                            showsCenterOverlay.toggle()
                        }
                        .accessibilityIdentifier("probe-toggle-center-overlay")
                        Button("Push behind overlay") { model.runCommand("open-game") }
                            .accessibilityIdentifier("probe-push-behind-overlay")
                    }
                }
                if ProcessInfo.processInfo.arguments.contains("--probe-replace-host") {
                    Button(model.status.contains("replacement=held") ? "Release observation" : "Replace during push") {
                        model.runCommand("replace-during-push")
                    }
                    .accessibilityIdentifier("probe-replace-host")
                }
            }
            .font(.caption)
        }
        .overlay {
            if showsCenterOverlay {
                GeometryReader { geometry in
                    Color.black.opacity(0.25)
                        .frame(width: geometry.size.width - 60, height: geometry.size.height * 0.5)
                        .position(x: geometry.size.width / 2, y: geometry.size.height * 0.4)
                        .accessibilityHidden(true)
                }
            }
        }
        .environment(\.appNavHoldsSettlement, holdsSettlement)
        .fullScreenCover(isPresented: $showsHostCover) {
            VStack {
                Text(model.status).accessibilityIdentifier("probe-cover-status")
                Button("Deep link") { model.runCommand("deep-link") }
                    .accessibilityIdentifier("probe-cover-link")
                Button("Dismiss") { showsHostCover = false }
                    .accessibilityIdentifier("probe-dismiss-host-cover")
            }
        }
        .environment(\.gameProbe, GameProbeContext(
            namespace: gameZoom, revision: model.target.revision,
            hasGame: model.target.branches.contains { $0.entries.contains { $0.entryId == "game" } },
            status: model.status, open: { model.runCommand("open-game") },
            openCrossFade: { model.runCommand("open-game-crossfade") },
            pushReview: { model.runCommand("push-review") },
            replaceModal: { model.runCommand($0) }, trace: gameTrace))
        .environment(\.splitProbe, SplitProbeContext.isEnabled
            ? SplitProbeContext(namespace: gameZoom, command: { model.runCommand($0) }) : nil)
    }

    private var holdsSettlement: Bool {
        if Self.holdsReviewSettlement && projection.modal?.entries.last?.id == "review-again" {
            return !releasedReview
        }
        return Self.holdsGameSettlement && gesturesBegan == 0 &&
            (model.target.branches.contains { $0.entries.last?.entryId == "game" } ||
                model.target.split.secondaryEntries.last?.entryId.hasPrefix("game-") == true)
    }

    private func splitLayout(_ diagnostics: NavigationDiagnostics) -> String {
        let layout = diagnostics.splitCollapsed.map { $0 ? "collapsed" : "expanded" } ?? "none"
        return "\(layout) repairs=\(diagnostics.compactColumnRepairs) taken back=\(diagnostics.pushesTakenBack) restored=\(diagnostics.pushesRestored)"
    }

    private static let holdsReviewSettlement = ProcessInfo.processInfo.arguments.contains("--probe-hold-review-settlement")

    /// Keeps a Game push applying, so a UI test can go Back before the shell settles it.
    static let holdsGameSettlement = ProcessInfo.processInfo.arguments.contains("--probe-hold-game-settlement")

    private func gameTrace(_ message: String) {
        model.recordTrace(message)
        guard ProcessInfo.processInfo.arguments.contains("--probe-animation-style"),
              message.contains("animated=true interactive=false") else { return }
        if message.hasPrefix("game managed content frame="), animationEvidence != "Animated native Game push" {
            animationEvidence = "Animated native Game push"
        }
        if message.hasPrefix("sheet fade content frame="),
           let style = message.split(separator: " ").first(where: { $0 == "sheetMotion=fade" || $0 == "sheetMotion=slide" }),
           animationEvidence != String(style) {
            animationEvidence = String(style)
        }
    }

    private var customization: NavigationCustomization {
        NavigationCustomization(
            modalPresentation: { modal in
                modal.id == "fade-sheet" && ProcessInfo.processInfo.arguments.contains("--probe-full-screen")
                    ? .fullScreenCover : .sheet
            },
            transactionAnimation: { context in
                if testsImmediatePush && coverKey(for: context) == "game-cover" { return nil }
                guard context.operation == .pop || coverKey(for: context) != nil || isCrossFadePush(context) ||
                      (context.operation == .modalPresent && context.destinationEntryID == "fade-sheet")
                else { return nil }
                model.recordTrace("animation policy \(context.operation.rawValue) \(context.sourceEntryID ?? "-") -> \(context.destinationEntryID ?? "-")")
                return .default
            },
            transition: { destination in
                #if compiler(>=6.4)
                if #available(iOS 27, *), supportsNativeCrossFade, destination.modalID == "fade-sheet",
                   destination.entry.id == "fade-sheet" {
                    model.recordTrace("native transition crossFade fade-sheet")
                    return .native(.crossFade)
                }
                #endif
                guard let context = destination.transition else { return .automatic }
                if isCrossFadePush(context) {
                    #if compiler(>=6.4)
                    if #available(iOS 27, *), supportsNativeCrossFade {
                        model.recordTrace("native transition crossFade game")
                        return .native(.crossFade)
                    }
                    #endif
                    model.recordTrace("native transition automatic game")
                }
                if let key = coverKey(for: context) {
                    return .zoom(sourceID: key, namespace: gameZoom)
                }
                return .automatic
            },
            chrome: { destination, content in
                if Self.holdsReviewSettlement && destination.entry.id == "review-again" {
                    return AnyView(content.overlay(alignment: .bottom) {
                        VStack {
                            Text(model.status).accessibilityIdentifier("probe-modal-status")
                            if !releasedReview {
                                Button("Release review") { releasedReview = true }
                                    .accessibilityIdentifier("probe-release-review")
                            }
                        }
                    })
                }
                guard destination.entry.id == "game" else { return content }
                return AnyView(content
                    .modifier(GameChrome(kind: .managed, trace: { model.recordTrace($0) }))
                    .overlay(alignment: .bottom) {
                        if testsImmediatePush && clearsPushTransition {
                            Text(destination.transition?.sourceKey ?? "no matched source")
                                .accessibilityIdentifier("probe-retained-push-source")
                        }
                    })
            }
        )
    }

    private func coverKey(for context: NavigationTransitionContext) -> String? {
        if let destination = context.destinationScreen, ["push", "replace"].contains(context.operation.rawValue),
           context.sourceKey == SplitProbeContext.coverKey(destination) {
            // Selecting a game from its Home row zooms from the row's cover when it pushes over a collapsed Home.
            return context.operation == .push ? context.sourceKey : nil
        }
        guard context.operation == .push else { return nil }
        switch (context.sourceScreen, context.destinationScreen) {
        case ("home-detail", "game") where context.sourceKey == "game-cover",
             ("edit", "review") where context.sourceKey == "review-cover":
            return context.sourceKey
        default: return nil
        }
    }

    private func isCrossFadePush(_ context: NavigationTransitionContext) -> Bool {
        context.operation == .push && context.sourceScreen == "home-detail" &&
            context.destinationScreen == "game" && context.sourceKey == "game-crossfade"
    }

    private func report(_ reading: NavigationReading) {
        model.report(NativeObservation(revision: reading.revision, branch: reading.branch,
                                       entries: reading.entries, modalId: reading.modalID ?? "",
                                       modalEntries: reading.modalEntries, secondaryEntries: reading.secondaryEntries,
                                       compactColumn: reading.compactColumn?.rawValue ?? ""))
    }

    private func evidence(_ reading: NavigationReading?) -> String {
        guard let reading else { return "Waiting for native surface" }
        var branch = "rev=\(reading.revision) \(reading.branch) [\(reading.entries.joined(separator: ","))]"
        if let compact = reading.compactColumn {
            branch += " + [\(reading.secondaryEntries.joined(separator: ","))] @\(compact.rawValue)"
        }
        guard let modal = reading.modalID else { return branch }
        return "\(branch) \(modal) [\(reading.modalEntries.joined(separator: ","))]"
    }

    private func gestures(_ diagnostics: NavigationDiagnostics) -> String {
        let count = diagnostics.gesturesCancelled + diagnostics.gesturesCompleted
        let summary = "began=\(diagnostics.gesturesBegan) cancelled=\(diagnostics.gesturesCancelled) completed=\(diagnostics.gesturesCompleted)"
        guard let reading = diagnostics.gestureReading else { return summary }
        return "\(summary) settled=\(count) [\(reading.entries.joined(separator: ","))]"
    }
}

private extension ShellTarget {
    var projection: NavigationProjection {
        NavigationProjection(
            revision: revision, selectedBranch: selectedBranch, isBranchHost: isBranchHost,
            branches: branches.map {
                NavigationBranchProjection(id: $0.branch, title: $0.title, entries: $0.entries.map(\.projection))
            },
            modal: modalId.isEmpty ? nil : ModalProjection(id: modalId, entries: modalEntries.map(\.projection)),
            transition: transition.context,
            split: split.splitId.isEmpty ? nil : SplitProjection(
                id: split.splitId,
                secondary: split.secondaryId.isEmpty ? nil : NavigationBranchProjection(
                    id: split.secondaryId, title: split.secondaryId, entries: split.secondaryEntries.map(\.projection)),
                compactColumn: Self.column(split.compactColumn)))
    }
}

private extension ShellTarget {
    // The managed side sends only these values; anything else is a broken contract.
    static func column(_ value: String) -> NavigationSplitColumn {
        guard let column = NavigationSplitColumn(rawValue: value) else {
            preconditionFailure("Unknown split column '\(value)'.")
        }
        return column
    }
}

private extension PresentationTransition {
    var context: NavigationTransitionContext? {
        guard let operation = NavigationTransitionContext.Operation(rawValue: operation) else { return nil }
        return NavigationTransitionContext(
            operation: operation,
            sourceEntryID: sourceEntryId.isEmpty ? nil : sourceEntryId,
            destinationEntryID: destinationEntryId.isEmpty ? nil : destinationEntryId,
            sourceKey: sourceKey.isEmpty ? nil : sourceKey,
            sourceScreen: sourceScreen.isEmpty ? nil : sourceScreen,
            destinationScreen: destinationScreen.isEmpty ? nil : destinationScreen)
    }
}

private extension ScreenEntry {
    var projection: NavigationEntry { NavigationEntry(id: entryId, mount: mount) }
}

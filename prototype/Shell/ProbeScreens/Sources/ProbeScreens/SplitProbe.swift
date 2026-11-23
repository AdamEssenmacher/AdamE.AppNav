import SwiftUI

/// Commands and the shared zoom namespace for the split probe, which shows Home beside the selected game's stack.
struct SplitProbeContext {
    let namespace: Namespace.ID
    let command: @MainActor (String) -> Void

    static let isEnabled = ProcessInfo.processInfo.arguments.contains("--probe-split")
    /// Game A's root asks AppNav, whose policy cancels Back from it until Allow Back.
    static let guardsGame = ProcessInfo.processInfo.arguments.contains("--probe-guard-game")

    /// The matched source key of a game's cover on its Home row.
    static func coverKey(_ game: String) -> String { "split-cover-\(game)" }
}

extension EnvironmentValues {
    @Entry var splitProbe: SplitProbeContext? = nil
}

/// Home in the primary column. Its rows are plain buttons that ask AppNav to select a game: a `List(selection:)`
/// would write the selection natively and fight the split's compact column.
struct SplitHome: View {
    let context: SplitProbeContext

    var body: some View {
        List {
            ForEach(["game-a", "game-b"], id: \.self) { game in
                Button {
                    context.command("select-\(game)")
                } label: {
                    HStack(spacing: 16) {
                        cover(game)
                        Text(game == "game-a" ? "Game A" : "Game B")
                    }
                }
                .accessibilityIdentifier("probe-select-\(game)")
            }
            Button("More Home") { context.command("push-home-page") }
                .accessibilityIdentifier("probe-push-home-page")
        }
        .accessibilityIdentifier("page-home")
        .navigationTitle("Home")
    }
}

extension SplitHome {
    @ViewBuilder
    private func cover(_ game: String) -> some View {
        let shape = RoundedRectangle(cornerRadius: 10)
            .fill(game == "game-a" ? Color.orange : Color.teal)
            .frame(width: 56, height: 56)
        if #available(iOS 18, *) {
            shape.matchedTransitionSource(id: SplitProbeContext.coverKey(game), in: context.namespace)
        } else {
            shape
        }
    }
}

/// A game's root in the secondary column.
struct SplitGame: View {
    let title: String
    let context: SplitProbeContext

    var body: some View {
        VStack(spacing: 20) {
            Text(title)
                .font(.largeTitle)
                .accessibilityIdentifier("page-" + title)
            Button("Push page") { context.command("push-game-page") }
                .accessibilityIdentifier("probe-push-game-page")
            Button(title == "game-a" ? "Open Game B" : "Open Game A") {
                context.command(title == "game-a" ? "switch-game-b" : "switch-game-a")
            }
            .accessibilityIdentifier("probe-switch-game")
            Button("AppNav Back") { context.command("request-back") }
                .accessibilityIdentifier("probe-split-back")
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(title == "game-a" ? Color.orange.opacity(0.2) : Color.teal.opacity(0.2))
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
    }
}

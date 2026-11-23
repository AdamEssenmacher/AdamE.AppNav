import XCTest
@testable import AppNavSwiftUI

private func entry(_ id: String) -> NavigationEntry {
    NavigationEntry(id: id, mount: Int64(abs(id.hashValue % 1000)) + 1)
}

/// Home in the primary column and, optionally, a game's stack in the secondary column.
private func split(_ revision: Int64, home: [String] = ["home"], game: [String]? = nil,
                   compact: NavigationSplitColumn? = nil, modal: [String]? = nil,
                   transition: NavigationTransitionContext? = nil) -> NavigationProjection {
    let secondary = game.map { NavigationBranchProjection(id: "\($0[0])-stack", title: "", entries: $0.map(entry)) }
    return NavigationProjection(
        revision: revision, selectedBranch: "home-stack", isBranchHost: false,
        branches: [NavigationBranchProjection(id: "home-stack", title: "", entries: home.map(entry))],
        modal: modal.map { ModalProjection(id: "sheet", entries: $0.map(entry)) }, transition: transition,
        split: SplitProjection(id: "games", secondary: secondary,
                               compactColumn: compact ?? (secondary == nil ? .primary : .secondary)))
}

/// Drives a SurfaceState with frames from a model of UIKit's split controller.
private struct SplitHarness {
    var state = SurfaceState()
    var time = 0.0
    var collapsed = true
    var readings: [NavigationReading] = []
    var traces: [String] = []
    var installs: [InstallAnimation] = []
    var nativePops = 0

    mutating func absorb(_ outcome: SurfaceOutcome) {
        readings += outcome.readings
        traces += outcome.traces
        if let install = outcome.install { installs.append(install) }
        if outcome.popsCollapsedSecondary { nativePops += 1 }
    }

    mutating func stage(_ projection: NavigationProjection, hasPolicy: Bool = false) {
        absorb(state.stage(projection, animatesSinglePushes: false, hasPolicy: hasPolicy, now: time))
    }

    var primary: [NavigationEntry] {
        guard let home = state.target?.branches.first, let root = home.entries.first else { return [] }
        return [root] + (state.paths[.branch(home.id)] ?? [])
    }

    var secondary: (key: StackKey, entries: [NavigationEntry])? {
        guard let game = state.target?.split?.secondary, let root = game.entries.first else { return nil }
        return (.secondary(game.id), [root] + (state.paths[.secondary(game.id)] ?? []))
    }

    /// A frame in which UIKit shows the shell's paths. Collapsed, it shows the secondary column on top when
    /// `secondaryOnTop` says so, which defaults to the binding.
    func faithfulSample(secondaryOnTop: Bool? = nil, primaryInWindow: Bool = true, hittable: Bool = true) -> NativeSample {
        var tops: [StackSample] = []
        let home = primary
        func sample(_ key: StackKey, _ entries: [NavigationEntry]) -> StackSample {
            StackSample(key: key, top: entries[entries.count - 1], depth: entries.count,
                        identities: entries.map { Optional($0.id) }, isHittable: hittable)
        }
        if collapsed {
            if let secondary, secondaryOnTop ?? (state.compactColumn == .secondary) {
                tops.append(sample(secondary.key, home + secondary.entries))
            } else if !home.isEmpty {
                tops.append(sample(.branch("home-stack"), home))
            }
        } else {
            if primaryInWindow, !home.isEmpty { tops.append(sample(.branch("home-stack"), home)) }
            if let secondary { tops.append(sample(secondary.key, secondary.entries)) }
        }
        if let modal = state.target?.modal, state.selectedModal?.id == modal.id, let root = modal.entries.first {
            tops.append(sample(.modal(modal.id), [root] + (state.paths[.modal(modal.id)] ?? [])))
        }
        return NativeSample(time: time, markerKeys: tops.map(\.key), tops: tops,
                            mounted: tops.map { MountedPage(key: $0.key, entry: $0.top) },
                            split: SplitSample(isCollapsed: collapsed))
    }

    mutating func frame(_ sample: NativeSample? = nil) {
        time += 1.0 / 60
        var next = sample ?? faithfulSample()
        next.time = time
        absorb(state.sample(next))
    }

    mutating func frames(_ count: Int = 3, secondaryOnTop: Bool? = nil) {
        for _ in 0..<count { frame(faithfulSample(secondaryOnTop: secondaryOnTop)) }
    }
}

final class SplitSurfaceStateTests: XCTestCase {
    private let selection = NavigationTransitionContext(operation: .push, sourceEntryID: "home", destinationEntryID: "game",
                                                        sourceKey: "cover", sourceScreen: "home", destinationScreen: "game")

    func testCollapsedAndExpandedLayoutsReadTheSameColumns() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game", "page"]))
        h.frame()
        XCTAssertTrue(h.readings.isEmpty, "a layout must hold for a frame before it is read")
        h.frames()
        let reading = NavigationReading(revision: 1, branch: "home-stack", entries: ["home"], modalID: nil,
                                        modalEntries: [], secondaryEntries: ["game", "page"], compactColumn: .secondary)
        XCTAssertEqual(h.readings, [reading])

        h.collapsed = false
        h.frames(5)
        h.collapsed = true
        h.frames(5)
        XCTAssertEqual(h.readings, [reading], "folding and unfolding change nothing AppNav sees")
        XCTAssertEqual(h.state.diagnostics.splitCollapsed, true)
    }

    func testCollapsedReadingNeedsTheStagedColumnOnTop() {
        var h = SplitHarness()
        h.stage(split(1))
        h.frames()
        h.stage(split(2, game: ["game"]))
        XCTAssertEqual(h.state.compactColumn, .secondary)
        for _ in 0..<4 { h.frame(h.faithfulSample(secondaryOnTop: false)) }
        XCTAssertEqual(h.readings.count, 1, "Home alone does not settle a selection a collapsed window shows on top")
        h.frames()
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game"])
        XCTAssertEqual(h.readings.last?.compactColumn, .secondary)
    }

    func testSelectionOverHomeIsAMatchedOneEntryPush() {
        var h = SplitHarness()
        h.stage(split(1))
        h.frames()
        h.stage(split(2, game: ["game"], transition: selection), hasPolicy: true)
        XCTAssertEqual(h.installs.last, .policy(selection))
        XCTAssertEqual(h.state.matchedPushContext(for: entry("game"), key: .secondary("game-stack")), selection)
        XCTAssertEqual(h.state.context(for: entry("game"), key: .secondary("game-stack")).splitColumn, .secondary)
        h.frames()
        XCTAssertEqual(h.readings.last?.revision, 2)
        XCTAssertNotNil(h.state.matchedPushContext(for: entry("game"), key: .secondary("game-stack")))
        // Unfolded, the row shows beside its game, so the zoom goes.
        h.collapsed = false
        h.frames()
        XCTAssertNil(h.state.matchedPushContext(for: entry("game"), key: .secondary("game-stack")))
    }

    func testSelectionBesideHomeDoesNotZoom() {
        var h = SplitHarness()
        h.collapsed = false
        h.stage(split(1))
        h.frames()
        h.stage(split(2, game: ["game"], transition: selection), hasPolicy: true)
        XCTAssertNil(h.state.matchedPushContext(for: entry("game"), key: .secondary("game-stack")))
        h.frames()
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game"])
    }

    func testSwitchingGamesInstallsWithoutAnimation() {
        var h = SplitHarness()
        h.stage(split(1, game: ["a"]))
        h.frames()
        let replace = NavigationTransitionContext(operation: .replace, sourceEntryID: "a", destinationEntryID: "b")
        h.stage(split(2, game: ["b"], transition: replace), hasPolicy: true)
        XCTAssertEqual(h.installs.last, InstallAnimation.none)
        h.frames()
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["b"])
        for collapsed in [false, true] {
            h.collapsed = collapsed
            h.stage(split(h.state.target!.revision + 1, game: [collapsed ? "c" : "d"]))
            h.frames()
            XCTAssertEqual(h.readings.last?.secondaryEntries, [collapsed ? "c" : "d"])
        }
    }

    // SwiftUI writes the primary column when the secondary path empties, even though the secondary root stays on top.
    // Writing the secondary column back while collapsed pushes that root again, so the shell keeps the write.
    func testStrayCompactColumnIsKeptWhileCollapsedAndRepairedWhenExpanded() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game", "page"]))
        h.frames()
        h.absorb(h.state.setCompactColumn(.primary))
        h.absorb(h.state.setPath([], for: .secondary("game-stack")))
        // UIKit keeps the game's root on top.
        h.frames(secondaryOnTop: true)
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game"], "the pop is a reading; the binding write is not")
        XCTAssertEqual(h.readings.last?.compactColumn, .secondary)
        XCTAssertEqual(h.state.compactColumn, .primary)

        // AppNav accepts the pop; restaging the column the window already shows on top must not write it.
        h.stage(split(1, game: ["game"]))
        XCTAssertEqual(h.state.compactColumn, .primary)
        h.stage(split(2, game: ["game", "next"]))
        XCTAssertEqual(h.state.compactColumn, .primary, "a push over the stray binding leaves it alone while collapsed")
        h.frames(secondaryOnTop: true)
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game", "next"])

        h.collapsed = false
        h.frames()
        XCTAssertEqual(h.state.compactColumn, .secondary, "both columns show, so the staged column goes back")
        XCTAssertEqual(h.state.diagnostics.compactColumnRepairs, 1)
        XCTAssertEqual(h.readings.count, 3, "repairing the binding is not a reading")
    }

    func testCollapsedBackFromTheSecondaryRootClosesTheColumn() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game"]))
        h.frames()
        h.absorb(h.state.setCompactColumn(.primary))
        h.frames()
        XCTAssertEqual(h.readings.last, NavigationReading(revision: 1, branch: "home-stack", entries: ["home"], modalID: nil,
                                                          modalEntries: [], secondaryEntries: [], compactColumn: .primary))
        // AppNav reconciles to a split without a secondary column under the same revision.
        let installs = h.installs.count
        h.stage(split(1))
        XCTAssertFalse(h.state.applying)
        XCTAssertEqual(h.installs.count, installs, "the restage leaves the native stacks alone")
    }

    func testClosingTheColumnOverAStrayBindingPopsNatively() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game", "page"]))
        h.frames()
        h.absorb(h.state.setCompactColumn(.primary))
        h.absorb(h.state.setPath([], for: .secondary("game-stack")))
        h.frames(secondaryOnTop: true)
        h.stage(split(1, game: ["game"]))
        h.stage(split(2))
        XCTAssertEqual(h.nativePops, 1)
        XCTAssertEqual(h.state.departingSecondary?.id, "game-stack", "the detail keeps the game while it pops")
        h.frame(h.faithfulSample(secondaryOnTop: true))
        h.frames()
        XCTAssertEqual(h.readings.last?.revision, 2)
        XCTAssertEqual(h.readings.last?.secondaryEntries, [])
        XCTAssertNil(h.state.departingSecondary)
    }

    func testSwitchingGamesOverAStrayBindingPopsThenShowsTheNewGame() {
        var h = SplitHarness()
        h.stage(split(1, game: ["a", "page"]))
        h.frames()
        h.absorb(h.state.setCompactColumn(.primary))
        h.absorb(h.state.setPath([], for: .secondary("a-stack")))
        h.frames(secondaryOnTop: true)
        h.stage(split(1, game: ["a"]))
        var outcome = h.state.stage(split(2, game: ["b"]), animatesSinglePushes: false, hasPolicy: false, now: h.time)
        XCTAssertTrue(outcome.popsCollapsedSecondary && outcome.resumesCompactColumn)
        XCTAssertEqual(h.state.compactColumn, .primary, "the binding waits until UIKit has popped Game A")
        // UIKit pops Game A; the binding then asks SwiftUI for Game B.
        h.frame(h.faithfulSample(secondaryOnTop: false))
        outcome = h.state.resumeCompactColumn()
        XCTAssertEqual(outcome.install, InstallAnimation.none)
        XCTAssertEqual(h.state.compactColumn, .secondary)
        h.frames()
        XCTAssertEqual(h.readings.last?.revision, 2)
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["b"])
    }

    func testBackPlanClosingTheColumnSetsTheBinding() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game"]))
        h.frames()
        let pop = NavigationTransitionContext(operation: .pop, sourceEntryID: "game", destinationEntryID: "home")
        h.stage(split(2, transition: pop), hasPolicy: true)
        XCTAssertEqual(h.installs.last, .policy(pop), "closing a one-page column is a pop")
        XCTAssertEqual(h.state.compactColumn, .primary)
        XCTAssertEqual(h.nativePops, 0)
        h.frames()
        XCTAssertEqual(h.readings.last?.revision, 2)
    }

    func testBackDuringAnAnimatedSelectionTakesItBack() {
        var h = SplitHarness()
        h.stage(split(1))
        h.frames()
        h.stage(split(2, game: ["game"], transition: selection), hasPolicy: true)
        XCTAssertTrue(h.state.withholdsBack(.secondary("game-stack")))
        // The game is on screen but not settled yet.
        h.frame(h.faithfulSample(hittable: false))
        XCTAssertFalse(h.state.withholdsBack(.secondary("game-stack")), "Back is allowed once the game was seen")
        h.absorb(h.state.setCompactColumn(.primary))
        h.frames()
        XCTAssertEqual(h.readings.map(\.secondaryEntries), [[], ["game"], []])
        XCTAssertEqual(h.readings.last?.compactColumn, .primary)
        XCTAssertEqual(h.state.diagnostics.pushesTakenBack, 1)
    }

    func testCancelledBackDuringAnAnimatedSelectionRestoresIt() {
        var h = SplitHarness()
        h.stage(split(1))
        h.frames()
        h.stage(split(2, game: ["game"], transition: selection), hasPolicy: true)
        h.frame(h.faithfulSample(hittable: false))
        h.absorb(h.state.setCompactColumn(.primary))
        h.absorb(h.state.setCompactColumn(.secondary))
        h.frames()
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game"])
        XCTAssertEqual(h.state.diagnostics.pushesRestored, 1)
    }

    func testHiddenPrimaryColumnSettlesOnItsPath() {
        var h = SplitHarness()
        h.collapsed = false
        h.stage(split(1, home: ["home", "more"], game: ["game"]))
        for _ in 0..<3 { h.frame(h.faithfulSample(primaryInWindow: false)) }
        XCTAssertEqual(h.readings.last?.entries, ["home", "more"])
    }

    func testPrimaryPopBesideTheSecondaryColumnKeepsIt() {
        var h = SplitHarness()
        h.collapsed = false
        h.stage(split(1, home: ["home", "more"], game: ["game"]))
        h.frames()
        h.absorb(h.state.setPath([], for: .branch("home-stack")))
        h.frames()
        XCTAssertEqual(h.readings.last?.entries, ["home"])
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game"])
        XCTAssertEqual(h.readings.last?.compactColumn, .secondary)
    }

    func testFoldDuringApplyWaitsForTheNewLayout() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game"]))
        h.frame()
        h.frame()
        h.collapsed = false
        h.frame()
        h.collapsed = true
        h.frame()
        XCTAssertTrue(h.readings.isEmpty, "a layout seen on one frame is not read")
        h.frames()
        XCTAssertEqual(h.readings.count, 1)
    }

    func testSheetOverTheSplit() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game"], modal: ["edit"]))
        h.frames()
        XCTAssertEqual(h.readings.last?.modalEntries, ["edit"])
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game"])
        h.absorb(h.state.dismissModal())
        h.frames()
        XCTAssertNil(h.readings.last?.modalID)
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game"])
    }

    func testRandomSplitWritesNeverLeaveARevisionApplying() {
        var generator = SystemRandomNumberGenerator()
        for run in 0..<200 {
            var h = SplitHarness()
            h.collapsed = Bool.random(using: &generator)
            var revision: Int64 = 0
            for _ in 0..<12 {
                switch Int.random(in: 0..<7, using: &generator) {
                case 0:
                    revision += 1
                    let game: [String]? = Bool.random(using: &generator) ? nil : ["g\(revision)"] +
                        (Bool.random(using: &generator) ? ["p"] : [])
                    h.stage(split(revision, game: game))
                case 1: h.collapsed.toggle()
                case 2: h.absorb(h.state.setCompactColumn(Bool.random(using: &generator) ? .primary : .secondary))
                case 3:
                    if let key = h.secondary?.key { h.absorb(h.state.setPath([], for: key)) }
                case 4: h.frame(h.faithfulSample(secondaryOnTop: Bool.random(using: &generator)))
                default: h.frame()
                }
            }
            for _ in 0..<2 { h.frame() }
            h.time += 3
            for _ in 0..<6 { h.frame() }
            XCTAssertFalse(h.state.applying, "run \(run) stayed applying")
        }
    }
}

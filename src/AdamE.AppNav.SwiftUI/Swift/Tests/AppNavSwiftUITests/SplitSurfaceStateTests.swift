import XCTest
@testable import AppNavSwiftUI

private func entry(_ id: String) -> NavigationEntry {
    NavigationEntry(id: id, mount: Int64(abs(id.hashValue % 1000)) + 1)
}

/// Home in the primary column and, optionally, a game's stack in the secondary column.
private func split(_ revision: Int64, home: [String] = ["home"], game: [String]? = nil,
                   compact: NavigationSplitColumn? = nil, modal: [String]? = nil,
                   transition: NavigationTransitionContext? = nil, id: String = "games") -> NavigationProjection {
    let secondary = game.map { NavigationBranchProjection(id: "\($0[0])-stack", title: "", entries: $0.map(entry)) }
    return NavigationProjection(
        revision: revision, selectedBranch: "home-stack", isBranchHost: false,
        branches: [NavigationBranchProjection(id: "home-stack", title: "", entries: home.map(entry))],
        modal: modal.map { ModalProjection(id: "sheet", entries: $0.map(entry)) }, transition: transition,
        split: SplitProjection(id: id, secondary: secondary,
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
    func faithfulSample(secondaryOnTop: Bool? = nil, primaryInWindow: Bool = true, holdsSettlement: Bool = false) -> NativeSample {
        var tops: [StackSample] = []
        let home = primary
        func sample(_ key: StackKey, _ entries: [NavigationEntry]) -> StackSample {
            StackSample(key: key, top: entries[entries.count - 1], depth: entries.count,
                        identities: entries.map { Optional($0.id) })
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
                            holdsSettlement: holdsSettlement, split: SplitSample(isCollapsed: collapsed))
    }

    /// A collapsed frame in which UIKit still shows `game`'s root over Home, whatever the shell staged.
    func staleSample(_ game: String) -> NativeSample {
        let entries = primary + [entry(game)]
        let top = StackSample(key: .secondary("\(game)-stack"), top: entries[entries.count - 1], depth: entries.count,
                              identities: entries.map { Optional($0.id) })
        return NativeSample(time: time, markerKeys: [top.key], tops: [top],
                            mounted: [MountedPage(key: top.key, entry: top.top)], split: SplitSample(isCollapsed: true))
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

    mutating func framesFollowingCompactBinding(departing game: String) {
        for _ in 0..<3 {
            // The shell still renders the departing game, so UIKit can keep showing it when the binding asks for it.
            let showsDeparting = state.compactColumn == .secondary && state.departingSecondary != nil
            frame(showsDeparting ? staleSample(game) : faithfulSample())
        }
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

    func testRollbackRestoresPrimaryBeforeUIKitShowsTheSelection() {
        let rollback = NavigationTransitionContext(operation: .rollback, sourceEntryID: "", destinationEntryID: "",
                                                   sourceKey: "", sourceScreen: "", destinationScreen: "")
        for oldPrimaryFrames in [0, 1, 2] {
            var h = SplitHarness()
            h.stage(split(1))
            h.frames()
            h.stage(split(2, game: ["game"]))
            XCTAssertEqual(h.state.compactColumn, .secondary)
            h.frames(oldPrimaryFrames, secondaryOnTop: false)

            h.stage(split(3, transition: rollback))
            XCTAssertEqual(h.state.compactColumn, .primary)
            h.framesFollowingCompactBinding(departing: "game")
            XCTAssertEqual(h.readings.map(\.revision), [1, 3], "after \(oldPrimaryFrames) old primary frames")
            XCTAssertNil(h.state.departingSecondary)
        }
    }

    func testSelectionCanSupersedeAnUnconfirmedPrimaryWrite() {
        var h = SplitHarness()
        h.stage(split(1, game: ["a"]))
        h.frames()
        h.stage(split(2))
        XCTAssertEqual(h.state.compactColumn, .primary)

        h.stage(split(3, game: ["b"]))
        XCTAssertEqual(h.state.compactColumn, .secondary)
        XCTAssertNil(h.state.nativePop)
        h.frames()
        XCTAssertEqual(h.readings.last?.revision, 3)
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["b"])
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

    /// Leaves the shell stuck on SwiftUI's stray primary binding with `game`'s root on top of a collapsed window.
    private func strayCollapsedGame(_ h: inout SplitHarness, _ game: String) {
        h.stage(split(1, game: [game, "page"]))
        h.frames()
        h.absorb(h.state.setCompactColumn(.primary))
        h.absorb(h.state.setPath([], for: .secondary("\(game)-stack")))
        h.frames(secondaryOnTop: true)
        h.stage(split(1, game: [game]))
        XCTAssertEqual(h.state.compactColumn, .primary)
        XCTAssertEqual(h.state.collapsedTop, .secondary)
    }

    func testClosingTheColumnOverAStrayBindingPopsNativelyUntilUIKitShowsHome() {
        var h = SplitHarness()
        strayCollapsedGame(&h, "game")
        h.stage(split(2))
        XCTAssertEqual(h.nativePops, 0, "the pop waits for a frame")
        XCTAssertEqual(h.state.departingSecondary?.id, "game-stack", "the detail keeps the game while it pops")
        // A transition in flight is not a quiet frame.
        var busy = h.staleSample("game")
        busy.isTransitioning = true
        h.frame(busy)
        XCTAssertEqual(h.nativePops, 0)
        // UIKit ignores the first request; the shell asks again on the next quiet frame.
        h.frame(h.staleSample("game"))
        h.frame(h.staleSample("game"))
        XCTAssertEqual(h.nativePops, 2)
        XCTAssertTrue(h.state.applying)
        h.frames()
        XCTAssertNil(h.state.nativePop)
        XCTAssertEqual(h.readings.last?.revision, 2)
        XCTAssertEqual(h.readings.last?.secondaryEntries, [])
        XCTAssertNil(h.state.departingSecondary)
    }

    func testADifferentSplitDoesNotRenderTheOldSecondaryColumn() {
        for collapsed in [true, false] {
            var h = SplitHarness()
            h.collapsed = collapsed
            h.stage(split(1, game: ["game"]))
            h.frames()
            h.stage(split(2, id: "levels"))
            XCTAssertNil(h.state.departingSecondary, "a new split's detail column never shows the old split's page")
            h.frames()
            XCTAssertEqual(h.readings.last?.revision, 2)
            XCTAssertEqual(h.readings.last?.secondaryEntries, [])
        }
    }

    func testSwitchingGamesOverAStrayBindingPopsThenShowsTheNewGame() {
        var h = SplitHarness()
        strayCollapsedGame(&h, "a")
        h.stage(split(2, game: ["b"]))
        XCTAssertNotNil(h.state.nativePop)
        XCTAssertEqual(h.state.compactColumn, .primary, "the binding waits until UIKit has popped Game A")
        h.frame(h.staleSample("a"))
        XCTAssertEqual(h.nativePops, 1)
        XCTAssertEqual(h.state.compactColumn, .primary, "no secondary write while Game A is still on top")
        // UIKit shows Home; only then does the binding ask SwiftUI for Game B.
        h.frame(h.faithfulSample(secondaryOnTop: false))
        XCTAssertEqual(h.state.compactColumn, .secondary)
        XCTAssertEqual(h.installs.last, InstallAnimation.none)
        h.frames()
        XCTAssertEqual(h.readings.last?.revision, 2)
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["b"])
    }

    func testRollbackBeforeUIKitShowsTheColumnWrittenAfterANativePop() {
        var h = SplitHarness()
        strayCollapsedGame(&h, "a")
        h.stage(split(2, game: ["b"]))
        h.frame(h.staleSample("a"))
        h.frame(h.faithfulSample(secondaryOnTop: false))
        XCTAssertEqual(h.state.collapsedTop, .primary)
        XCTAssertEqual(h.state.compactColumn, .secondary)

        h.stage(split(3))
        XCTAssertEqual(h.state.compactColumn, .primary)
        h.framesFollowingCompactBinding(departing: "b")
        XCTAssertEqual(h.readings.last?.revision, 3)
    }

    func testUnfoldingBeforeTheNativePopWritesTheBindingInstead() {
        var h = SplitHarness()
        strayCollapsedGame(&h, "a")
        h.stage(split(2, game: ["b"]))
        h.collapsed = false
        h.frames()
        XCTAssertEqual(h.nativePops, 0)
        XCTAssertNil(h.state.nativePop)
        XCTAssertEqual(h.state.compactColumn, .secondary)
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["b"])
    }

    // Review finding: an unfold and a fold before the expanded repair left the stray binding, so the collapsed window
    // showed Home alone and the shell reported a Back that closed the game.
    func testQuickFoldBeforeTheRepairShowsTheGameAgain() {
        var h = SplitHarness()
        h.collapsed = false
        h.stage(split(1, game: ["game", "page"]))
        h.frames()
        h.absorb(h.state.setCompactColumn(.primary))
        h.absorb(h.state.setPath([], for: .secondary("game-stack")))
        // The device folds again while the pop still animates, so no quiet expanded frame repairs the binding.
        var moving = h.faithfulSample()
        moving.isTransitioning = true
        h.frame(moving)
        XCTAssertEqual(h.state.compactColumn, .primary)
        h.collapsed = true
        h.frame()
        let readings = h.readings.count
        h.frames(2)
        XCTAssertEqual(h.state.compactColumn, .secondary, "the column is shown again")
        XCTAssertEqual(h.state.diagnostics.compactColumnRepairs, 1)
        h.frames()
        XCTAssertEqual(h.readings.count, readings + 1, "only the pop to the game's root is reported")
        XCTAssertFalse(h.readings.contains { $0.secondaryEntries.isEmpty }, "no Back closed the game")
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game"])
        XCTAssertEqual(h.readings.last?.compactColumn, .secondary)
    }

    func testCollapsedTopFollowsHeldLayoutsOnly() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game"]))
        h.frames()
        XCTAssertEqual(h.state.collapsedTop, .secondary)
        h.collapsed = false
        h.frame()
        XCTAssertEqual(h.state.collapsedTop, .secondary, "one unfolded frame is not a layout yet")
        h.frame()
        XCTAssertNil(h.state.collapsedTop)
        h.collapsed = true
        h.frame(h.faithfulSample(secondaryOnTop: false))
        XCTAssertNil(h.state.collapsedTop, "one folded frame is not a layout yet")
        h.frame(h.faithfulSample(secondaryOnTop: true))
        XCTAssertEqual(h.state.collapsedTop, .secondary)
    }

    func testStageDuringUnfoldDoesNotReuseTheOldCollapsedTop() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game"], compact: .primary))
        h.frames()
        h.collapsed = false
        h.frame()
        XCTAssertEqual(h.state.collapsedTop, .primary, "the last held layout still showed Home")
        h.absorb(h.state.setCompactColumn(.secondary))

        h.stage(split(2))
        XCTAssertEqual(h.state.compactColumn, .primary)
        h.collapsed = true
        h.framesFollowingCompactBinding(departing: "game")
        XCTAssertEqual(h.readings.last?.revision, 2)
    }

    func testCancelledTakeBackKeepsTheBindingWhileTheGameIsOnTop() {
        var h = SplitHarness()
        h.stage(split(1))
        h.frames()
        h.stage(split(2, game: ["game"], transition: selection), hasPolicy: true)
        h.frame(h.faithfulSample(holdsSettlement: true))
        h.absorb(h.state.setCompactColumn(.primary))
        // The Back was cancelled and UIKit settles with the game still on top; SwiftUI keeps its primary binding.
        h.frames(4, secondaryOnTop: true)
        XCTAssertEqual(h.state.diagnostics.pushesRestored, 1)
        XCTAssertEqual(h.state.compactColumn, .primary, "writing the shown column back would push it twice")
        XCTAssertEqual(h.readings.last?.revision, 2)
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game"])
    }

    func testPrimaryWriteBesideTheGameIsNotATakeBack() {
        var h = SplitHarness()
        h.collapsed = false
        h.stage(split(1))
        h.frames()
        h.stage(split(2, game: ["game"], transition: selection), hasPolicy: true)
        h.frame(h.faithfulSample(holdsSettlement: true))
        h.absorb(h.state.setCompactColumn(.primary))
        h.frames()
        XCTAssertEqual(h.state.diagnostics.pushesTakenBack, 0)
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game"])
        XCTAssertEqual(h.state.compactColumn, .secondary, "the repair puts the staged column back")
    }

    // Review finding: a guarded secondary root kept the system Back on a collapsed window, where it sits over Home.
    func testSecondaryRootHasBackOnlyOnHeldCollapsedLayouts() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game"]))
        XCTAssertNil(h.state.heldCollapsed)
        h.frames()
        XCTAssertEqual(h.state.heldCollapsed, true)
        h.collapsed = false
        h.frame()
        XCTAssertEqual(h.state.heldCollapsed, true, "a passing layout keeps the guard")
        h.frame()
        XCTAssertEqual(h.state.heldCollapsed, false, "side by side the root has nothing behind it")
        h.collapsed = true
        h.frame()
        XCTAssertEqual(h.state.heldCollapsed, false)
        h.frame()
        XCTAssertEqual(h.state.heldCollapsed, true)
    }

    // Review finding: a Back that closed Game A left Game B, never shown, counted as seen on top.
    func testSeenOnTopBelongsToTheShownSecondaryColumn() {
        var h = SplitHarness()
        h.stage(split(1, game: ["a"]))
        h.frames()
        h.absorb(h.state.setCompactColumn(.primary))
        h.frames(secondaryOnTop: false)
        XCTAssertEqual(h.readings.last?.secondaryEntries, [], "the Back that closed Game A")
        h.stage(split(1))
        h.stage(split(2, game: ["b"]))
        // UIKit never shows Game B: the selection stalls past the quiet limit.
        for _ in 0..<3 { h.frame(h.faithfulSample(secondaryOnTop: false)) }
        h.time += 3
        for _ in 0..<6 { h.frame(h.faithfulSample(secondaryOnTop: false)) }
        XCTAssertFalse(h.state.applying)
        XCTAssertFalse(h.readings.contains { $0.revision == 2 }, "no phantom Back closed Game B")
    }

    func testNativePopWithoutAPrimaryPageReleasesAtTheStallLimit() {
        var h = SplitHarness()
        strayCollapsedGame(&h, "game")
        h.stage(split(2))
        // The adapter never finds Home's page, so UIKit keeps the game on top whatever the shell asks.
        for _ in 0..<3 { h.frame(h.staleSample("game")) }
        h.time += 3
        h.frame(h.staleSample("game"))
        XCTAssertFalse(h.state.applying, "the revision is released at the stall limit")
        XCTAssertNil(h.state.nativePop)
        XCTAssertNil(h.state.pendingCompactColumn)
        let pops = h.nativePops
        for _ in 0..<5 { h.frame(h.staleSample("game")) }
        XCTAssertEqual(h.nativePops, pops, "it stops asking once released")
        XCTAssertFalse(h.readings.contains { $0.revision == 2 })
    }

    // Review finding: the undo snapshot for a refused native write left out the compact column, so a binding SwiftUI
    // changed before the quiet frame stayed changed.
    func testRefusedNativeWriteRestoresTheCompactColumn() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game"], compact: .primary))
        h.frames()
        XCTAssertEqual(h.state.collapsedTop, .primary)
        // A write the shell refuses arms the undo; SwiftUI then moves the binding before the next quiet frame.
        h.absorb(h.state.setPath([entry("stray")], for: .branch("home-stack")))
        h.absorb(h.state.setCompactColumn(.secondary))
        h.frame(h.faithfulSample(secondaryOnTop: true))
        XCTAssertEqual(h.state.compactColumn, .primary)
        XCTAssertEqual(h.state.paths[.branch("home-stack")], [])
    }

    func testStrayLeftByARestoreTriggersTheNativePopOnTheNextStage() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game"], compact: .primary))
        h.frames()
        h.absorb(h.state.setPath([entry("stray")], for: .branch("home-stack")))
        h.absorb(h.state.setCompactColumn(.secondary))
        // UIKit shows the game on top; the restore puts the binding back on the primary column over it.
        h.frame(h.faithfulSample(secondaryOnTop: true))
        h.frame(h.faithfulSample(secondaryOnTop: true))
        XCTAssertEqual(h.state.compactColumn, .primary)
        XCTAssertEqual(h.state.collapsedTop, .secondary)
        h.stage(split(2))
        XCTAssertNotNil(h.state.nativePop, "the binding already names the primary column, so UIKit must pop the game")
        h.frame(h.staleSample("game"))
        XCTAssertEqual(h.nativePops, 1)
    }

    // Review finding: a window that went from a split to a plain stack and back reused the old split's held layout.
    func testSplitStackSplitStartsWithoutAHeldLayout() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game"]))
        h.frames()
        XCTAssertEqual(h.state.heldCollapsed, true)
        XCTAssertEqual(h.state.collapsedTop, .secondary)
        let stack = NavigationProjection(
            revision: 2, selectedBranch: "main", isBranchHost: false,
            branches: [NavigationBranchProjection(id: "main", title: "", entries: [entry("page")])], modal: nil)
        h.stage(stack)
        let top = StackSample(key: .branch("main"), top: entry("page"), depth: 1, identities: ["page"])
        for _ in 0..<3 { h.frame(NativeSample(time: h.time, markerKeys: [top.key], tops: [top])) }
        XCTAssertNil(h.state.heldCollapsed)
        XCTAssertNil(h.state.collapsedTop)
        XCTAssertNil(h.state.splitLayout)
        h.stage(split(3, game: ["game"]))
        XCTAssertNil(h.state.heldCollapsed, "no layout has held for the new split")
        h.frame()
        XCTAssertNil(h.state.heldCollapsed, "one frame is not a layout yet")
        h.frame()
        XCTAssertEqual(h.state.heldCollapsed, true)
    }

    // Review finding: one frame without the split controller dropped the held layout, so a stage wrote the column
    // already on top and SwiftUI pushed it again.
    func testFrameWithoutTheSplitKeepsTheHeldLayout() {
        var h = SplitHarness()
        strayCollapsedGame(&h, "game")
        var missing = h.faithfulSample(secondaryOnTop: true)
        missing.split = nil
        h.frame(missing)
        XCTAssertEqual(h.state.heldCollapsed, true)
        XCTAssertEqual(h.state.collapsedTop, .secondary)
        // A push in the game keeps the stray binding: writing the shown column would push the game's root again.
        h.stage(split(2, game: ["game", "next"]))
        XCTAssertEqual(h.state.compactColumn, .primary)
        XCTAssertNil(h.state.nativePop)
    }

    // Review finding: one frame without the split controller forgot that the game had shown on top, so a Back that
    // closed it right after was taken for a selection that never showed, and the column was pushed again.
    func testFrameWithoutTheSplitKeepsTheGameSeenOnTop() {
        var h = SplitHarness()
        h.stage(split(1, game: ["game"]))
        h.frames()
        var missing = h.faithfulSample(secondaryOnTop: true)
        missing.split = nil
        h.frame(missing)
        // A native Back closes the game; UIKit shows Home and SwiftUI writes the primary column.
        h.absorb(h.state.setCompactColumn(.primary))
        h.frames(secondaryOnTop: false)
        XCTAssertEqual(h.state.compactColumn, .primary, "the closed column is not shown again")
        XCTAssertEqual(h.state.diagnostics.compactColumnRepairs, 0)
        XCTAssertEqual(h.readings.last, NavigationReading(revision: 1, branch: "home-stack", entries: ["home"], modalID: nil,
                                                          modalEntries: [], secondaryEntries: [], compactColumn: .primary))
    }

    // Review finding: a different split kept the old split's stray binding and collapsed top, but the new split view
    // starts from its binding, so it showed Home instead of the staged game and the revision never settled.
    func testADifferentSplitTakesTheStagedCompactColumn() {
        var h = SplitHarness()
        strayCollapsedGame(&h, "game")
        h.stage(split(2, game: ["game"], id: "levels"))
        XCTAssertEqual(h.state.compactColumn, .secondary)
        XCTAssertNil(h.state.nativePop, "the old split's stack is gone, so there is nothing to pop")
        XCTAssertNil(h.state.heldCollapsed, "no layout has held for the new split")
        XCTAssertNil(h.state.collapsedTop)
        h.frames()
        XCTAssertEqual(h.readings.last?.revision, 2)
        XCTAssertEqual(h.readings.last?.secondaryEntries, ["game"])
        XCTAssertEqual(h.readings.last?.compactColumn, .secondary)
    }

    // A new split id kept the old view's zoom origin, so the new view decorated a root it mounted in place with a zoom,
    // whose dismissal kept the edge swipe from starting. The old split's departing game must not carry over either.
    func testNewSplitIdDropsOnlyCrossColumnZoomAndTheDepartingGame() {
        var h = SplitHarness()
        let key = StackKey.secondary("game-stack")
        let pagePush = NavigationTransitionContext(operation: .push, sourceEntryID: "game", destinationEntryID: "page",
                                                   sourceKey: "card")
        h.stage(split(1))
        h.frames()
        h.stage(split(2, game: ["game"], transition: selection), hasPolicy: true)
        h.frames()
        h.stage(split(3, game: ["game", "page"], transition: pagePush), hasPolicy: true)
        h.frames()
        XCTAssertEqual(h.state.matchedPushContext(for: entry("game"), key: key), selection)
        XCTAssertEqual(h.state.matchedPushContext(for: entry("page"), key: key), pagePush)

        h.stage(split(4, game: ["game", "page"], id: "games-2"))
        h.frames()
        XCTAssertNil(h.state.matchedPushContext(for: entry("game"), key: key))
        XCTAssertEqual(h.state.matchedPushContext(for: entry("page"), key: key), pagePush)
        h.stage(split(5, id: "games-3"))
        XCTAssertNil(h.state.departingSecondary, "the old split's game is not carried into the new view")
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
        h.frame(h.faithfulSample(holdsSettlement: true))
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
        h.frame(h.faithfulSample(holdsSettlement: true))
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

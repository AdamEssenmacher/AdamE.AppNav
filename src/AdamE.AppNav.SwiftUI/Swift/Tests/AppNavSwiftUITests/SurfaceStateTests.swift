import XCTest
@testable import AppNavSwiftUI

private final class Gesture {}

private func entry(_ id: String) -> NavigationEntry {
    NavigationEntry(id: id, mount: Int64(abs(id.hashValue % 1000)) + 1)
}

private func stack(_ revision: Int64, _ ids: [String], modal: [String]? = nil,
                   transition: NavigationTransitionContext? = nil) -> NavigationProjection {
    NavigationProjection(
        revision: revision, selectedBranch: "main", isBranchHost: false,
        branches: [NavigationBranchProjection(id: "main", title: "Main", entries: ids.map(entry))],
        modal: modal.map { ModalProjection(id: "sheet", entries: $0.map(entry)) }, transition: transition)
}

private func tabs(_ revision: Int64, selected: String, home: [String] = ["home"],
                  other: [String] = ["other"]) -> NavigationProjection {
    NavigationProjection(
        revision: revision, selectedBranch: selected, isBranchHost: true,
        branches: [NavigationBranchProjection(id: "home", title: "Home", entries: home.map(entry)),
                   NavigationBranchProjection(id: "other", title: "Other", entries: other.map(entry))],
        modal: nil)
}

/// Drives a SurfaceState with frames from a model of UIKit that shows exactly what the shell's bindings hold.
private struct Harness {
    var state = SurfaceState()
    var time = 0.0
    var readings: [NavigationReading] = []
    var traces: [String] = []
    var installs: [InstallAnimation] = []

    mutating func absorb(_ outcome: SurfaceOutcome) {
        readings += outcome.readings
        traces += outcome.traces
        if let install = outcome.install { installs.append(install) }
    }

    mutating func stage(_ projection: NavigationProjection, animatesSinglePushes: Bool = false, hasPolicy: Bool = false) {
        absorb(state.stage(projection, animatesSinglePushes: animatesSinglePushes, hasPolicy: hasPolicy, now: time))
    }

    /// A frame in which UIKit shows the shell's own paths.
    func faithfulSample() -> NativeSample {
        guard let target = state.target else { return NativeSample(time: time, markerKeys: [], tops: []) }
        var tops: [StackSample] = []
        var keys: [StackKey?] = []
        func add(_ key: StackKey, _ root: NavigationEntry?) {
            guard let root else { return }
            let entries = [root] + (state.paths[key] ?? [])
            keys.append(key)
            tops.append(StackSample(key: key, top: entries.last!, depth: entries.count,
                                    identities: entries.map { Optional($0.id) }))
        }
        if let branch = target.branches.first(where: { $0.id == state.selectedBranch }) {
            add(.branch(branch.id), branch.entries.first)
        }
        if let modal = target.modal, state.selectedModal?.id == modal.id {
            add(.modal(modal.id), modal.entries.first)
        }
        return NativeSample(time: time, markerKeys: keys, tops: tops)
    }

    mutating func frame(_ sample: NativeSample? = nil, advance: Double = 1.0 / 60) {
        time += advance
        var next = sample ?? faithfulSample()
        next.time = time
        absorb(state.sample(next))
    }

    mutating func settle() {
        frame()
        frame()
    }
}

final class SurfaceStateTests: XCTestCase {
    func testStagedRevisionSettlesAfterTwoMatchingFrames() {
        var h = Harness()
        h.stage(stack(1, ["home", "game"]))
        XCTAssertTrue(h.state.applying)
        h.frame()
        XCTAssertTrue(h.readings.isEmpty, "one frame is not enough to settle")
        h.frame()
        XCTAssertEqual(h.readings.map(\.entries), [["home", "game"]])
        XCTAssertEqual(h.state.phase, .idle)
    }

    func testUnknownSelectedBranchFallsBackToTheFirst() {
        var h = Harness()
        h.stage(tabs(1, selected: "missing"))
        h.settle()
        XCTAssertEqual(h.readings.map(\.branch), ["home"])
    }

    // Review finding: a revision staged during a native Back swipe left the surface applying forever.
    func testRevisionStagedDuringGestureIsHeldUntilTheGestureEnds() {
        var h = Harness()
        h.stage(stack(1, ["home", "game"]))
        h.settle()
        let gesture = ObjectIdentifier(Gesture())
        h.state.interactionBegan(gesture)

        h.stage(stack(2, ["home", "game", "review"]))
        XCTAssertEqual(h.state.target?.revision, 1, "the paths under a live gesture stay put")
        XCTAssertFalse(h.state.applying)

        // The swipe completes and SwiftUI writes the pop back.
        h.absorb(h.state.setPath([], for: .branch("main")))
        XCTAssertTrue(h.state.interactionEnded(gesture, cancelled: false))
        XCTAssertEqual(h.state.paths[.branch("main")], [], "the pop the gesture completed is accepted")

        let held = h.state.takeHeld()
        XCTAssertEqual(held?.projection.revision, 2)
        h.stage(held!.projection)
        h.settle()
        XCTAssertEqual(h.readings.last?.revision, 2)
        XCTAssertEqual(h.readings.last?.entries, ["home", "game", "review"])
        XCTAssertEqual(h.state.phase, .idle)
    }

    func testOnlyTheLatestRevisionIsHeld() {
        var h = Harness()
        h.stage(stack(1, ["home"]))
        h.settle()
        let gesture = ObjectIdentifier(Gesture())
        h.state.interactionBegan(gesture)
        h.stage(stack(2, ["home", "a"]))
        h.stage(stack(3, ["home", "b"]))
        _ = h.state.interactionEnded(gesture, cancelled: true)
        XCTAssertEqual(h.state.takeHeld()?.projection.revision, 3)
        XCTAssertNil(h.state.takeHeld())
    }

    // Review finding: the same revision with different contents replaced the target but not the stacks.
    func testSameRevisionWithDifferentContentsInstalls() {
        var h = Harness()
        h.stage(stack(1, ["home", "game"]))
        h.settle()
        h.stage(stack(1, ["home", "detail"]))
        XCTAssertEqual(h.state.paths[.branch("main")], [entry("detail")])
        XCTAssertTrue(h.state.applying)
        XCTAssertEqual(h.installs.last, InstallAnimation.none)
        h.settle()
        XCTAssertEqual(h.readings.last?.entries, ["home", "detail"])
    }

    // The presenter republishes a revision when it accepts a native pop. The shell already shows it.
    func testSameRevisionMatchingAnAcceptedNativePopDoesNotReinstall() {
        var h = Harness()
        h.stage(stack(1, ["home", "game"]))
        h.settle()
        h.absorb(h.state.setPath([], for: .branch("main")))
        h.settle()
        let installs = h.installs.count
        h.stage(stack(1, ["home"]))
        XCTAssertEqual(h.installs.count, installs)
        XCTAssertEqual(h.state.phase, .idle)
        XCTAssertEqual(h.state.target?.branches.first?.entries, [entry("home")])
    }

    // A second native pop can land before the presenter republishes its acceptance of the first.
    func testSameRevisionBehindTheUsersPopsDoesNotPushThemBack() {
        var h = Harness()
        h.stage(stack(1, ["home", "a", "b"]))
        h.settle()
        h.absorb(h.state.setPath([], for: .branch("main")))
        let installs = h.installs.count
        h.stage(stack(1, ["home", "a"]))
        XCTAssertEqual(h.installs.count, installs)
        XCTAssertEqual(h.state.paths[.branch("main")], [])
        h.settle()
        XCTAssertEqual(h.readings.last?.entries, ["home"])
    }

    func testSameRevisionWithOnlyNewMetadataDoesNotReinstall() {
        var h = Harness()
        h.stage(stack(1, ["home", "game"]))
        h.settle()
        let installs = h.installs.count
        h.stage(stack(1, ["home", "game"], transition: NavigationTransitionContext(operation: .refresh)))
        XCTAssertEqual(h.installs.count, installs)
        XCTAssertFalse(h.state.applying)
        XCTAssertEqual(h.state.target?.transition?.operation, .refresh)
    }

    // Review finding: a sheet root never received a matched (zoom) style.
    func testRootOnlySheetRecordsItsSourceForAMatchedStyle() {
        var h = Harness()
        h.stage(stack(1, ["home"]))
        h.settle()
        let present = NavigationTransitionContext(operation: .modalPresent, sourceEntryID: "home",
                                                  destinationEntryID: "edit", sourceKey: "cover")
        h.stage(stack(2, ["home"], modal: ["edit"], transition: present), hasPolicy: true)
        XCTAssertEqual(h.state.matchedPushContext(for: entry("edit"), key: .modal("sheet"))?.sourceKey, "cover")
        XCTAssertEqual(h.installs.last, .policy(present))
        h.settle()
        XCTAssertNotNil(h.state.matchedPushContext(for: entry("edit"), key: .modal("sheet")),
                        "the match survives settlement so a later dismissal can use it")

        h.stage(stack(3, ["home"], transition: NavigationTransitionContext(operation: .modalDismiss)))
        h.settle()
        XCTAssertNil(h.state.matchedPushContext(for: entry("edit"), key: .modal("sheet")))
    }

    func testSheetWithPrebuiltHistoryRecordsNoRootMatch() {
        var h = Harness()
        h.stage(stack(1, ["home"]))
        h.settle()
        let present = NavigationTransitionContext(operation: .modalPresent, sourceEntryID: "home",
                                                  destinationEntryID: "edit", sourceKey: "cover")
        h.stage(stack(2, ["home"], modal: ["edit", "review"], transition: present), hasPolicy: true)
        XCTAssertNil(h.state.matchedPushContext(for: entry("edit"), key: .modal("sheet")))
        XCTAssertEqual(h.installs.last, InstallAnimation.none)
    }

    // The class behind several review rounds: a native change the gates missed while a revision applies.
    func testNativePopWhileApplyingIsTakenThenUndone() {
        var h = Harness()
        h.stage(stack(1, ["home", "game"]))
        h.frame()
        h.absorb(h.state.setPath([], for: .branch("main")))
        XCTAssertEqual(h.state.paths[.branch("main")], [], "the shell follows UIKit so SwiftUI has a change to make")
        h.frame()
        XCTAssertEqual(h.state.paths[.branch("main")], [entry("game")], "the next quiet frame puts the target back")
        XCTAssertEqual(h.installs.last, InstallAnimation.none)
        h.settle()
        XCTAssertEqual(h.readings.map(\.entries), [["home", "game"]])
    }

    func testNativeSheetDismissalWhileApplyingIsUndone() {
        var h = Harness()
        h.stage(stack(1, ["home"], modal: ["edit"]))
        h.absorb(h.state.dismissModal())
        XCTAssertNil(h.state.selectedModal)
        h.frame()
        XCTAssertEqual(h.state.selectedModal, ModalSelection(id: "sheet"))
        h.settle()
        XCTAssertEqual(h.readings.last?.modalEntries, ["edit"])
    }

    func testUndoWaitsForTransitionsToFinish() {
        var h = Harness()
        h.stage(stack(1, ["home", "game"]))
        h.absorb(h.state.setPath([], for: .branch("main")))
        var busy = h.faithfulSample()
        busy.isTransitioning = true
        h.frame(busy)
        XCTAssertEqual(h.state.paths[.branch("main")], [])
        h.frame()
        XCTAssertEqual(h.state.paths[.branch("main")], [entry("game")])
    }

    func testNativeWritesThatKeepOverridingARevisionEndIt() {
        var h = Harness()
        h.stage(stack(1, ["home", "game"]))
        for _ in 0..<h.state.undoLimit {
            h.absorb(h.state.setPath([], for: .branch("main")))
            h.frame()
        }
        XCTAssertTrue(h.state.applying)
        h.absorb(h.state.setPath([], for: .branch("main")))
        XCTAssertEqual(h.state.phase, .abandoned)
        XCTAssertEqual(h.state.paths[.branch("main")], [], "the native state stands")
    }

    func testPushTheShellDidNotMakeIsUndoneWhenIdle() {
        var h = Harness()
        h.stage(stack(1, ["home"]))
        h.settle()
        h.absorb(h.state.setPath([entry("stray")], for: .branch("main")))
        h.frame()
        XCTAssertEqual(h.state.paths[.branch("main")], [])
        XCTAssertEqual(h.state.phase, .idle)
    }

    func testUserPopWhenIdleIsAcceptedAndReported() {
        var h = Harness()
        h.stage(stack(1, ["home", "game"]))
        h.settle()
        h.absorb(h.state.setPath([], for: .branch("main")))
        h.settle()
        XCTAssertEqual(h.readings.map(\.entries), [["home", "game"], ["home"]])
        XCTAssertTrue(h.installs.dropFirst().isEmpty, "an accepted pop needs no reinstall")
    }

    func testWritesFromAStackTheTargetDroppedAreIgnored() {
        var h = Harness()
        h.stage(stack(1, ["home"], modal: ["edit", "review"]))
        h.settle()
        h.stage(stack(2, ["home"]))
        h.absorb(h.state.setPath([entry("zombie")], for: .modal("sheet")))
        XCTAssertNil(h.state.paths[.modal("sheet")])
        h.settle()
        XCTAssertEqual(h.readings.last?.revision, 2)
    }

    // An overlay, a dropped install, or any state the witness can't read used to withhold Back forever.
    func testRevisionTheNativeSideNeverShowsIsAbandoned() {
        var h = Harness()
        h.stage(tabs(1, selected: "home", home: ["home", "game"]))
        var covered = h.faithfulSample()
        covered.tops[0].isHittable = false
        while h.time < h.state.stallLimit + 0.5 { h.frame(covered) }
        XCTAssertEqual(h.state.phase, .abandoned)
        XCTAssertFalse(h.state.applying)
        XCTAssertFalse(h.state.isWaitingForTab(.branch("home")), "Back is released on the selected tab")
        XCTAssertTrue(h.readings.isEmpty)

        // Once the stack can be read again it still settles.
        h.settle()
        XCTAssertEqual(h.readings.map(\.entries), [["home", "game"]])
        XCTAssertEqual(h.state.phase, .idle)
    }

    func testTransitionsDoNotCountTowardTheStallLimit() {
        var h = Harness()
        h.stage(stack(1, ["home", "game"]))
        var moving = h.faithfulSample()
        moving.tops[0].isTransitioning = true
        moving.isTransitioning = true
        while h.time < h.state.stallLimit * 2 { h.frame(moving) }
        XCTAssertTrue(h.state.applying)
    }

    func testSelectingATabThenPoppingEarlyRestoresItsStack() {
        var h = Harness()
        h.stage(tabs(1, selected: "home", other: ["other", "deep"]))
        h.settle()
        h.absorb(h.state.selectBranch("other"))
        XCTAssertTrue(h.state.isWaitingForTab(.branch("other")))
        h.absorb(h.state.setPath([], for: .branch("other")))
        h.frame()
        XCTAssertEqual(h.state.paths[.branch("other")], [entry("deep")])
    }

    // Whatever arrives, in whatever order, a quiet native side ends every revision: the shell either
    // settles or stops waiting. It never withholds Back indefinitely.
    func testApplyingAlwaysEndsOnceTheNativeSideIsQuiet() {
        var generator = SystemRandomNumberGenerator()
        for _ in 0..<300 {
            var seed = SplitMix(state: generator.next())
            var h = Harness()
            var revision: Int64 = 0
            var gesture: ObjectIdentifier?
            let ids = ["a", "b", "c"]
            for _ in 0..<40 {
                switch Int.random(in: 0..<9, using: &seed) {
                case 0, 1:
                    revision += Int64.random(in: 0...1, using: &seed)
                    let depth = Int.random(in: 0...3, using: &seed)
                    let modal = Bool.random(using: &seed) ? Array(["m"] + ids.prefix(Int.random(in: 0...2, using: &seed))) : nil
                    h.stage(stack(max(revision, 1), ["home"] + ids.prefix(depth), modal: modal),
                            animatesSinglePushes: Bool.random(using: &seed), hasPolicy: Bool.random(using: &seed))
                case 2:
                    let key: StackKey = Bool.random(using: &seed) ? .branch("main") : .modal("sheet")
                    h.absorb(h.state.setPath(ids.shuffled(using: &seed).prefix(Int.random(in: 0...3, using: &seed)).map(entry),
                                             for: key))
                case 3: h.absorb(h.state.dismissModal())
                case 4: h.absorb(h.state.selectBranch("main"))
                case 5:
                    if gesture == nil {
                        gesture = ObjectIdentifier(Gesture())
                        h.state.interactionBegan(gesture!)
                    }
                case 6:
                    if let token = gesture {
                        _ = h.state.interactionEnded(token, cancelled: Bool.random(using: &seed))
                        gesture = nil
                        if let held = h.state.takeHeld() { h.stage(held.projection) }
                    }
                case 7:
                    var odd = h.faithfulSample()
                    if !odd.tops.isEmpty {
                        odd.tops[0].depth += Int.random(in: -1...1, using: &seed)
                        odd.tops[0].isHittable = Bool.random(using: &seed)
                    }
                    odd.isTransitioning = Bool.random(using: &seed)
                    h.frame(odd)
                default: h.frame()
                }
            }
            if let token = gesture {
                _ = h.state.interactionEnded(token, cancelled: false)
                if let held = h.state.takeHeld() { h.stage(held.projection) }
            }
            // A native side that shows something the shell can never read is the worst case.
            let deadline = h.time + h.state.stallLimit + 1
            while h.time < deadline {
                var unreadable = h.faithfulSample()
                for index in unreadable.tops.indices { unreadable.tops[index].isHittable = false }
                h.frame(unreadable)
            }
            XCTAssertFalse(h.state.applying, "seed \(seed.start) left the shell applying")
            XCTAssertNil(h.state.held, "seed \(seed.start) left a revision held")
        }
    }

    // With a native side that follows the shell, every staged revision is eventually reported.
    func testFaithfulNativeSideAlwaysSettlesTheLatestRevision() {
        var generator = SystemRandomNumberGenerator()
        for _ in 0..<300 {
            var seed = SplitMix(state: generator.next())
            var h = Harness()
            var revision: Int64 = 0
            for _ in 0..<25 {
                switch Int.random(in: 0..<5, using: &seed) {
                case 0:
                    revision += 1
                    let modal = Bool.random(using: &seed) ? ["m"] : nil
                    h.stage(stack(revision, ["home"] + ["a", "b", "c"].prefix(Int.random(in: 0...3, using: &seed)), modal: modal))
                case 1:
                    let current = h.state.paths[.branch("main")] ?? []
                    h.absorb(h.state.setPath(Array(current.dropLast()), for: .branch("main")))
                case 2: h.absorb(h.state.dismissModal())
                default: h.frame()
                }
            }
            guard revision > 0 else { continue }
            for _ in 0..<20 { h.frame() }
            XCTAssertEqual(h.state.phase, .idle, "seed \(seed.start)")
            XCTAssertEqual(h.readings.last?.revision, revision, "seed \(seed.start)")
        }
    }
}

private struct SplitMix: RandomNumberGenerator {
    let start: UInt64
    var state: UInt64

    init(state: UInt64) {
        start = state
        self.state = state
    }

    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }
}

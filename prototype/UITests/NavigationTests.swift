import XCTest

final class NavigationTests: XCTestCase {
    let app = XCUIApplication(bundleIdentifier: "com.adamessenmacher.appnav.swiftuiprobe")

    override func setUpWithError() throws {
        continueAfterFailure = false
        if name.contains("testFullScreenModal") {
            app.launchArguments.append("--probe-full-screen")
        }
        if name.contains("testReplaceHostDuringGamePush") {
            app.launchArguments.append("--probe-replace-host")
        }
        if name.contains("testHomeGameZoomTakenBackDuringPush") || name.contains("testHomeGameCancelledBackDuringPush") {
            app.launchArguments.append("--probe-hold-game-settlement")
        }
        if name.contains("testHomeGameCrossFadeCancelAndReturn") || name.contains("testNativeFadeSheetPreservesHistory") {
            app.launchArguments.append("--probe-animation-style")
        }
        if name.contains("testNonanimatedGamePushRetainsMatchedSourceForNativeBack") {
            app.launchArguments.append("--probe-disable-game-push-animation")
        }
        if name.contains("testCallbackReplacementWithUnchangedProjection") {
            app.launchArguments.append("--probe-replace-callbacks")
        }
        if name.contains("testSplit") {
            app.launchArguments.append("--probe-split")
        }
        if name.contains("testSplitGuardedGame") || name.contains("testGuardedZoomedGame") {
            app.launchArguments.append("--probe-guard-game")
        }
        if name.contains("testSplitSelectionTakenBack") {
            app.launchArguments.append("--probe-hold-game-settlement")
        }
        app.launch()
    }

    private let home = "home[home,home-detail] history=1 [InAppCommand@home[home,home-detail]]"
    private let game = "home[home,home-detail,game] history=2 [InAppCommand@home[home,home-detail];InAppCommand@home[home,home-detail,game]]"
    private let returned = "home[home,home-detail] history=3 [InAppCommand@home[home,home-detail];InAppCommand@home[home,home-detail,game];HostReconciliation@home[home,home-detail]]"
    private let deep = "catalog[catalog,group,item,detail] history=2 [InAppCommand@home[home,home-detail];AppLink@catalog[catalog,group,item,detail]]"
    private let popped = "catalog[catalog,group] history=3 [InAppCommand@home[home,home-detail];AppLink@catalog[catalog,group,item,detail];HostReconciliation@catalog[catalog,group]]"

    func testHomeGameZoomCancelAndReturn() {
        verifyGameCancelAndReturn(openButton: "probe-open-game")
    }

    // A slow Back swipe can catch the zoom before the shell settles the push. UIKit then gives the push back and
    // SwiftUI writes the shorter path while the push is still applying. AppNav records the push and a native Back, and
    // SwiftUI must not push Game again. The probe holds the push applying until a Back gesture begins.
    func testHomeGameZoomTakenBackDuringPush() {
        awaitLabel("probe-status") { $0 == self.home }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        app.buttons["probe-open-game"].press(forDuration: 0.22)
        XCTAssertTrue(app.staticTexts["page-game"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.staticTexts["probe-native"].label.hasSuffix("home [home,home-detail]"),
                      "The shell settled the push before Back")
        XCTAssertEqual(app.staticTexts["probe-status"].label, home)

        edgeSwipe(to: 0.9)
        awaitLabel("probe-taken-back") { $0 == "taken back=1 restored=0" }
        awaitLabel("probe-status") { $0 == self.returned }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        XCTAssertTrue(app.staticTexts["page-game"].waitForNonExistence(timeout: 10))
        // Game stays gone once the native Back settles.
        let reappeared = expectation(for: NSPredicate(format: "exists == true"), evaluatedWith: app.staticTexts["page-game"])
        reappeared.isInverted = true
        wait(for: [reappeared], timeout: 3)
        XCTAssertEqual(app.staticTexts["probe-status"].label, returned)
    }

    // A Back swipe released short while the push is still applying leaves Game, and the push settles as usual.
    func testHomeGameCancelledBackDuringPush() {
        awaitLabel("probe-status") { $0 == self.home }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        app.buttons["probe-open-game"].press(forDuration: 0.22)
        XCTAssertTrue(app.staticTexts["page-game"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.staticTexts["probe-native"].label.hasSuffix("home [home,home-detail]"),
                      "The shell settled the push before Back")

        edgeSwipe(to: 0.18)
        awaitLabel("probe-status") { $0 == self.game }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail,game]") }
        awaitLabel("probe-gesture") { $0.hasPrefix("began=1 cancelled=1 completed=0") }
        // SwiftUI writes the shorter path only once a Back gesture completes, so a short release never takes the push
        // back and there is nothing to restore.
        XCTAssertEqual(app.staticTexts["probe-taken-back"].label, "taken back=0 restored=0")
        XCTAssertTrue(app.staticTexts["page-game"].exists)
    }

    func testHomeGameCrossFadeCancelAndReturn() {
        verifyGameCancelAndReturn(openButton: "probe-open-game-crossfade") {
            self.awaitLabel("probe-animation-style") { $0 == "Animated native Game push" }
        }
    }

    func testReplaceHostDuringGamePush() {
        awaitLabel("probe-status") { $0 == self.home }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        let replacement = app.buttons["probe-replace-host"]
        replacement.tap()
        awaitLabel("probe-status") { $0 == self.home + " replacement=armed" }

        app.buttons["probe-open-game-crossfade"].tap()
        let held = awaitLabel("probe-status") { $0.hasPrefix(self.home + " replacement=held epoch=") }
        let epochs = held.components(separatedBy: "epoch=").last?.components(separatedBy: "->").compactMap(Int.init)
        XCTAssertEqual(epochs?.count, 2)
        if let epochs, epochs.count == 2 { XCTAssertGreaterThan(epochs[1], epochs[0]) }
        XCTAssertTrue(app.staticTexts["page-game"].exists)
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail,game]") }

        // The injected result from the old epoch matches the replay revision and entries.
        // The router commits only after the matching reading from the new host is released.
        XCTAssertEqual(app.staticTexts["probe-status"].label, held)
        replacement.tap()
        awaitLabel("probe-status") { $0 == self.game }
        app.navigationBars.firstMatch.buttons.firstMatch.tap()
        awaitLabel("probe-status") { $0 == self.returned }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
    }

    private func verifyGameCancelAndReturn(openButton: String, verifyAnimation: () -> Void = {}) {
        awaitLabel("probe-status") { $0 == self.home }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        capture("AppNav Home")
        app.buttons[openButton].press(forDuration: 0.22)
        awaitLabel("probe-status") { $0 == self.game }
        let presented = awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail,game]") }
        XCTAssertTrue(app.staticTexts["page-game"].exists)
        XCTAssertTrue(app.descendants(matching: .any)["probe-game-toolbar"].firstMatch.exists)
        capture("AppNav Game")

        edgeSwipe(to: 0.18)
        awaitLabel("probe-gesture") { $0 == "began=1 cancelled=1 completed=0 settled=1 [home,home-detail,game]" }
        XCTAssertEqual(app.staticTexts["probe-status"].label, game)
        XCTAssertEqual(app.staticTexts["probe-native"].label, presented)
        XCTAssertTrue(app.staticTexts["page-game"].exists)
        capture("AppNav Game after canceled return")

        app.navigationBars.firstMatch.buttons.firstMatch.tap()
        awaitLabel("probe-status") { $0 == self.returned }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        XCTAssertTrue(app.buttons["probe-open-game"].exists)
        capture("AppNav Home after return")
        verifyAnimation()
    }

    func testNonanimatedGamePushRetainsMatchedSourceForNativeBack() {
        awaitLabel("probe-status") { $0 == self.home }
        app.buttons["probe-open-game"].tap()
        awaitLabel("probe-status") { $0 == self.game }
        XCTAssertTrue(app.staticTexts["page-game"].exists)

        let installed = awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail,game]") }
        app.buttons["probe-clear-push-metadata"].tap()
        awaitLabel("probe-retained-push-source") { $0 == "game-cover" }
        XCTAssertEqual(app.staticTexts["probe-native"].label, installed)

        app.navigationBars["Game"].buttons.firstMatch.tap()
        awaitLabel("probe-status") { $0 == self.returned }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
    }

    func testCallbackReplacementWithUnchangedProjection() {
        awaitLabel("probe-status") { $0 == self.home }
        awaitLabel("probe-callback-generations") { $0 == "callbacks=0 report=0 trace=0" }
        let initial = awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }

        app.buttons["probe-replace-callbacks"].tap()
        awaitLabel("probe-callback-generations") { $0 == "callbacks=1 report=0 trace=0" }
        XCTAssertEqual(app.staticTexts["probe-native"].label, initial)
        XCTAssertEqual(app.staticTexts["probe-status"].label, home)

        app.tabBars.buttons["catalog"].tap()
        awaitLabel("probe-status") { $0.hasPrefix("catalog[catalog] history=2 ") }
        awaitLabel("probe-callback-generations") { $0 == "callbacks=1 report=1 trace=1" }
    }

    func testNativeHomeGameZoomReference() {
        awaitLabel("probe-status") { $0 == self.home }
        capture("Native Home")
        app.buttons["probe-native-game"].press(forDuration: 0.22)
        let nativeGame = app.staticTexts["page-native-game"]
        XCTAssertTrue(nativeGame.waitForExistence(timeout: 10))
        XCTAssertEqual(app.staticTexts["probe-status"].label, home)
        capture("Native Game")

        edgeSwipe(to: 0.18)
        XCTAssertTrue(nativeGame.exists)
        XCTAssertEqual(app.staticTexts["probe-status"].label, home)
        capture("Native Game after interrupted return")

        app.navigationBars.firstMatch.buttons.firstMatch.tap()
        XCTAssertTrue(app.buttons["probe-native-game"].waitForExistence(timeout: 10))
        XCTAssertEqual(app.staticTexts["probe-status"].label, home)
        capture("Native Home after return")
    }

    func testBranchSwitchInterruptedSwipeAndBackMenuMultiPop() {
        awaitLabel("probe-status") { $0 == self.home }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        XCTAssertTrue(app.navigationBars["home-detail"].exists)
        app.buttons["probe-link"].tap()
        awaitLabel("probe-status") { $0 == self.deep }
        XCTAssertEqual(app.staticTexts["probe-presented"].label, "detail")
        XCTAssertTrue(app.navigationBars["detail"].exists)
        XCTAssertTrue(app.staticTexts["page-detail"].exists)

        let nativeBeforeSwipe = awaitLabel("probe-native") { $0.hasSuffix("catalog [catalog,group,item,detail]") }
        edgeSwipe(to: 0.18)
        awaitLabel("probe-gesture") { $0 == "began=1 cancelled=1 completed=0 settled=1 [catalog,group,item,detail]" }
        XCTAssertEqual(app.staticTexts["probe-status"].label, deep)
        XCTAssertEqual(app.staticTexts["probe-native"].label, nativeBeforeSwipe)
        XCTAssertTrue(app.navigationBars["detail"].exists)
        XCTAssertTrue(app.staticTexts["page-detail"].exists)

        let back = app.navigationBars["detail"].buttons.firstMatch
        // The iPhone history menu opens with its first row underneath the finger.
        // Releasing there selects the immediate parent. Keep touching and slide one
        // menu row down before release to select Group in a single native gesture.
        let start = back.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
        let groupRow = start.withOffset(CGVector(dx: 55, dy: 48))
        start.press(forDuration: 1, thenDragTo: groupRow, withVelocity: .slow, thenHoldForDuration: 0.2)
        awaitLabel("probe-status") { $0 == self.popped }
        awaitLabel("probe-native") { $0.hasSuffix("catalog [catalog,group]") }
        XCTAssertTrue(app.navigationBars["group"].exists)
        XCTAssertTrue(app.staticTexts["page-group"].exists)
    }

    func testOrdinaryBackRevealsEachLazyDestination() {
        awaitLabel("probe-status") { $0 == self.home }
        app.buttons["probe-link"].tap()
        awaitLabel("probe-status") { $0 == self.deep }
        XCTAssertEqual(app.staticTexts["probe-presented"].label, "detail")
        for (from, to, prefix, count) in [("detail", "item", "catalog,group,item", 3),
                                          ("item", "group", "catalog,group", 4),
                                          ("group", "catalog", "catalog", 5)] {
            app.navigationBars[from].buttons.firstMatch.tap()
            awaitLabel("probe-status") { $0.hasPrefix("catalog[\(prefix)] history=\(count) ") }
            XCTAssertTrue(app.staticTexts["page-" + to].exists)
        }
    }

    func testNativeTabSelectionRetainsBothStacks() {
        awaitLabel("probe-status") { $0 == self.home }
        app.buttons["probe-link"].tap()
        awaitLabel("probe-status") { $0 == self.deep }

        app.tabBars.buttons["home"].tap()
        awaitLabel("probe-status") { $0.hasPrefix("home[home,home-detail] history=3 ") }
        XCTAssertTrue(app.staticTexts["page-home-detail"].exists)

        app.tabBars.buttons["catalog"].tap()
        awaitLabel("probe-status") { $0.hasPrefix("catalog[catalog,group,item,detail] history=4 ") }
        XCTAssertTrue(app.staticTexts["page-detail"].exists)
    }

    func testNativeModalStackBackAndDismiss() {
        awaitLabel("probe-status") { $0 == self.home }
        openModal()
        XCTAssertTrue(app.staticTexts["page-review"].waitForExistence(timeout: 10))

        app.navigationBars["review"].buttons.firstMatch.tap()
        XCTAssertTrue(app.staticTexts["page-edit"].waitForExistence(timeout: 10))
        app.navigationBars["edit"].buttons["Done"].tap()

        awaitLabel("probe-status") { $0.hasPrefix("home[home,home-detail] history=4 ") }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        XCTAssertTrue(app.staticTexts["page-home-detail"].exists)
    }

    func testFullScreenModalPreservesHistoryAndDismisses() {
        awaitLabel("probe-status") { $0 == self.home }
        let window = app.windows.firstMatch.frame
        app.buttons["probe-open-fade-sheet"].tap()
        let dismiss = app.buttons["probe-dismiss-fade-sheet"]
        XCTAssertTrue(dismiss.waitForExistence(timeout: 10))
        awaitModalCommit(2)
        let bounds = app.descendants(matching: .any)["probe-modal-bounds"].frame
        XCTAssertEqual(bounds.minX, window.minX, accuracy: 1)
        XCTAssertEqual(bounds.minY, window.minY, accuracy: 1)
        XCTAssertEqual(bounds.width, window.width, accuracy: 1)
        XCTAssertEqual(bounds.height, window.height, accuracy: 1)
        dismiss.tap()
        awaitLabel("probe-status") { $0.hasPrefix("home[home,home-detail] history=3 ") }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        XCTAssertTrue(app.buttons["probe-open-fade-sheet"].isHittable)
    }

    func testFullScreenModalReplacesSheetAndReturnsToSheet() {
        awaitLabel("probe-status") { $0 == self.home }
        openModal()
        XCTAssertTrue(app.staticTexts["page-review"].waitForExistence(timeout: 10))
        awaitModalCommit(2)

        app.buttons["probe-replace-with-cover"].tap()
        XCTAssertTrue(app.staticTexts["page-fade-sheet"].waitForExistence(timeout: 10))
        awaitModalCommit(3)

        app.buttons["probe-replace-with-sheet"].tap()
        XCTAssertTrue(app.staticTexts["page-review"].waitForExistence(timeout: 10))
        awaitModalCommit(4)

        app.navigationBars["review"].buttons.firstMatch.tap()
        XCTAssertTrue(app.staticTexts["page-edit"].waitForExistence(timeout: 10))
        app.navigationBars["edit"].buttons["Done"].tap()
        awaitLabel("probe-status") { $0.hasPrefix("home[home,home-detail] history=6 ") }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
    }

    private func awaitModalCommit(_ historyCount: Int) {
        awaitLabel("probe-modal-status") { $0.hasPrefix("home[home,home-detail] history=\(historyCount) ") }
    }

    func testNativeFadeSheetPreservesHistory() {
        awaitLabel("probe-status") { $0 == self.home }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        app.buttons["probe-open-fade-sheet"].tap()
        XCTAssertTrue(app.staticTexts["page-fade-sheet"].waitForExistence(timeout: 10))
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail] fade-sheet [fade-sheet]") }
        awaitLabel("probe-status") {
            $0 == "home[home,home-detail] history=2 [InAppCommand@home[home,home-detail];InAppCommand@home[home,home-detail]]"
        }
        let style = awaitLabel("probe-crossfade-capability") { $0 == "crossFade" || $0 == "automatic" }
        let expectedMotion = style == "crossFade" ? "sheetMotion=fade" : "sheetMotion=slide"
        awaitLabel("probe-animation-style") { $0 == expectedMotion }
        capture("AppNav fade sheet")
        app.buttons["probe-dismiss-fade-sheet"].tap()
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        awaitLabel("probe-status") {
            $0 == "home[home,home-detail] history=3 [InAppCommand@home[home,home-detail];InAppCommand@home[home,home-detail];HostReconciliation@home[home,home-detail]]"
        }
    }

    func testManagedPushInsideSettledSheet() {
        awaitLabel("probe-status") { $0 == self.home }
        openModal()
        XCTAssertTrue(app.staticTexts["page-review"].waitForExistence(timeout: 10))
        app.navigationBars["review"].buttons.firstMatch.tap()

        let review = app.buttons["probe-push-review"]
        XCTAssertTrue(review.waitForExistence(timeout: 10))
        review.tap()
        XCTAssertTrue(app.staticTexts["page-review-again"].waitForExistence(timeout: 10))
        app.navigationBars["review-again"].buttons.firstMatch.tap()
        XCTAssertTrue(review.waitForExistence(timeout: 10))
        app.navigationBars["edit"].buttons["Done"].tap()

        awaitLabel("probe-status") { $0.hasPrefix("home[home,home-detail] history=6 ") }
        awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        XCTAssertTrue(app.staticTexts["page-home-detail"].exists)
    }

    func testGuardedBackUsesAppNavPolicy() {
        awaitLabel("probe-status") { $0 == self.home }
        let back = app.buttons["appnav-request-back"]
        let initialNative = awaitLabel("probe-native") { $0.hasSuffix("home [home,home-detail]") }
        // Start below the Game links so the List cannot interpret an unclaimed edge drag as row input.
        edgeSwipe(to: 0.60, at: 0.66)
        XCTAssertTrue(app.staticTexts["page-home-detail"].exists)
        XCTAssertEqual(app.staticTexts["probe-native"].label, initialNative)
        back.tap()
        awaitLabel("probe-status") { $0 == self.home + " back=Canceled" }
        XCTAssertEqual(app.staticTexts["probe-native"].label, initialNative)
        XCTAssertTrue(app.staticTexts["page-home-detail"].exists)

        app.buttons["probe-allow-back"].tap()
        back.tap()
        awaitLabel("probe-status") { $0.hasPrefix("home[home] history=2 ") && $0.hasSuffix("back=Completed") }
        awaitLabel("probe-native") { $0.hasSuffix("home [home]") }
        XCTAssertTrue(app.staticTexts["page-home"].exists)
    }

    private func openModal(file: StaticString = #filePath, line: UInt = #line) {
        let button = app.buttons["probe-open-modal"]
        let hittable = XCTNSPredicateExpectation(
            predicate: NSPredicate { _, _ in
                self.app.state == .runningForeground && button.exists && button.isHittable
            },
            object: nil)
        guard XCTWaiter.wait(for: [hittable], timeout: 10) == .completed else {
            XCTFail("The sheet button never became hittable in the foreground app.", file: file, line: line)
            return
        }
        button.tap()
    }

    @discardableResult
    private func awaitLabel(_ identifier: String, timeout: TimeInterval = 12, matching condition: @escaping (String) -> Bool,
                            file: StaticString = #filePath, line: UInt = #line) -> String {
        let element = app.staticTexts[identifier].firstMatch
        let predicate = NSPredicate { _, _ in element.exists && condition(element.label) }
        let result = XCTWaiter.wait(for: [XCTNSPredicateExpectation(predicate: predicate, object: nil)], timeout: timeout)
        XCTAssertEqual(result, .completed, "Expected \(identifier); got \(element.exists ? element.label : "<absent>")\n\(app.debugDescription)", file: file, line: line)
        return element.label
    }

    private func edgeSwipe(to fraction: CGFloat, at height: CGFloat = 0.45) {
        let origin = app.coordinate(withNormalizedOffset: CGVector(dx: 0.005, dy: height))
        let end = app.coordinate(withNormalizedOffset: CGVector(dx: fraction, dy: height))
        origin.press(forDuration: 0.05, thenDragTo: end, withVelocity: .slow, thenHoldForDuration: 0.4)
    }

    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: app.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    // MARK: Split: Home beside the selected game's stack

    // Phones and a closed iPhone Duo collapse the split into one stack. An open Duo shows both columns; the posture
    // tests wait for the device to fold or unfold, which the simulator controls drive from outside the test.
    private let splitHome = "split[home]@primary history=1 [InAppCommand@split[home]@primary]"

    private func splitLayout() -> String {
        awaitLabel("probe-split-layout") { $0.hasPrefix("collapsed") || $0.hasPrefix("expanded") }
    }

    private func requireSplit(_ layout: String) throws {
        let current = splitLayout()
        guard current.hasPrefix(layout) else { throw XCTSkip("Needs a \(layout) split; this window is \(current)") }
    }

    private func status(_ history: Int, _ state: String) -> (String) -> Bool {
        { $0.hasPrefix("\(state) history=\(history) ") }
    }

    /// Shows the primary column of an expanded split whose automatic visibility hid it.
    private func revealHome() {
        let row = app.buttons["probe-select-game-a"]
        guard !(row.exists && row.isHittable) else { return }
        let toggle = app.buttons.matching(NSPredicate(format: "identifier CONTAINS[c] 'sidebar' OR label CONTAINS[c] 'sidebar'")).firstMatch
        XCTAssertTrue(toggle.waitForExistence(timeout: 5), "Expected the system Show Sidebar button\n\(app.debugDescription)")
        toggle.tap()
        XCTAssertTrue(row.waitForExistence(timeout: 5))
    }

    // Selecting from Home pushes the game's root over Home, and the edge swipe from that root closes the column.
    func testSplitCollapsedSelectAndSwipeBack() throws {
        try requireSplit("collapsed")
        awaitLabel("probe-status") { $0 == self.splitHome }
        app.buttons["probe-select-game-a"].tap()
        awaitLabel("probe-status", matching: status(2, "split[home]+game-a-stack[game-a]@secondary"))
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [game-a] @secondary" }
        XCTAssertTrue(app.staticTexts["page-game-a"].exists)
        capture("Split collapsed Game A")

        edgeSwipe(to: 0.9)
        awaitLabel("probe-status") {
            self.status(3, "split[home]@primary")($0) && $0.hasSuffix(";HostReconciliation@split[home]@primary]")
        }
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [] @primary" }
        XCTAssertTrue(app.buttons["probe-select-game-b"].waitForExistence(timeout: 5))
        app.buttons["probe-select-game-b"].tap()
        awaitLabel("probe-status", matching: status(4, "split[home]+game-b-stack[game-b]@secondary"))
        XCTAssertTrue(app.staticTexts["page-game-b"].waitForExistence(timeout: 5))
    }

    // Switching from one game to another while collapsed replaces the game; one Back still reaches Home.
    func testSplitCollapsedSwitchGames() throws {
        try requireSplit("collapsed")
        awaitLabel("probe-status") { $0 == self.splitHome }
        app.buttons["probe-select-game-a"].tap()
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [game-a] @secondary" }
        app.buttons["probe-push-game-page"].tap()
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [game-a,game-a-page-1] @secondary" }
        edgeSwipe(to: 0.9)
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [game-a] @secondary" }
        app.buttons["probe-switch-game"].tap()
        awaitLabel("probe-status", matching: status(5, "split[home]+game-b-stack[game-b]@secondary"))
        awaitLabel("probe-native") { $0 == "rev=4 home-stack [home] + [game-b] @secondary" }
        XCTAssertTrue(app.staticTexts["page-game-b"].exists)
        XCTAssertFalse(app.staticTexts["page-game-a"].exists)

        edgeSwipe(to: 0.9)
        awaitLabel("probe-status", matching: status(6, "split[home]@primary"))
        awaitLabel("probe-native") { $0 == "rev=4 home-stack [home] + [] @primary" }
        XCTAssertTrue(app.buttons["probe-select-game-a"].waitForExistence(timeout: 5),
                      "One Back from the game must reach Home; a duplicate game root would stay on top")
    }

    // A Back that catches the animated selection before it settles takes it back: AppNav records the selection and a
    // native Back that closes the column, and the game does not come back.
    func testSplitSelectionTakenBack() throws {
        try requireSplit("collapsed")
        awaitLabel("probe-status") { $0 == self.splitHome }
        app.buttons["probe-select-game-a"].tap()
        XCTAssertTrue(app.staticTexts["page-game-a"].waitForExistence(timeout: 10))
        XCTAssertEqual(app.staticTexts["probe-native"].label, "rev=1 home-stack [home] + [] @primary",
                       "The shell settled the selection before Back")
        edgeSwipe(to: 0.9)
        awaitLabel("probe-split-layout") { $0.hasSuffix("taken back=1 restored=0") }
        awaitLabel("probe-status") {
            self.status(3, "split[home]@primary")($0) &&
                $0.hasSuffix("InAppCommand@split[home]+game-a-stack[game-a]@secondary;HostReconciliation@split[home]@primary]")
        }
        XCTAssertTrue(app.staticTexts["page-game-a"].waitForNonExistence(timeout: 10))
        let reappeared = expectation(for: NSPredicate(format: "exists == true"), evaluatedWith: app.staticTexts["page-game-a"])
        reappeared.isInverted = true
        wait(for: [reappeared], timeout: 3)
    }

    func testSplitSheetOverGame() throws {
        awaitLabel("probe-status") { $0 == self.splitHome }
        revealHome()
        app.buttons["probe-select-game-a"].tap()
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [game-a] @secondary" }
        openModal()
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [game-a] @secondary editor [edit]" }
        app.navigationBars["edit"].buttons["Done"].tap()
        awaitLabel("probe-status") {
            self.status(4, "split[home]+game-a-stack[game-a]@secondary")($0) &&
                $0.hasSuffix("HostReconciliation@split[home]+game-a-stack[game-a]@secondary]")
        }
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [game-a] @secondary" }
    }

    // Side by side, a selection shows the game beside Home; selecting another replaces it, and Back works inside it.
    func testSplitExpandedSwitchAndBack() throws {
        try requireSplit("expanded")
        let window = app.windows.firstMatch.frame
        if window.height > window.width {
            // On Xcode 27.2, XCUITest's synthesized taps on the game column of the upright Duo inner display never
            // reach the app, though the same taps from the simulator do. SWIFTUI.md records the manual upright run.
            throw XCTSkip("XCUITest cannot tap the detail column of an upright Duo inner display")
        }
        awaitLabel("probe-status") { $0 == self.splitHome }
        revealHome()
        app.buttons["probe-select-game-a"].tap()
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [game-a] @secondary" }
        revealHome()
        app.buttons["probe-select-game-b"].tap()
        awaitLabel("probe-status", matching: status(3, "split[home]+game-b-stack[game-b]@secondary"))
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [game-b] @secondary" }
        XCTAssertTrue(app.staticTexts["page-game-b"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.staticTexts["page-game-a"].exists)
        capture("Split expanded Game B")
        // Upright, Home overlays the game and stays open after a selection, as the system does. Close it with the
        // system button; side by side this hides the tiled column, which the game's own Back does not need.
        let hide = app.buttons["Hide Sidebar"].firstMatch
        if hide.exists { hide.tap() }
        // XCUITest reports the game's buttons hittable under the dimming view, so let the column finish closing.
        Thread.sleep(forTimeInterval: 1)

        app.buttons["probe-push-game-page"].tap()
        awaitLabel("probe-native") { $0 == "rev=4 home-stack [home] + [game-b,game-b-page-1] @secondary" }
        // The system Back button of the game's column; the only one while Home sits at its root beside it.
        let back = app.buttons["BackButton"].firstMatch
        XCTAssertTrue(back.waitForExistence(timeout: 5), "Expected the game column's Back button\n\(app.debugDescription)")
        back.tap()
        awaitLabel("probe-status", matching: status(5, "split[home]+game-b-stack[game-b]@secondary"))
        awaitLabel("probe-native") { $0 == "rev=4 home-stack [home] + [game-b] @secondary" }
        app.buttons["probe-split-back"].tap()
        awaitLabel("probe-status", matching: status(6, "split[home]@primary"))
        awaitLabel("probe-native") { $0 == "rev=5 home-stack [home] + [] @primary" }
    }

    // SwiftUI sets the compact column to the primary one when the game's path empties, though the game stays on top.
    // Unfolding repairs it, so folding again keeps the game on top with no duplicate under it.
    func testSplitFoldKeepsGameAfterPop() throws {
        // The device must be folded and unfolded from outside the test: run with TEST_RUNNER_APPNAV_POSTURE=1 and
        // change the posture when the test prints SPLIT-POSTURE.
        guard ProcessInfo.processInfo.environment["APPNAV_POSTURE"] == "1" else {
            throw XCTSkip("Set TEST_RUNNER_APPNAV_POSTURE=1 and drive the Duo's posture to run this test")
        }
        try requireSplit("collapsed")
        awaitLabel("probe-status") { $0 == self.splitHome }
        app.buttons["probe-select-game-a"].tap()
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [game-a] @secondary" }
        app.buttons["probe-push-game-page"].tap()
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [game-a,game-a-page-1] @secondary" }
        edgeSwipe(to: 0.9)
        awaitLabel("probe-status", matching: status(4, "split[home]+game-a-stack[game-a]@secondary"))
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [game-a] @secondary" }
        XCTAssertTrue(app.staticTexts["page-game-a"].exists)

        print("SPLIT-POSTURE: open the device")
        awaitLabel("probe-split-layout", timeout: 120) { $0.hasPrefix("expanded") }
        awaitLabel("probe-split-layout") { $0.hasPrefix("expanded repairs=1 ") }
        XCTAssertTrue(app.staticTexts["page-game-a"].exists)
        XCTAssertEqual(app.staticTexts["probe-native"].label, "rev=3 home-stack [home] + [game-a] @secondary")
        capture("Split unfolded after pop")

        print("SPLIT-POSTURE: close the device")
        awaitLabel("probe-split-layout", timeout: 120) { $0.hasPrefix("collapsed") }
        XCTAssertTrue(app.staticTexts["page-game-a"].waitForExistence(timeout: 5))
        XCTAssertEqual(app.staticTexts["probe-native"].label, "rev=3 home-stack [home] + [game-a] @secondary")
        awaitLabel("probe-status", matching: status(4, "split[home]+game-a-stack[game-a]@secondary"))
        capture("Split folded again")

        edgeSwipe(to: 0.9)
        awaitLabel("probe-status", matching: status(5, "split[home]@primary"))
        XCTAssertTrue(app.buttons["probe-select-game-a"].waitForExistence(timeout: 5),
                      "One Back from the game must reach Home")
    }

    // A guarded game root stacked over Home on a collapsed window has the app's Back instead of the system Back, so
    // neither the edge swipe nor the Back button gets past the policy.
    func testSplitGuardedGameRootCollapsed() throws {
        try requireSplit("collapsed")
        awaitLabel("probe-status") { $0 == self.splitHome }
        app.buttons["probe-select-game-a"].tap()
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [game-a] @secondary" }
        let back = app.buttons["appnav-request-back"]
        XCTAssertTrue(back.waitForExistence(timeout: 5), "The guarded root needs the app's Back")
        XCTAssertFalse(app.buttons["BackButton"].exists, "The system Back would skip the policy")
        edgeSwipe(to: 0.9)
        XCTAssertTrue(app.staticTexts["page-game-a"].exists)
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [game-a] @secondary" }
        XCTAssertTrue(app.staticTexts["probe-status"].label.hasPrefix("split[home]+game-a-stack[game-a]@secondary history=2 "))
        back.tap()
        awaitLabel("probe-status") { $0.hasPrefix("split[home]+game-a-stack[game-a]@secondary history=2 ") && $0.hasSuffix("back=Canceled") }
        XCTAssertTrue(app.staticTexts["page-game-a"].exists)

        app.buttons["probe-allow-back"].tap()
        back.tap()
        awaitLabel("probe-status") { $0.hasPrefix("split[home]@primary history=3 ") && $0.hasSuffix("back=Completed") }
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [] @primary" }
    }

    // Side by side the guarded root has nothing behind it, so it gets no Back of either kind.
    func testSplitGuardedGameRootExpanded() throws {
        try requireSplit("expanded")
        let window = app.windows.firstMatch.frame
        if window.height > window.width {
            throw XCTSkip("XCUITest cannot tap the detail column of an upright Duo inner display")
        }
        awaitLabel("probe-status") { $0 == self.splitHome }
        revealHome()
        app.buttons["probe-select-game-a"].tap()
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [game-a] @secondary" }
        XCTAssertFalse(app.buttons["appnav-request-back"].exists)
        XCTAssertFalse(app.buttons["BackButton"].exists)
    }

    // A guarded page has only the app's Back. The shell does not zoom it in, since UIKit's zoom dismissal would pop it
    // past the policy, so the edge swipe leaves it in place.
    func testGuardedZoomedGameKeepsItsPolicy() {
        awaitLabel("probe-status") { $0 == self.home }
        app.buttons["probe-open-game"].press(forDuration: 0.22)
        awaitLabel("probe-status") { $0 == self.game }
        let back = app.buttons["appnav-request-back"]
        XCTAssertTrue(back.waitForExistence(timeout: 5))
        edgeSwipe(to: 0.9)
        XCTAssertTrue(app.staticTexts["page-game"].exists, "The edge swipe popped a guarded page")
        XCTAssertEqual(app.staticTexts["probe-status"].label, game)
    }

    // When the app stops intercepting the root while it shows, the guard puts back the system Back and the swipes.
    func testSplitGuardedGameRootReleased() throws {
        try requireSplit("collapsed")
        awaitLabel("probe-status") { $0 == self.splitHome }
        app.buttons["probe-select-game-a"].tap()
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [game-a] @secondary" }
        XCTAssertTrue(app.buttons["appnav-request-back"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.buttons["BackButton"].exists)
        app.buttons["probe-stop-guarding"].tap()
        XCTAssertTrue(app.buttons["appnav-request-back"].waitForNonExistence(timeout: 5))
        XCTAssertTrue(app.buttons["BackButton"].waitForExistence(timeout: 5), "The system Back did not come back")
        edgeSwipe(to: 0.9)
        awaitLabel("probe-status") {
            self.status(3, "split[home]@primary")($0) && $0.hasSuffix(";HostReconciliation@split[home]@primary]")
        }
    }

    // A Back swipe from a page above the guarded root lands on the root: the guard waits for the swipe to settle
    // instead of turning the recognizer off under it, then holds against both the edge and the content swipe.
    func testSplitGuardedGameRootSwipeOntoRoot() throws {
        try requireSplit("collapsed")
        awaitLabel("probe-status") { $0 == self.splitHome }
        app.buttons["probe-select-game-a"].tap()
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [game-a] @secondary" }
        app.buttons["probe-push-game-page"].tap()
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [game-a,game-a-page-1] @secondary" }
        edgeSwipe(to: 0.9)
        awaitLabel("probe-gesture") { $0.hasPrefix("began=1 cancelled=0 completed=1") }
        awaitLabel("probe-status", matching: status(4, "split[home]+game-a-stack[game-a]@secondary"))
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [game-a] @secondary" }
        XCTAssertTrue(app.buttons["appnav-request-back"].waitForExistence(timeout: 5))

        edgeSwipe(to: 0.9)
        let start = app.coordinate(withNormalizedOffset: CGVector(dx: 0.3, dy: 0.3))
        start.press(forDuration: 0.05, thenDragTo: app.coordinate(withNormalizedOffset: CGVector(dx: 0.95, dy: 0.3)),
                    withVelocity: .slow, thenHoldForDuration: 0.4)
        Thread.sleep(forTimeInterval: 1)
        XCTAssertTrue(app.staticTexts["page-game-a"].exists, "A swipe popped the guarded root")
        awaitLabel("probe-status", matching: status(4, "split[home]+game-a-stack[game-a]@secondary"))
    }

    // An AppNav Back from a guarded root, after a pop that left SwiftUI's stray compact column, closes the column
    // through the native pop; the guard holds until the root has left the top.
    func testSplitGuardedGameRootAppNavBackAfterPop() throws {
        try requireSplit("collapsed")
        awaitLabel("probe-status") { $0 == self.splitHome }
        app.buttons["probe-select-game-a"].tap()
        awaitLabel("probe-native") { $0 == "rev=2 home-stack [home] + [game-a] @secondary" }
        app.buttons["probe-push-game-page"].tap()
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [game-a,game-a-page-1] @secondary" }
        edgeSwipe(to: 0.9)
        awaitLabel("probe-native") { $0 == "rev=3 home-stack [home] + [game-a] @secondary" }
        let back = app.buttons["appnav-request-back"]
        XCTAssertTrue(back.waitForExistence(timeout: 5))
        app.buttons["probe-allow-back"].tap()
        back.tap()
        awaitLabel("probe-status") { $0.hasPrefix("split[home]@primary history=5 ") && $0.hasSuffix("back=Completed") }
        awaitLabel("probe-native") { $0 == "rev=4 home-stack [home] + [] @primary" }
        XCTAssertTrue(app.buttons["probe-select-game-a"].waitForExistence(timeout: 5))
    }
}

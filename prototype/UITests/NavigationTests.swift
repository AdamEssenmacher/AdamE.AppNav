import XCTest

final class NavigationTests: XCTestCase {
    let app = XCUIApplication(bundleIdentifier: "com.adamessenmacher.appnav.swiftuiprobe")

    override func setUpWithError() throws {
        continueAfterFailure = false
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
    // SwiftUI must not push Game again. The probe holds the push applying until Game leaves the top of the stack.
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
    private func awaitLabel(_ identifier: String, matching condition: @escaping (String) -> Bool,
                            file: StaticString = #filePath, line: UInt = #line) -> String {
        let element = app.staticTexts[identifier].firstMatch
        let predicate = NSPredicate { _, _ in element.exists && condition(element.label) }
        let result = XCTWaiter.wait(for: [XCTNSPredicateExpectation(predicate: predicate, object: nil)], timeout: 12)
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
}

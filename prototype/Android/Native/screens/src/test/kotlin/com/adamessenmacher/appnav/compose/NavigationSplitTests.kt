package com.adamessenmacher.appnav.compose

import androidx.activity.BackEventCompat
import androidx.activity.ComponentActivity
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalWindowInfo
import androidx.compose.ui.platform.WindowInfo
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.getUnclippedBoundsInRoot
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.unit.DpSize
import androidx.compose.ui.unit.dp
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

private val Unfolded = DpSize(852.dp, 883.dp)
private val Folded = DpSize(443.dp, 994.dp)
private val FoldedSideways = DpSize(994.dp, 443.dp)

// The real window is large enough for two panes; FakeWindow decides the layout a test sees.
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [36], qualifiers = "w900dp-h900dp")
class NavigationSplitTests {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    private val window = mutableStateOf(Unfolded)
    private val observations = mutableListOf<NavigationObservation>()

    @Test
    @Config(qualifiers = "w900dp-h600dp")
    fun expandedWindowShowsListBesideDetail() {
        compose.setContent { Shell(split(listOf("game-a"))) }

        compose.onNodeWithText("home").assertExists()
        compose.onNodeWithText("game-a").assertExists()
        compose.runOnIdle {
            assertEquals(listOf("home"), observations.last().entries)
            assertEquals(listOf("game-a"), observations.last().secondaryEntries)
            assertEquals(NavigationSplitColumn.Secondary, observations.last().compactColumn)
        }
    }

    @Test
    @Config(qualifiers = "w400dp-h800dp")
    fun compactWindowStacksSecondaryOverPrimary() {
        compose.setContent { Shell(split(listOf("game-a"))) }

        compose.onNodeWithText("game-a").assertExists()
        compose.onNodeWithText("home").assertDoesNotExist()
    }

    @Test
    fun sidewaysPhoneKeepsOnePane() {
        window.value = FoldedSideways
        compose.setContent { FakeWindow { Shell(split(listOf("game-a"))) } }

        compose.onNodeWithText("game-a").assertExists()
        compose.onNodeWithText("home").assertDoesNotExist()
    }

    @Test
    fun placeholderFillsTheDetailPaneWithoutSelection() {
        compose.setContent { FakeWindow { Shell(split(emptyList())) } }

        compose.onNodeWithText("home").assertExists()
        compose.onNodeWithText("Pick a game").assertExists()
        compose.runOnIdle {
            assertEquals(emptyList<String>(), observations.last().secondaryEntries)
            assertEquals(NavigationSplitColumn.Primary, observations.last().compactColumn)
        }
    }

    @Test
    fun displayDefaultShowsPrimaryInOnePaneAndKeepsItsState() {
        window.value = Folded
        compose.setContent {
            FakeWindow { Shell(split(listOf("game-a"), compact = NavigationSplitColumn.Primary), counters = true) }
        }
        compose.onNodeWithText("home 0").assertExists()
        compose.onNodeWithText("game-a 0").assertDoesNotExist()

        compose.runOnIdle { window.value = Unfolded }
        compose.onNodeWithText("game-a 0").performClick()
        compose.onNodeWithText("game-a 1").assertExists()
        compose.runOnIdle { window.value = Folded }
        compose.onNodeWithText("game-a 1").assertDoesNotExist()
        compose.runOnIdle { window.value = Unfolded }
        compose.onNodeWithText("game-a 1").assertExists()
        compose.runOnIdle {
            assertEquals(1, observations.size)
            assertEquals(NavigationSplitColumn.Primary, observations.last().compactColumn)
        }
    }

    @Test
    fun foldAndUnfoldKeepStateAndNeverReport() {
        compose.setContent { FakeWindow { Shell(split(listOf("game-a", "page")), counters = true) } }
        compose.onNodeWithText("page 0").performClick()
        compose.onNodeWithText("home 0").assertExists()

        compose.runOnIdle { window.value = Folded }
        compose.onNodeWithText("page 1").assertExists()
        compose.onNodeWithText("home 0").assertDoesNotExist()
        compose.runOnIdle { window.value = Unfolded }
        compose.onNodeWithText("page 1").assertExists()
        compose.onNodeWithText("home 0").assertExists()
        compose.runOnIdle { assertEquals(1, observations.size) }
    }

    @Test
    fun twoPaneBackPopsInsideTheDetail() {
        compose.setContent { FakeWindow { Shell(split(listOf("game-a", "page"))) } }
        compose.onNodeWithText("page").assertExists()

        compose.runOnIdle { compose.activity.onBackPressedDispatcher.onBackPressed() }
        compose.onNodeWithText("page").assertDoesNotExist()
        compose.onNodeWithText("game-a").assertExists()
        compose.onNodeWithText("home").assertExists()
        compose.runOnIdle {
            assertEquals(listOf("home"), observations.last().entries)
            assertEquals(listOf("game-a"), observations.last().secondaryEntries)
            assertEquals(NavigationSplitColumn.Secondary, observations.last().compactColumn)
        }
    }

    @Test
    fun twoPaneBackAtTheDetailRootLeavesTheApp() {
        var requested = 0
        compose.setContent { FakeWindow { Shell(split(listOf("game-a")), requestBack = { requested++ }) } }
        compose.onNodeWithText("game-a").assertExists()

        compose.runOnIdle {
            assertFalse(compose.activity.onBackPressedDispatcher.hasEnabledCallbacks())
            compose.activity.onBackPressedDispatcher.onBackPressed()
        }
        compose.runOnIdle {
            assertEquals(0, requested)
            assertTrue(compose.activity.isFinishing)
        }
    }

    @Test
    fun twoPaneBackWithADeeperPrimaryClosesTheDetailAsCorePlans() {
        compose.setContent { FakeWindow { Shell(split(listOf("game-a"), primary = listOf("home", "more"))) } }
        compose.onNodeWithText("game-a").assertExists()

        compose.runOnIdle { compose.activity.onBackPressedDispatcher.onBackPressed() }
        compose.onNodeWithText("game-a").assertDoesNotExist()
        compose.onNodeWithText("Pick a game").assertExists()
        compose.runOnIdle {
            assertEquals(listOf("home", "more"), observations.last().entries)
            assertEquals(emptyList<String>(), observations.last().secondaryEntries)
            assertEquals(NavigationSplitColumn.Primary, observations.last().compactColumn)
        }
    }

    @Test
    fun onePaneBackAtTheSecondaryRootClosesTheColumn() {
        window.value = Folded
        compose.setContent { FakeWindow { Shell(split(listOf("game-a"))) } }
        compose.onNodeWithText("game-a").assertExists()

        compose.runOnIdle { compose.activity.onBackPressedDispatcher.onBackPressed() }
        compose.onNodeWithText("home").assertExists()
        compose.onNodeWithText("game-a").assertDoesNotExist()
        compose.runOnIdle {
            assertEquals(listOf("home"), observations.last().entries)
            assertEquals(emptyList<String>(), observations.last().secondaryEntries)
            assertEquals(NavigationSplitColumn.Primary, observations.last().compactColumn)
            assertFalse(compose.activity.isFinishing)
        }
    }

    @Test
    fun onePaneManagedBackAsksTheRouterAtTheSecondaryRoot() {
        window.value = Folded
        var requested = 0
        compose.setContent { FakeWindow { Shell(split(listOf("game-a")), requestBack = { requested++ }) } }
        compose.onNodeWithText("game-a").assertExists()

        compose.runOnIdle { compose.activity.onBackPressedDispatcher.onBackPressed() }
        compose.runOnIdle {
            assertEquals(1, requested)
            assertFalse(compose.activity.isFinishing)
        }
        compose.onNodeWithText("game-a").assertExists()
    }

    @Test
    fun displayDefaultBackPopsThePrimaryAndClosesTheDefault() {
        window.value = Folded
        compose.setContent {
            FakeWindow {
                Shell(split(listOf("game-a"), primary = listOf("home", "more"), compact = NavigationSplitColumn.Primary))
            }
        }
        compose.onNodeWithText("more").assertExists()

        compose.runOnIdle { compose.activity.onBackPressedDispatcher.onBackPressed() }
        compose.onNodeWithText("home").assertExists()
        compose.runOnIdle {
            assertEquals(listOf("home"), observations.last().entries)
            assertEquals(emptyList<String>(), observations.last().secondaryEntries)
            assertEquals(NavigationSplitColumn.Primary, observations.last().compactColumn)
        }
    }

    @Test
    fun switchingGamesReplacesTheDetailPane() {
        val target = mutableStateOf(split(listOf("game-a")))
        compose.setContent { FakeWindow { Shell(target.value) } }
        compose.onNodeWithText("game-a").assertExists()

        compose.runOnIdle { target.value = split(listOf("game-b"), revision = 2) }
        compose.onNodeWithText("game-b").assertExists()
        compose.onNodeWithText("game-a").assertDoesNotExist()
        compose.onNodeWithText("home").assertExists()
        compose.runOnIdle { assertEquals(listOf("game-b"), observations.last().secondaryEntries) }
    }

    @Test
    fun modalCoversTheSplitAndTakesBack() {
        compose.setContent {
            FakeWindow { Shell(split(listOf("game-a"), modal = "sheet"), modalHost = { _, content -> content() }) }
        }
        compose.onNodeWithText("sheet-root").assertExists()

        compose.runOnIdle { compose.activity.onBackPressedDispatcher.onBackPressed() }
        compose.onNodeWithText("sheet-root").assertDoesNotExist()
        compose.onNodeWithText("game-a").assertExists()
        compose.runOnIdle {
            assertEquals("", observations.last().modalId)
            assertEquals(listOf("game-a"), observations.last().secondaryEntries)
            assertFalse(compose.activity.isFinishing)
        }
    }

    @Test
    fun entriesKnowTheirColumnAndLayout() {
        val scopes = mutableMapOf<String, NavigationEntryScope>()
        compose.setContent {
            FakeWindow {
                AppNavNavigationShell(split(listOf("game-a")), observations::add, unavailableContent = {
                    scopes[it.entryId] = checkNotNull(LocalNavigationEntryScope.current)
                    Text(it.entryId)
                })
            }
        }
        compose.runOnIdle {
            assertEquals(NavigationSplitColumn.Primary, scopes.getValue("home").splitColumn)
            assertEquals(NavigationSplitColumn.Secondary, scopes.getValue("game-a").splitColumn)
            assertTrue(scopes.getValue("game-a").isSplitExpanded)
            assertNull(scopes.getValue("game-a").origin)
        }
    }

    @Test
    fun foldSwapsLayoutsWithoutAnimating() {
        compose.setContent { FakeWindow { Shell(split(listOf("game-a"))) } }
        compose.onNodeWithText("home").assertExists()
        compose.mainClock.autoAdvance = false

        // Nav3's default fade lasts 700 ms (about 44 frames); a swap takes the few frames a scene change needs.
        compose.runOnIdle { window.value = Folded }
        val folded = framesUntil { compose.onAllNodesWithText("home").fetchSemanticsNodes().isEmpty() }
        assertTrue("The fold took $folded frames", folded <= 6)
        compose.onNodeWithText("game-a").assertExists()

        compose.runOnIdle { window.value = Unfolded }
        val unfolded = framesUntil {
            compose.onAllNodesWithText("home").fetchSemanticsNodes().size == 1 &&
                compose.onAllNodesWithText("game-a").fetchSemanticsNodes().size == 1
        }
        assertTrue("The unfold took $unfolded frames", unfolded <= 6)
        compose.mainClock.autoAdvance = true
        compose.runOnIdle { assertEquals(1, observations.size) }
    }

    private fun framesUntil(done: () -> Boolean): Int {
        var frames = 0
        while (!done() && frames < 120) {
            compose.mainClock.advanceTimeByFrame()
            frames++
        }
        return frames
    }

    @Test
    fun foldDuringAPaneAnimationSettlesOnce() {
        val target = mutableStateOf(split(listOf("game-a")))
        compose.setContent { FakeWindow { Shell(target.value) } }
        compose.onNodeWithText("game-a").assertExists()
        compose.mainClock.autoAdvance = false

        compose.runOnIdle { target.value = split(listOf("game-a", "page"), revision = 2) }
        compose.mainClock.advanceTimeBy(150)
        compose.runOnIdle { window.value = Folded }
        compose.mainClock.autoAdvance = true
        compose.onNodeWithText("page").assertExists()
        compose.onNodeWithText("home").assertDoesNotExist()
        compose.runOnIdle {
            assertEquals(1, observations.count { it.revision == 2L })
            assertEquals(listOf("game-a", "page"), observations.last().secondaryEntries)
        }
    }

    @Test
    fun unfoldingDuringAPredictiveBackKeepsTheGestureAndItsBack() {
        window.value = Folded
        compose.setContent { FakeWindow { Shell(split(listOf("game-a"))) } }
        compose.onNodeWithText("game-a").assertExists()
        val dispatcher = compose.activity.onBackPressedDispatcher

        compose.runOnIdle {
            dispatcher.dispatchOnBackStarted(BackEventCompat(10f, 500f, 0f, BackEventCompat.EDGE_LEFT))
            dispatcher.dispatchOnBackProgressed(BackEventCompat(200f, 500f, 0.4f, BackEventCompat.EDGE_LEFT))
        }
        compose.runOnIdle { window.value = Unfolded }
        compose.runOnIdle {
            assertTrue(dispatcher.hasEnabledCallbacks())
            dispatcher.onBackPressed()
        }
        compose.onNodeWithText("Pick a game").assertExists()
        compose.onNodeWithText("home").assertExists()
        compose.runOnIdle {
            assertEquals(emptyList<String>(), observations.last().secondaryEntries)
            assertEquals(NavigationSplitColumn.Primary, observations.last().compactColumn)
            assertFalse(compose.activity.isFinishing)
        }
    }

    @Test
    fun unfoldingDuringAManagedBackGestureStillAsksTheRouter() {
        window.value = Folded
        var requested = 0
        compose.setContent { FakeWindow { Shell(split(listOf("game-a")), requestBack = { requested++ }) } }
        compose.onNodeWithText("game-a").assertExists()
        val dispatcher = compose.activity.onBackPressedDispatcher

        compose.runOnIdle {
            dispatcher.dispatchOnBackStarted(BackEventCompat(10f, 500f, 0f, BackEventCompat.EDGE_LEFT))
            dispatcher.dispatchOnBackProgressed(BackEventCompat(200f, 500f, 0.4f, BackEventCompat.EDGE_LEFT))
        }
        compose.runOnIdle { window.value = Unfolded }
        compose.runOnIdle {
            assertTrue(dispatcher.hasEnabledCallbacks())
            dispatcher.onBackPressed()
        }
        compose.runOnIdle {
            assertEquals(1, requested)
            assertFalse(compose.activity.isFinishing)
        }
    }

    @Test
    fun splitOriginsDropSideBySideButAModalKeepsItsOwn() {
        val origins = NavigationOrigins()
        val home = MountedEntry("home", 1)
        val game = MountedEntry("game-a", 2)
        val edit = MountedEntry("edit", 3)
        val review = MountedEntry("review", 4)
        val selection = NavigationProjection(1, "home-stack", false,
            listOf(NavigationBranch("home-stack", "home-stack", listOf(home))), "", emptyList(),
            NavigationPresentationTransition("push", "home", "game-a", "cover", "home", "game-a"),
            NavigationSplit("games", "game-a-stack", listOf(game), NavigationSplitColumn.Secondary))
        origins.stage(selection)
        val zoom = origins.pushFor(game, selection.transition, listOf(home, game))
        assertEquals("cover", zoom?.sourceKey)

        // Unfolded: the selection shows beside its source, so neither now nor after a fold does it zoom.
        origins.dropSplit(selection)
        origins.stage(selection)
        assertNull(origins.pushFor(game, selection.transition, listOf(home, game)))

        // A sheet over the unfolded split keeps its own push's origin.
        val sheet = selection.copy(revision = 2, modalId = "sheet", modalEntries = listOf(edit, review),
            transition = NavigationPresentationTransition("push", "edit", "review", "review-cover", "edit", "review"))
        origins.dropSplit(sheet)
        origins.stage(sheet)
        assertEquals("review-cover", origins.pushFor(review, sheet.transition, listOf(edit, review))?.sourceKey)
        assertNull(origins.pushFor(game, sheet.transition, listOf(home, game)))
    }

    @Test
    fun navigationRootsHaveNoUpWithinTheShell() {
        val scopes = mutableMapOf<String, NavigationEntryScope>()
        val target = mutableStateOf(split(listOf("game-a", "page")))
        compose.setContent {
            FakeWindow {
                AppNavNavigationShell(target.value, observations::add, modalHost = { _, content -> content() },
                    unavailableContent = {
                        scopes[it.entryId] = checkNotNull(LocalNavigationEntryScope.current)
                        Text(it.entryId)
                    })
            }
        }
        // Two panes: Home's root in the list pane has no Up; a game page does.
        compose.runOnIdle {
            assertTrue(scopes.getValue("home").isNavigationRoot)
            assertFalse(scopes.getValue("page").isNavigationRoot)
            target.value = split(listOf("game-a"), revision = 2)
        }
        // The game's root beside Home's root has no Up either; Back there leaves the app.
        compose.onNodeWithText("page").assertDoesNotExist()
        compose.runOnIdle {
            assertTrue(scopes.getValue("game-a").isNavigationRoot)
            assertTrue(scopes.getValue("home").isNavigationRoot)
            window.value = Folded
        }
        // Stacked over Home, the game's root has an Up that closes the column.
        compose.runOnIdle { assertFalse(scopes.getValue("game-a").isNavigationRoot) }
        compose.runOnIdle {
            window.value = Unfolded
            target.value = split(listOf("game-a"), primary = listOf("home", "more"), revision = 3)
        }
        compose.onNodeWithText("more").assertExists()
        compose.runOnIdle {
            assertFalse(scopes.getValue("game-a").isNavigationRoot)
            assertFalse(scopes.getValue("more").isNavigationRoot)
        }
        // One pane with a hidden default secondary: Home's root has no Up.
        compose.runOnIdle {
            window.value = Folded
            target.value = split(listOf("game-a"), compact = NavigationSplitColumn.Primary, revision = 4)
        }
        compose.onNodeWithText("home").assertExists()
        compose.runOnIdle { assertTrue(scopes.getValue("home").isNavigationRoot) }
        // A modal covers Home: inside the modal and under it, nothing is a navigation root.
        compose.runOnIdle { target.value = split(listOf("game-a"), compact = NavigationSplitColumn.Primary, revision = 5, modal = "sheet") }
        compose.onNodeWithText("sheet-root").assertExists()
        compose.runOnIdle {
            assertFalse(scopes.getValue("sheet-root").isNavigationRoot)
            assertFalse(scopes.getValue("home").isNavigationRoot)
        }
    }

    @Test
    fun aCancelledGestureReleasesTheHoldAndAppliesAFoldMadeDuringIt() {
        window.value = Folded
        compose.setContent { FakeWindow { Shell(split(listOf("game-a"))) } }
        compose.onNodeWithText("game-a").assertExists()
        val dispatcher = compose.activity.onBackPressedDispatcher

        compose.runOnIdle {
            dispatcher.dispatchOnBackStarted(BackEventCompat(10f, 500f, 0f, BackEventCompat.EDGE_LEFT))
            dispatcher.dispatchOnBackProgressed(BackEventCompat(200f, 500f, 0.4f, BackEventCompat.EDGE_LEFT))
        }
        // The preview composes Home under the game, so read the layout from where the game sits.
        compose.runOnIdle { window.value = Unfolded }
        assertTrue(compose.onNodeWithText("game-a").getUnclippedBoundsInRoot().left < SplitListPaneWidth)
        compose.runOnIdle { dispatcher.dispatchOnBackCancelled() }
        compose.onNodeWithText("home").assertExists()
        assertTrue(compose.onNodeWithText("game-a").getUnclippedBoundsInRoot().left >= SplitListPaneWidth)
        compose.runOnIdle {
            assertEquals(listOf("game-a"), observations.last().secondaryEntries)
            assertEquals(1, observations.size)
        }
    }

    @Test
    fun anInlineModalGestureHoldsTheLayoutThroughAnUnfold() {
        window.value = Folded
        compose.setContent {
            FakeWindow { Shell(split(listOf("game-a"), modal = "sheet"), modalHost = { _, content -> content() }) }
        }
        compose.onNodeWithText("sheet-root").assertExists()
        val dispatcher = compose.activity.onBackPressedDispatcher

        compose.runOnIdle { dispatcher.dispatchOnBackStarted(BackEventCompat(10f, 500f, 0f, BackEventCompat.EDGE_LEFT)) }
        compose.runOnIdle { window.value = Unfolded }
        compose.onNodeWithText("home").assertDoesNotExist()
        compose.runOnIdle { dispatcher.onBackPressed() }
        compose.onNodeWithText("sheet-root").assertDoesNotExist()
        compose.onNodeWithText("home").assertExists()
        compose.onNodeWithText("game-a").assertExists()
        compose.runOnIdle {
            assertEquals("", observations.last().modalId)
            assertEquals(listOf("game-a"), observations.last().secondaryEntries)
            assertFalse(compose.activity.isFinishing)
        }
    }

    @Test
    fun twoPanesNeedExpandedWidthAndMediumHeight() {
        window.value = DpSize(839.dp, 900.dp)
        compose.setContent { FakeWindow { Shell(split(listOf("game-a"))) } }
        compose.onNodeWithText("home").assertDoesNotExist()
        compose.runOnIdle { window.value = DpSize(840.dp, 900.dp) }
        compose.onNodeWithText("home").assertExists()
        compose.runOnIdle { window.value = DpSize(900.dp, 479.dp) }
        compose.onNodeWithText("home").assertDoesNotExist()
        compose.runOnIdle { window.value = DpSize(900.dp, 480.dp) }
        compose.onNodeWithText("home").assertExists()
        compose.runOnIdle { assertEquals(1, observations.size) }
    }

    @Test
    fun aStackWithoutSplitIgnoresTheWindowSize() {
        window.value = Folded
        val stack = NavigationProjection(1L, "home", false,
            listOf(NavigationBranch("home", "Home", listOf(MountedEntry("home", 0), MountedEntry("detail", 0)))), "", emptyList())
        compose.setContent { FakeWindow { Shell(stack) } }
        compose.onNodeWithText("detail").assertExists()
        compose.runOnIdle { window.value = Unfolded }
        compose.onNodeWithText("detail").assertExists()
        compose.onNodeWithText("home").assertDoesNotExist()
        compose.runOnIdle { window.value = Folded }
        compose.onNodeWithText("detail").assertExists()
        compose.runOnIdle {
            assertEquals(1, observations.size)
            assertNull(observations.last().compactColumn)
        }
    }

    @Test
    fun aDeeperDefaultSecondaryShowsBesideAPrimaryOnTop() {
        compose.setContent {
            FakeWindow { Shell(split(listOf("game-a", "page"), compact = NavigationSplitColumn.Primary)) }
        }
        compose.onNodeWithText("home").assertExists()
        compose.onNodeWithText("page").assertExists()
        // The primary column on top is at its root, so Back leaves the app.
        compose.runOnIdle { assertFalse(compose.activity.onBackPressedDispatcher.hasEnabledCallbacks()) }
    }

    @Test
    fun aDeeperPrimaryOnTopPopsAndClosesADeeperDefault() {
        compose.setContent {
            FakeWindow {
                Shell(split(listOf("game-a", "page"), primary = listOf("home", "more"), compact = NavigationSplitColumn.Primary))
            }
        }
        compose.onNodeWithText("page").assertExists()

        compose.runOnIdle { compose.activity.onBackPressedDispatcher.onBackPressed() }
        compose.onNodeWithText("Pick a game").assertExists()
        compose.runOnIdle {
            assertEquals(listOf("home"), observations.last().entries)
            assertEquals(emptyList<String>(), observations.last().secondaryEntries)
            assertEquals(NavigationSplitColumn.Primary, observations.last().compactColumn)
        }
    }

    @Composable
    private fun FakeWindow(content: @Composable () -> Unit) {
        val real = LocalWindowInfo.current
        val size by window
        val info = object : WindowInfo {
            override val isWindowFocused: Boolean get() = real.isWindowFocused
            override val containerDpSize: DpSize get() = size
        }
        CompositionLocalProvider(LocalWindowInfo provides info, content = content)
    }

    @Composable
    private fun Shell(
        target: NavigationProjection,
        requestBack: (suspend () -> Unit)? = null,
        counters: Boolean = false,
        modalHost: (@Composable (NavigationModalPresentation, @Composable () -> Unit) -> Unit)? = null,
    ) {
        val content: @Composable (MountedEntry) -> Unit = { entry ->
            if (counters) {
                var count by rememberSaveable { mutableIntStateOf(0) }
                TextButton(onClick = { count++ }) { Text("${entry.entryId} $count") }
            } else {
                Text(entry.entryId)
            }
        }
        CompositionLocalProvider(LocalNavigationSplitPlaceholder provides { Text("Pick a game") }) {
            if (modalHost == null) {
                AppNavNavigationShell(target, observations::add, requestBack = requestBack, unavailableContent = content,
                    onBackFailure = {})
            } else {
                AppNavNavigationShell(target, observations::add, modalHost, requestBack = requestBack,
                    unavailableContent = content)
            }
        }
    }

    private fun split(
        secondary: List<String>,
        primary: List<String> = listOf("home"),
        compact: NavigationSplitColumn =
            if (secondary.isEmpty()) NavigationSplitColumn.Primary else NavigationSplitColumn.Secondary,
        revision: Long = 1L,
        modal: String = "",
    ) = NavigationProjection(
        revision,
        "home-stack",
        false,
        listOf(NavigationBranch("home-stack", "home-stack", primary.map { MountedEntry(it, 0) })),
        modal,
        if (modal.isEmpty()) emptyList() else listOf(MountedEntry("$modal-root", 0)),
        split = NavigationSplit(
            "games",
            secondary.firstOrNull()?.let { "$it-stack" }.orEmpty(),
            secondary.map { MountedEntry(it, 0) },
            compact,
        ),
    )
}

package com.adamessenmacher.appnav.compose

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
        if (modalHost == null) {
            AppNavNavigationShell(target, observations::add, requestBack = requestBack, unavailableContent = content,
                onBackFailure = {}, splitPlaceholder = { Text("Pick a game") })
        } else {
            AppNavNavigationShell(target, observations::add, modalHost, requestBack = requestBack,
                unavailableContent = content, splitPlaceholder = { Text("Pick a game") })
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

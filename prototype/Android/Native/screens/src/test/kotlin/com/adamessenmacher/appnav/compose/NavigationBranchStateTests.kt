package com.adamessenmacher.appnav.compose

import androidx.activity.ComponentActivity
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.junit4.StateRestorationTester
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [36])
class NavigationBranchStateTests {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun restoreKeepsSurvivingBranchStateAndDiscardsRemovedBranchState() {
        val home = NavigationBranch("home", "Home", listOf(MountedEntry("home-entry", 0)))
        val inbox = NavigationBranch("inbox", "Inbox", listOf(MountedEntry("inbox-entry", 0)))
        val both = listOf(home, inbox)
        val original = mutableStateOf(NavigationProjection(1, "inbox", true, both, "", emptyList()))
        val restored = mutableStateOf(NavigationProjection(3, "home", true, listOf(home), "", emptyList()))
        var initialTarget = original
        val restoration = StateRestorationTester(compose)
        restoration.setContent {
            val target by remember { initialTarget }
            AppNavNavigationShell(target, report = {}, unavailableContent = { entry ->
                var edited by rememberSaveable { mutableStateOf(false) }
                TextButton(onClick = { edited = true }) {
                    Text("${entry.entryId} ${if (edited) "edited" else "fresh"}")
                }
            })
        }

        compose.onNodeWithText("inbox-entry fresh").performClick()
        compose.onNodeWithText("inbox-entry edited").assertExists()
        compose.runOnIdle { original.value = original.value.copy(revision = 2, selectedBranch = "home") }
        compose.onNodeWithText("home-entry fresh").performClick()
        compose.onNodeWithText("home-entry edited").assertExists()

        // Only the recreated shell sees the projection without Inbox.
        compose.runOnIdle { initialTarget = restored }
        restoration.emulateSavedInstanceStateRestore()
        compose.onNodeWithText("home-entry edited").assertExists()
        compose.runOnIdle {
            restored.value = restored.value.copy(revision = 4, selectedBranch = "inbox", branches = both)
        }
        compose.onNodeWithText("inbox-entry fresh").assertExists()
    }
}

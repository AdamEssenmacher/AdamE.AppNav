package com.adamessenmacher.appnav.compose

import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.assertIsEnabled
import androidx.activity.ComponentActivity
import androidx.compose.ui.test.junit4.v2.createAndroidComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.window.Dialog
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowDialog

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [36])
class NavigationModalHostTests {
    @get:Rule val compose = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun defaultHostRendersContentAndDismissesNatively() {
        val observations = mutableListOf<NavigationObservation>()
        compose.setContent {
            AppNavNavigationShell(target(), observations::add, unavailableContent = { Text(it.entryId) })
        }

        compose.onNodeWithText("first-entry").assertExists()
        compose.onNodeWithText("Dismiss").assertIsEnabled().performClick()
        compose.onNodeWithText("first-entry").assertDoesNotExist()
        compose.runOnIdle { assertEquals("", observations.last().modalId) }
    }

    @Test
    fun customHostRendersMountedContentAndUsesManagedBackPolicy() {
        val observations = mutableListOf<NavigationObservation>()
        var requested = 0
        compose.setContent {
            AppNavNavigationShell(
                target(),
                observations::add,
                modalHost = { presentation, content ->
                    Dialog(onDismissRequest = presentation.onDismissRequest) {
                        TextButton(onClick = presentation.onDismissRequest, enabled = presentation.canDismiss) {
                            Text("Close ${presentation.id} (${presentation.entryCount})")
                        }
                        content()
                    }
                },
                requestBack = { requested++ },
                unavailableContent = { Text(it.entryId) },
            )
        }

        compose.onNodeWithText("Dismiss").assertDoesNotExist()
        compose.onNodeWithText("first-entry").assertExists()
        compose.onNodeWithText("Close first (1)").assertIsEnabled()
        compose.runOnIdle {
            @Suppress("DEPRECATION")
            ShadowDialog.getLatestDialog().onBackPressed()
        }
        compose.runOnIdle {
            assertEquals(1, requested)
            assertEquals(listOf("first-entry"), observations.last().modalEntries)
        }
        compose.onNodeWithText("first-entry").assertExists()
    }

    @Test
    fun retiredOccurrenceCannotDismissItsReplacement() {
        val current = mutableStateOf(target())
        var oldDismiss: (() -> Unit)? = null
        var requested = 0
        compose.setContent {
            AppNavNavigationShell(
                current.value,
                report = {},
                modalHost = { presentation, content ->
                    if (presentation.id == "first") oldDismiss = presentation.onDismissRequest
                    content()
                },
                requestBack = { requested++ },
                unavailableContent = { Text(it.entryId) },
            )
        }

        compose.onNodeWithText("first-entry").assertExists()
        compose.runOnIdle { current.value = target("second", revision = 2) }
        compose.onNodeWithText("second-entry").assertExists()
        compose.runOnIdle { checkNotNull(oldDismiss).invoke() }
        compose.runOnIdle { assertEquals(0, requested) }
    }

    @Test
    fun inlineHostRoutesSystemBackThroughManagedPolicy() {
        val observations = mutableListOf<NavigationObservation>()
        var requested = 0
        compose.setContent {
            AppNavNavigationShell(
                target(),
                observations::add,
                modalHost = { _, content -> content() },
                requestBack = { requested++ },
                unavailableContent = { Text(it.entryId) },
            )
        }

        compose.onNodeWithText("first-entry").assertExists()
        compose.runOnIdle { compose.activity.onBackPressedDispatcher.onBackPressed() }
        compose.runOnIdle {
            assertEquals(1, requested)
            assertEquals(listOf("first-entry"), observations.last().modalEntries)
        }
        compose.onNodeWithText("first-entry").assertExists()
        assertEquals(false, compose.activity.isFinishing)
    }

    @Test
    fun inlineHostBackDismissesModalWithoutPoppingPrimary() {
        val observations = mutableListOf<NavigationObservation>()
        compose.setContent {
            AppNavNavigationShell(
                target(primary = listOf("home-entry", "detail-entry")),
                observations::add,
                modalHost = { _, content -> content() },
                unavailableContent = { Text(it.entryId) },
            )
        }

        compose.onNodeWithText("first-entry").assertExists()
        compose.runOnIdle { compose.activity.onBackPressedDispatcher.onBackPressed() }
        compose.onNodeWithText("first-entry").assertDoesNotExist()
        compose.runOnIdle {
            assertEquals("", observations.last().modalId)
            assertEquals(listOf("home-entry", "detail-entry"), observations.last().entries)
        }
    }

    private fun target(
        modalId: String = "first",
        revision: Long = 1L,
        primary: List<String> = listOf("home-entry"),
    ) = NavigationProjection(
        revision,
        "home",
        false,
        listOf(NavigationBranch("home", "Home", primary.mapIndexed { i, id -> MountedEntry(id, i.toLong()) })),
        modalId,
        listOf(MountedEntry("$modalId-entry", 0)),
    )
}

package com.adamessenmacher.appnav.probe

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.adamessenmacher.appnav.compose.AppNavNavigationShell
import com.adamessenmacher.appnav.compose.MountedEntry
import com.adamessenmacher.appnav.compose.NavigationBranch
import com.adamessenmacher.appnav.compose.NavigationPresentationTransition
import com.adamessenmacher.appnav.compose.NavigationProjection
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.flow.first

private val backRequestIds = AtomicLong()

@Composable
public fun DemoShellScreen(model: DemoShellModel) {
    val target by model.target.collectAsState()
    val status by model.status.collectAsState()
    val projection = remember(target) { target.toProjection() }
    val actions = remember(model) { GameActions(model::runCommand, model::recordTrace) }
    val animations = remember(actions) { gameAnimations(actions) }
    var managedBack by rememberSaveable { mutableStateOf(false) }
    MaterialTheme {
        CompositionLocalProvider(LocalGameActions provides actions) {
            AppNavNavigationShell(
                projection,
                report = { observed ->
                    model.report(NativeObservation(
                        observed.revision, observed.branch, observed.entries, observed.modalId, observed.modalEntries,
                    ))
                },
                requestBack = if (managedBack) {
                    {
                        val request = backRequestIds.incrementAndGet()
                        model.requestBack(request)
                        model.completedBack.first { it == request }
                    }
                } else null,
                animations = animations,
            ) {
                Column(Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 12.dp)) {
                    Text("AppNav Compose probe", style = MaterialTheme.typography.titleLarge)
                    Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                        TextButton(onClick = { model.runCommand("deep-link") }) { Text("Deep link") }
                        TextButton(onClick = { model.runCommand("recreate-link") }) { Text("Recreate + link") }
                        TextButton(onClick = { model.runCommand("open-modal") }) { Text("Modal") }
                    }
                    Row {
                        TextButton(onClick = {
                            managedBack = !managedBack
                            if (managedBack) model.runCommand("deny-next-back")
                        }) {
                            Text(if (managedBack) "Managed Back: on" else "Managed Back: off")
                        }
                        if (managedBack) {
                            TextButton(onClick = { model.runCommand("step-policy-back") }) {
                                Text("Step Back test")
                            }
                        }
                    }
                    Text(status, style = MaterialTheme.typography.labelSmall)
                }
            }
        }
    }
}

@Composable
public fun PageScreen(model: PageModel) {
    val title by model.title.collectAsState()
    var localCount by rememberSaveable { mutableIntStateOf(0) }
    val sceneHeight = when (title) {
        "home-detail" -> 0.70f
        "game" -> 0.95f
        else -> 1f
    }
    Box(Modifier.fillMaxWidth().fillMaxHeight(sceneHeight), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text("Page: $title", style = MaterialTheme.typography.headlineMedium)
            when (title) {
                "home-detail" -> GameControls()
                "game" -> GameCover(expanded = true)
            }
            TextButton(onClick = { localCount++ }) { Text("Local count $localCount") }
        }
    }
}

private fun ShellTarget.toProjection() = NavigationProjection(
    revision,
    selectedBranch,
    isBranchHost,
    branches.map { branch -> NavigationBranch(branch.branch, branch.title, branch.entries.map(ScreenEntry::toMountedEntry)) },
    modalId,
    modalEntries.map(ScreenEntry::toMountedEntry),
    NavigationPresentationTransition(
        transition.operation,
        transition.sourceEntryId,
        transition.destinationEntryId,
        transition.sourceKey,
        transition.sourceScreen,
        transition.destinationScreen,
    ),
)

private fun ScreenEntry.toMountedEntry() = MountedEntry(entryId, mount)

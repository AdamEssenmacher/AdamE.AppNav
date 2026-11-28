package com.adamessenmacher.appnav.probe

import android.util.Log
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
import androidx.compose.runtime.rememberCoroutineScope
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
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.launch
import kotlinx.coroutines.isActive
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withTimeout

private val backRequestIds = AtomicLong()

@Composable
public fun DemoShellScreen(model: DemoShellModel) {
    val target by model.target.collectAsState()
    val status by model.status.collectAsState()
    val projection = remember(target) { target.toProjection() }
    val actions = remember(model) { GameActions(model::runCommand, model::recordTrace) }
    val animations = remember(actions) { gameAnimations(actions) }
    var managedBack by rememberSaveable { mutableStateOf(false) }
    var throwOnBack by remember { mutableStateOf(false) }
    var shortBackTimeout by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val requestManagedBack: suspend () -> Unit = {
        if (throwOnBack) {
            throwOnBack = false
            error("Probe Back callback failed")
        }
        val completionTimeoutMs = if (shortBackTimeout) 2_000L else 30_000L
        shortBackTimeout = false
        val request = backRequestIds.incrementAndGet()
        model.requestBack(request)
        try {
            withTimeout(completionTimeoutMs) {
                combine(model.completedBack, model.rejectedBack) { completed, rejected ->
                    completed == request || rejected == request
                }.first { it }
            }
        } catch (canceled: CancellationException) {
            if (!currentCoroutineContext().isActive) {
                Log.i("AppNavProbe", "policy back waiter canceled id=$request")
            }
            throw canceled
        }
        if (model.rejectedBack.value == request) {
            model.recordTrace("policy back rejection observed id=$request")
        }
    }
    MaterialTheme {
        CompositionLocalProvider(LocalGameActions provides actions) {
            AppNavNavigationShell(
                projection,
                report = { observed ->
                    model.report(NativeObservation(
                        observed.revision, observed.branch, observed.entries, observed.modalId, observed.modalEntries,
                        // The Compose shell presents no splits, so it reports no split columns.
                        emptyList(), "",
                    ))
                },
                requestBack = if (managedBack) requestManagedBack else null,
                animations = animations,
                onBackFailure = { failure ->
                    val reason = if (failure is TimeoutCancellationException) "timeout" else failure.message
                    Log.i("AppNavProbe", "policy back failed: $reason")
                },
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
                    if (managedBack) {
                        Row {
                            TextButton(onClick = { throwOnBack = true }) { Text("Throw on next Back") }
                            TextButton(onClick = {
                                shortBackTimeout = true
                                model.runCommand("drop-next-back-completion")
                            }) { Text("Drop Back completion") }
                        }
                        Row {
                            TextButton(onClick = { shortBackTimeout = true }) { Text("Time out next Back") }
                            TextButton(onClick = { model.runCommand("recreate-activity") }) {
                                Text("Recreate Activity")
                            }
                        }
                    }
                    if (managedBack) {
                        // Exercise the bridge even before the replacement has a navigable projection.
                        TextButton(onClick = { scope.launch { requestManagedBack() } }) {
                            Text("Request managed Back")
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

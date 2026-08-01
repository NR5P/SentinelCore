package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.DeviceCandidate
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.EmptyState
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// Listens to the MQTT broker for devices that announce themselves (Home Assistant discovery) and
// for topics that look like a switch, then offers what it found. Nothing is added until the user
// picks it - the same scan/poll/pick shape as the camera network scan.
@Composable
fun DeviceDiscoveryScreen(
    model: SentinelViewModel,
    onBack: () -> Unit,
    onReview: (DeviceCandidate) -> Unit,
) {
    var isScanning by remember { mutableStateOf(false) }
    var isComplete by remember { mutableStateOf(false) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val results = remember { mutableStateListOf<DeviceCandidate>() }
    val addedIds = remember { mutableStateListOf<String>() }
    var addingId by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    suspend fun scan() {
        isScanning = true
        isComplete = false
        errorMessage = null
        results.clear()
        try {
            val start = model.startDeviceScan()
            // The server reports its own scan window, so a longer one configured in Settings is
            // waited out rather than timed out early here.
            val deadline = System.currentTimeMillis() + (start.scanSeconds + 15) * 1000L
            while (System.currentTimeMillis() < deadline) {
                delay(1_000)
                val snapshot = model.deviceScanStatus(start.scanId)
                if (snapshot.status == "completed") {
                    results.addAll(snapshot.results)
                    isComplete = true
                    return
                }
            }
            errorMessage = "The scan did not finish in time."
        } catch (error: Exception) {
            errorMessage = error.message ?: "Could not scan for devices."
        } finally {
            isScanning = false
        }
    }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Discover Devices", onBack)

        if (isScanning) {
            LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
        }
        errorMessage?.let { Text(it, color = SentinelRed, modifier = Modifier.padding(16.dp)) }

        if (!isScanning && !isComplete) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(
                    "Devices that announce themselves for Home Assistant show up immediately. Others " +
                        "are found by listening for topics that look like a switch, so a device only " +
                        "appears if it says something while the scan runs.",
                    style = MaterialTheme.typography.bodySmall,
                    color = SentinelTextDim,
                )
                Button(onClick = { scope.launch { scan() } }, modifier = Modifier.fillMaxWidth()) {
                    Text("Scan the broker")
                }
            }
        }

        if (isComplete && results.isEmpty()) {
            EmptyState(
                "Nothing found",
                "Try again, or add the device by hand if you know its topics.",
                Modifier.fillMaxSize(),
            )
        }

        if (results.isNotEmpty()) {
            LazyColumn(
                contentPadding = PaddingValues(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
                modifier = Modifier.weight(1f),
            ) {
                items(results, key = { it.commandTopic.ifBlank { it.stateTopic } }) { candidate ->
                    val key = candidate.commandTopic.ifBlank { candidate.stateTopic }
                    val isAdded = candidate.alreadyAdded || addedIds.contains(key)

                    Card(Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text(candidate.suggestedName, style = MaterialTheme.typography.titleSmall)
                            Text(
                                "${candidate.kind} · ${candidate.schema} payloads · found by ${candidate.source}",
                                style = MaterialTheme.typography.bodySmall,
                                color = SentinelTextDim,
                            )
                            Text(
                                key,
                                style = MaterialTheme.typography.bodySmall,
                                color = SentinelTextDim,
                            )
                            Row(
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                if (isAdded) {
                                    Text("Added", color = SentinelGreen)
                                } else {
                                    Button(
                                        onClick = {
                                            scope.launch {
                                                addingId = key
                                                try {
                                                    model.addDiscoveredDevice(candidate)
                                                    addedIds.add(key)
                                                } catch (error: Exception) {
                                                    // Usually a field the scan couldn't infer -
                                                    // send the user to the pre-filled form where
                                                    // they can see and fix it.
                                                    errorMessage = error.message ?: "Could not add this device."
                                                    onReview(candidate)
                                                } finally {
                                                    addingId = null
                                                }
                                            }
                                        },
                                        enabled = addingId != key,
                                    ) {
                                        Text(if (addingId == key) "Adding…" else "Add")
                                    }
                                    TextButton(onClick = { onReview(candidate) }) { Text("Review") }
                                }
                            }
                        }
                    }
                }
            }

            TextButton(
                onClick = { scope.launch { scan() } },
                modifier = Modifier.padding(16.dp),
            ) {
                Text("Scan again")
            }
        }
    }
}

package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.HomeKitSettings
import com.sentinelcore.android.data.HomeKitStatus
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch
import kotlin.math.roundToInt
import com.sentinelcore.android.ui.components.SaveFeedbackHost
import com.sentinelcore.android.ui.components.rememberSaveFeedback
import com.sentinelcore.android.ui.components.SaveButton

// Apple Home integration - the web dashboard's APPLE HOMEKIT section. Two halves that look alike
// but aren't: the settings are ordinary config saved to appsettings, while the status and pairing
// code come live from the sidecar and are why this section can't be schema-driven like the other
// 36.
//
// Pairing itself happens on an Apple device, not here - but the sidecar, the confidence threshold
// and the reset are all server-side settings, and there's no reason an Android owner of the same
// server shouldn't be able to reach them. The code is shown as text for the same reason.
@Composable
fun HomeKitAdminScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()

    var status by remember { mutableStateOf<HomeKitStatus?>(null) }
    var settings by remember { mutableStateOf<HomeKitSettings?>(null) }
    var isSaving by remember { mutableStateOf(false) }
    var confirmingReset by remember { mutableStateOf(false) }
    var statusMessage by remember { mutableStateOf<String?>(null) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    val feedback = rememberSaveFeedback()

    suspend fun refreshStatus() {
        // Allowed to fail on its own - the sidecar is optional, and a settings form that refused
        // to appear because it isn't installed would be the wrong trade.
        status = runCatching { model.homeKitStatus() }.getOrNull()
    }

    LaunchedEffect(Unit) {
        refreshStatus()
        runCatching { model.homeKitSettings() }
            .onSuccess { settings = it }
            .onFailure { errorMessage = it.message ?: "Could not load HomeKit settings." }
    }

    SaveFeedbackHost(feedback) {
        ScreenHeader("Apple HomeKit", state)
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(10.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            item {
                TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
                    Text("Back to Settings", modifier = Modifier.weight(1f).padding(start = 8.dp))
                }
            }

            item {
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        val current = status
                        Text(
                            when {
                                current == null -> "Checking HomeKit…"
                                !current.running -> "Sidecar not running"
                                current.paired -> "Paired"
                                current.published -> "Advertising, not yet paired"
                                else -> "Running, not advertising"
                            },
                            style = MaterialTheme.typography.titleSmall,
                            color = SentinelGreen,
                        )
                        if (current != null && current.running && current.accessories.isNotEmpty()) {
                            Text(
                                "${current.accessories.size} accessories",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        current?.error?.let { Text(it, color = SentinelRed, style = MaterialTheme.typography.bodySmall) }
                        Text(
                            "Puts your cameras in Apple's Home app as motion sensors, so alert-worthy events " +
                                "become native iPhone notifications. Turn on live video in the dashboard's HomeKit " +
                                "settings to watch the cameras in Home too. Nothing is put on your network " +
                                "until this is turned on.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            // Only worth showing when there's something to pair with: an unpublished sidecar has no
            // code, and a paired one doesn't need showing again.
            val pairingCode = status?.pairingCode
            if (pairingCode != null && status?.paired != true) {
                item {
                    Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                        Column(
                            Modifier.fillMaxWidth().padding(14.dp),
                            verticalArrangement = Arrangement.spacedBy(6.dp),
                            horizontalAlignment = Alignment.CenterHorizontally,
                        ) {
                            Text("Pairing Code", style = MaterialTheme.typography.titleSmall, color = SentinelGreen)
                            Text(
                                pairingCode,
                                style = MaterialTheme.typography.headlineSmall,
                                fontFamily = FontFamily.Monospace,
                            )
                            Text(
                                "On an iPhone or iPad, open the Home app, choose Add Accessory, then " +
                                    "\"More options…\", and enter this code.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                textAlign = TextAlign.Center,
                            )
                        }
                    }
                }
            }

            settings?.let { current ->
                item {
                    Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text("Enabled", Modifier.weight(1f))
                                Switch(
                                    checked = current.enabled,
                                    onCheckedChange = { settings = current.copy(enabled = it) },
                                )
                            }
                            OutlinedTextField(
                                value = current.sidecarBaseUrl,
                                onValueChange = { settings = current.copy(sidecarBaseUrl = it) },
                                label = { Text("Sidecar address") },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            Text("Timeout: ${current.timeoutSeconds}s", style = MaterialTheme.typography.bodySmall)
                            Slider(
                                value = current.timeoutSeconds.toFloat(),
                                onValueChange = { settings = current.copy(timeoutSeconds = it.roundToInt()) },
                                valueRange = 1f..120f,
                            )
                            Text(
                                "Minimum confidence: ${"%.2f".format(current.minimumConfidence)}",
                                style = MaterialTheme.typography.bodySmall,
                            )
                            Slider(
                                value = current.minimumConfidence.toFloat(),
                                onValueChange = { settings = current.copy(minimumConfidence = it.toDouble()) },
                                valueRange = 0f..1f,
                                steps = 19,
                            )
                            Text(
                                "How sure the AI must be before an event is passed to Apple Home.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }

                item {
                    SaveButton(
                        feedback = feedback,
                        isSaving = isSaving,
                        onClick = {
                            scope.launch {
                                isSaving = true
                                statusMessage = null
                                errorMessage = null
                                try {
                                    model.saveHomeKitSettings(current)
                                    feedback.saved()
                                    refreshStatus()
                                } catch (error: Exception) {
                                    errorMessage = error.message ?: "Save failed."
                                    feedback.failed("Not saved")
                                } finally {
                                    isSaving = false
                                }
                            }
                        },
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                    )
                }

                item {
                    OutlinedButton(
                        onClick = { confirmingReset = true },
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                    ) { Text("Reset Pairing", color = SentinelRed) }
                }

                item {
                    Text(
                        "Resetting drops the pairing and restarts the sidecar, so every Home app on the " +
                            "network has to pair again with a new code.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                    )
                }
            }

            statusMessage?.let { message ->
                item { Text(message, color = SentinelGreen, modifier = Modifier.padding(horizontal = 14.dp)) }
            }
            errorMessage?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }
        }
    }

    if (confirmingReset) {
        AlertDialog(
            onDismissRequest = { confirmingReset = false },
            title = { Text("Reset HomeKit pairing?") },
            text = { Text("Every Home app paired with this server has to pair again with a new code.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmingReset = false
                    scope.launch {
                        statusMessage = null
                        errorMessage = null
                        try {
                            model.resetHomeKitPairing()
                            statusMessage = "Pairing reset — the sidecar is restarting."
                            feedback.note("Pairing reset")
                            refreshStatus()
                        } catch (error: Exception) {
                            errorMessage = error.message ?: "Reset failed."
                        }
                    }
                }) { Text("Reset Pairing", color = SentinelRed) }
            },
            dismissButton = { TextButton(onClick = { confirmingReset = false }) { Text("Cancel") } },
        )
    }
}

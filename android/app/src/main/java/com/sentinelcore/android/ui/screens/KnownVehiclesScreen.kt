package com.sentinelcore.android.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.KnownVehicle
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch

// Number plates the AI should recognise as household rather than a stranger's - the web
// dashboard's KNOWN VEHICLES section, which PACKAGE THEFT WATCH also consults before deciding a
// pickup was unrecognised.
//
// The save endpoint replaces the whole list rather than patching it, so this edits a local copy
// and sends all of it at once. That also makes Save a deliberate step, so a mis-tapped delete
// doesn't reach the server on its own.
@Composable
fun KnownVehiclesScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()

    val vehicles = remember { mutableStateListOf<KnownVehicle>() }
    var loaded by remember { mutableStateOf(false) }
    var newPlate by remember { mutableStateOf("") }
    var newLabel by remember { mutableStateOf("") }
    var isSaving by remember { mutableStateOf(false) }
    var statusMessage by remember { mutableStateOf<String?>(null) }
    var errorMessage by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(Unit) {
        runCatching { model.knownVehicles() }
            .onSuccess {
                vehicles.clear()
                vehicles.addAll(it)
                loaded = true
            }
            .onFailure { errorMessage = it.message ?: "Could not load known vehicles." }
    }

    val trimmedPlate = newPlate.trim().uppercase()
    val canAdd = trimmedPlate.isNotEmpty() &&
        newLabel.isNotBlank() &&
        vehicles.none { it.plate.uppercase() == trimmedPlate }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Known Vehicles", state)
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
                Text(
                    "Plates the AI treats as household rather than a stranger's. Matching ignores spaces " +
                        "and punctuation, so \"ABC 123\" and \"ABC-123\" are the same plate.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                )
            }

            if (loaded && vehicles.isEmpty()) {
                item {
                    Text(
                        "No known vehicles yet.",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(14.dp),
                    )
                }
            }

            items(vehicles, key = { it.plate }) { vehicle ->
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Row(
                        Modifier.fillMaxWidth().padding(14.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(
                                vehicle.plate,
                                style = MaterialTheme.typography.bodyLarge,
                                fontFamily = FontFamily.Monospace,
                            )
                            Text(
                                vehicle.label,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        IconButton(onClick = {
                            vehicles.remove(vehicle)
                            statusMessage = null
                        }) {
                            Icon(Icons.Filled.Delete, contentDescription = "Remove", tint = SentinelRed)
                        }
                    }
                }
            }

            item {
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text("Add a Vehicle", style = MaterialTheme.typography.titleSmall, color = SentinelGreen)
                        OutlinedTextField(
                            value = newPlate,
                            onValueChange = { newPlate = it },
                            label = { Text("Plate") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = newLabel,
                            onValueChange = { newLabel = it },
                            label = { Text("Label, e.g. Dad's truck") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedButton(
                            onClick = {
                                vehicles.add(KnownVehicle(trimmedPlate, newLabel.trim()))
                                newPlate = ""
                                newLabel = ""
                                statusMessage = null
                            },
                            enabled = canAdd,
                            modifier = Modifier.fillMaxWidth(),
                        ) { Text("Add") }
                    }
                }
            }

            item {
                Button(
                    onClick = {
                        scope.launch {
                            isSaving = true
                            statusMessage = null
                            errorMessage = null
                            try {
                                model.saveKnownVehicles(vehicles.toList())
                                statusMessage = "Saved."
                            } catch (error: Exception) {
                                errorMessage = error.message ?: "Save failed."
                            } finally {
                                isSaving = false
                            }
                        }
                    },
                    enabled = !isSaving && loaded,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                ) { Text(if (isSaving) "Saving…" else "Save") }
            }

            item {
                Text(
                    "Changes here aren't sent to the server until you save.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                )
            }

            statusMessage?.let { message ->
                item { Text(message, color = SentinelGreen, modifier = Modifier.padding(horizontal = 14.dp)) }
            }
            errorMessage?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }
        }
    }
}

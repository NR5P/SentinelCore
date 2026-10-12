package com.sentinelcore.android.ui.screens

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
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
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import kotlinx.coroutines.launch
import java.nio.charset.StandardCharsets
import java.time.LocalDate

// Exporting the whole configuration to one file and restoring from one - the web dashboard's
// BACKUP section. Worth doing before any big change, and once things first work the way you like.
//
// The backup passes through this app as raw text, never parsed: it is whatever the server
// considers its whole configuration, and a client that decoded it would quietly drop any section
// added after this app was built.
@Composable
fun BackupAdminScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val context = LocalContext.current

    var isExporting by remember { mutableStateOf(false) }
    var isRestoring by remember { mutableStateOf(false) }
    var pendingRestore by remember { mutableStateOf<String?>(null) }
    var statusMessage by remember { mutableStateOf<String?>(null) }
    var errorMessage by remember { mutableStateOf<String?>(null) }

    // Written through the system's own save dialog rather than somewhere this app chose: a backup
    // the owner can't find again is no backup at all.
    val saver = rememberLauncherForActivityResult(
        ActivityResultContracts.CreateDocument("application/json"),
    ) { uri: Uri? ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            isExporting = true
            statusMessage = null
            errorMessage = null
            try {
                val contents = model.exportBackup()
                context.contentResolver.openOutputStream(uri)?.use {
                    it.write(contents.toByteArray(StandardCharsets.UTF_8))
                }
                statusMessage = "Backup saved."
            } catch (error: Exception) {
                errorMessage = error.message ?: "Export failed."
            } finally {
                isExporting = false
            }
        }
    }

    // Reads the file and holds it for confirmation rather than restoring on pick - choosing a file
    // in a system dialog isn't consent to overwrite a working configuration.
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
        if (uri == null) return@rememberLauncherForActivityResult
        statusMessage = null
        errorMessage = null
        try {
            val contents = context.contentResolver.openInputStream(uri)?.use {
                it.readBytes().toString(StandardCharsets.UTF_8)
            }
            if (contents.isNullOrBlank()) {
                errorMessage = "That file is empty or could not be read."
            } else {
                pendingRestore = contents
            }
        } catch (error: Exception) {
            errorMessage = error.message ?: "Could not read that file."
        }
    }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Backup", state)
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
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text("Export", style = MaterialTheme.typography.titleSmall, color = SentinelGreen)
                        Text(
                            "Saves the server's configuration into one file you can restore later or move to another " +
                                "machine: settings, cameras, camera groups, trigger zones, recording schedules, gate checks, " +
                                "user accounts, Device Hub devices and automations, the alarm, Organizer notes, events, " +
                                "reminders, boards and board automations, Files backup drives, movie, music and podcast " +
                                "libraries with everyone's watch and listening history, and wake-up alarms. Attachments, face " +
                                "photos and the licence are only in the full backup made on the dashboard.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        Button(
                            onClick = { saver.launch("sentinelcore-backup-${LocalDate.now()}.json") },
                            enabled = !isExporting,
                            modifier = Modifier.fillMaxWidth(),
                        ) { Text(if (isExporting) "Exporting…" else "Export Backup…") }
                    }
                }
            }

            item {
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text("Restore", style = MaterialTheme.typography.titleSmall, color = SentinelGreen)
                        Text(
                            "Nothing in the current setup that isn't mentioned in the backup is touched or " +
                                "removed.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        OutlinedButton(
                            onClick = { picker.launch(arrayOf("application/json", "*/*")) },
                            enabled = !isRestoring,
                            modifier = Modifier.fillMaxWidth(),
                        ) { Text(if (isRestoring) "Restoring…" else "Restore From File…") }
                    }
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

    pendingRestore?.let { contents ->
        AlertDialog(
            onDismissRequest = { pendingRestore = null },
            title = { Text("Restore this backup?") },
            text = {
                Text(
                    "Settings, cameras, trigger zones, and accounts in the file replace what's on the " +
                        "server now.",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    pendingRestore = null
                    scope.launch {
                        isRestoring = true
                        statusMessage = null
                        errorMessage = null
                        try {
                            val restored = model.importBackup(contents)
                            val summary = if (restored.counts.isEmpty()) {
                                "Restored settings."
                            } else {
                                "Restored " + restored.counts.joinToString(", ") { (count, noun) ->
                                    "$count $noun${if (count == 1) "" else "s"}"
                                } + "."
                            }
                            statusMessage = (listOf(summary) + restored.notes).joinToString(" ")
                        } catch (error: Exception) {
                            errorMessage = error.message ?: "Restore failed."
                        } finally {
                            isRestoring = false
                        }
                    }
                }) { Text("Restore", color = SentinelRed) }
            },
            dismissButton = { TextButton(onClick = { pendingRestore = null }) { Text("Cancel") } },
        )
    }
}

package com.sentinelcore.android.ui.files

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.FamilyDrive
import com.sentinelcore.android.data.FamilyFilesStatus
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.SaveFeedbackHost
import com.sentinelcore.android.ui.components.StatusChip
import com.sentinelcore.android.ui.components.rememberSaveFeedback
import com.sentinelcore.android.ui.screens.ScreenHeader
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.LocalTime

// The library's backup drives - a folder on the server's machine (a USB disk, say) or another
// computer over SFTP - plus the nightly pass and the duplicate check. An Admin runs and edits
// everything here; anyone else sees the drives and how their last backup went.
@Composable
fun FilesDrivesScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
    onEditDrive: (String?) -> Unit,
    onEditLocation: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val feedback = rememberSaveFeedback()

    var status by remember { mutableStateOf<FamilyFilesStatus?>(null) }
    var errorMessage by remember { mutableStateOf<String?>(null) }
    var reloadTick by remember { mutableStateOf(0) }
    var isStarting by remember { mutableStateOf(false) }
    var confirmRestore by remember { mutableStateOf<FamilyDrive?>(null) }
    var confirmRemoveDuplicates by remember { mutableStateOf(false) }

    // Polls while a job runs so the drive cards and the banner keep up with it.
    LaunchedEffect(reloadTick) {
        while (true) {
            val next = runCatching { model.familyFilesStatus() }
                .onFailure { errorMessage = it.message ?: "Could not load the drives." }
                .getOrNull()
            if (next != null) {
                status = next
                errorMessage = null
            }
            if (next?.job == null) break
            delay(2_000)
        }
    }

    // Starting a job: the server answers 202 straight away (or 409 while another runs), and the
    // poll above picks it up.
    fun start(failure: String, block: suspend () -> String) {
        scope.launch {
            isStarting = true
            runCatching { block() }
                .onSuccess {
                    feedback.note(it.ifBlank { "Started." })
                    reloadTick++
                }
                .onFailure { feedback.failed(it.message ?: failure) }
            isStarting = false
        }
    }

    val isAdmin = state.isAdmin && (status?.isAdmin ?: true)
    val busy = status?.job != null || isStarting

    SaveFeedbackHost(feedback) {
        ScreenHeader("Backup Drives", state, onRefresh = { reloadTick++ })
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item { FilesBackRow("Back to Files", onBack) }

            errorMessage?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }

            status?.job?.let { job ->
                item { FamilyJobBanner(job, Modifier.padding(horizontal = 14.dp)) }
            }

            val current = status
            if (current == null && errorMessage == null) {
                item {
                    Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.width(10.dp))
                        Text("Loading…", color = SentinelTextDim)
                    }
                }
            }
            if (current != null) {
                current.libraryProblem?.let { problem ->
                    item { FamilyLibraryProblemBanner(problem, Modifier.padding(horizontal = 14.dp)) }
                }
                current.library?.let { library ->
                    item {
                        Text(
                            "${library.files} files, ${formatFileSize(library.bytes)} in the library",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                        )
                    }
                }

                // Where the library's files live: a folder on the server or a shared folder on
                // another computer. Only an Admin can move it.
                item {
                    Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            Text("Library location", style = MaterialTheme.typography.titleSmall, color = SentinelGreen)
                            Text(
                                if (current.libraryOnNetwork) "A shared folder on another computer, reached over the network."
                                else "A folder on this server.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                            current.libraryFolder?.takeIf { it.isNotBlank() }?.let {
                                Text(it, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace)
                            }
                            if (isAdmin) {
                                OutlinedButton(onClick = onEditLocation, enabled = !busy, modifier = Modifier.fillMaxWidth()) {
                                    Text("Change location")
                                }
                            }
                        }
                    }
                }

                if (current.drives.isEmpty()) {
                    item {
                        Text(
                            if (isAdmin) "No backup drives yet. Add a folder on the server's machine or another computer over SFTP."
                            else "No backup drives have been set up.",
                            color = SentinelTextDim,
                            modifier = Modifier.padding(horizontal = 14.dp),
                        )
                    }
                }
                items(current.drives, key = { it.id }) { drive ->
                    DriveCard(
                        drive = drive,
                        isAdmin = isAdmin,
                        busy = busy,
                        onSync = { start("Could not start the backup.") { model.syncFamilyDrive(drive.id) } },
                        onRestore = { confirmRestore = drive },
                        onEdit = { onEditDrive(drive.id) },
                    )
                }

                if (isAdmin) {
                    item {
                        Row(
                            Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            OutlinedButton(onClick = { onEditDrive(null) }, modifier = Modifier.weight(1f)) { Text("Add drive") }
                            Button(
                                onClick = { start("Could not start the backup.") { model.syncAllFamilyDrives() } },
                                enabled = !busy && current.drives.any { it.enabled },
                                modifier = Modifier.weight(1f),
                            ) { Text("Back up to all drives now") }
                        }
                    }

                    current.nightly?.let { nightly ->
                        item {
                            Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                                Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                                    Text("Nightly pass", style = MaterialTheme.typography.titleSmall, color = SentinelGreen)
                                    Text(
                                        "Every night at ${LocalTime.of(nightly.hour.coerceIn(0, 23), nightly.minute.coerceIn(0, 59))}: " +
                                            "backs up to every drive, " +
                                            (if (nightly.removeDuplicates) "removes duplicates, " else "reports duplicates, ") +
                                            "and clears trash older than ${nightly.trashKeepDays} days. Change the time under Settings.",
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                    DetailLine("Next run", formatFileDate(nightly.nextRunUtc) ?: "not scheduled")
                                    DetailLine("Last run", formatFileDate(nightly.lastRunUtc) ?: "never")
                                    if (nightly.lastResult.isNotBlank() || nightly.lastMessage.isNotBlank()) {
                                        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                            if (nightly.lastResult.isNotBlank()) {
                                                StatusChip(
                                                    nightly.lastResult.uppercase(),
                                                    when (nightly.lastResult) {
                                                        "ok" -> SentinelGreen
                                                        "incomplete" -> SentinelAmber
                                                        else -> SentinelRed
                                                    },
                                                )
                                            }
                                            Text(nightly.lastMessage, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
                                        }
                                    }
                                    OutlinedButton(
                                        onClick = { start("Could not start the nightly pass.") { model.runFamilyNightly() } },
                                        enabled = !busy,
                                        modifier = Modifier.fillMaxWidth(),
                                    ) { Text("Run the nightly pass now") }
                                }
                            }
                        }
                    }

                    item {
                        Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                            Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                                Text("Duplicates", style = MaterialTheme.typography.titleSmall, color = SentinelGreen)
                                val report = current.duplicates
                                if (report == null) {
                                    Text("Not checked yet.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                } else {
                                    Text(
                                        "Last check ${formatFileDate(report.ranUtc) ?: report.ranUtc}: ${report.filesChecked} files looked at, " +
                                            "${report.copies} extra ${if (report.copies == 1) "copy" else "copies"} in ${report.groups} " +
                                            "${if (report.groups == 1) "group" else "groups"} (${formatFileSize(report.bytes)})" +
                                            (if (report.removed) ", moved to the trash." else "."),
                                        style = MaterialTheme.typography.bodySmall,
                                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                                    )
                                }
                                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                    OutlinedButton(
                                        onClick = { start("Could not start the duplicate check.") { model.checkFamilyDuplicates(remove = false) } },
                                        enabled = !busy,
                                        modifier = Modifier.weight(1f),
                                    ) { Text("Check now") }
                                    OutlinedButton(
                                        onClick = { confirmRemoveDuplicates = true },
                                        enabled = !busy,
                                        modifier = Modifier.weight(1f),
                                    ) { Text("Check and remove") }
                                }
                            }
                        }
                    }
                }
            }
            item { Spacer(Modifier.size(20.dp)) }
        }
    }

    confirmRestore?.let { drive ->
        ConfirmDialog(
            title = "Bring files back from \"${drive.name}\"?",
            text = "Anything on the drive that the library no longer has is copied back into it. Nothing is removed from either side.",
            confirmLabel = "Bring files back",
            destructive = false,
            onConfirm = {
                confirmRestore = null
                start("Could not start the restore.") { model.restoreFamilyDrive(drive.id) }
            },
            onDismiss = { confirmRestore = null },
        )
    }
    if (confirmRemoveDuplicates) {
        ConfirmDialog(
            title = "Remove duplicates?",
            text = "Where the same file is in the library more than once, every copy but one goes to the trash. They can be put back from there.",
            confirmLabel = "Check and remove",
            onConfirm = {
                confirmRemoveDuplicates = false
                start("Could not start the duplicate check.") { model.checkFamilyDuplicates(remove = true) }
            },
            onDismiss = { confirmRemoveDuplicates = false },
        )
    }
}

@Composable
private fun DriveCard(
    drive: FamilyDrive,
    isAdmin: Boolean,
    busy: Boolean,
    onSync: () -> Unit,
    onRestore: () -> Unit,
    onEdit: () -> Unit,
) {
    Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(drive.name, style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                when {
                    !drive.enabled -> StatusChip("OFF", SentinelTextDim)
                    drive.lastResult == "ok" -> StatusChip("OK", SentinelGreen)
                    drive.lastResult == "failed" -> StatusChip("FAILED", SentinelRed)
                    // Backed up, but files that left the library were kept on the drive.
                    drive.lastResult == "held" -> StatusChip("HELD BACK", SentinelAmber)
                    drive.lastResult.isBlank() -> StatusChip("NOT YET RUN", SentinelAmber)
                    else -> StatusChip(drive.lastResult.uppercase(), SentinelAmber)
                }
            }
            Text(
                (if (drive.isSftp) "Another computer over SFTP: " else "Folder: ") + drive.location,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (drive.lastMessage.isNotBlank()) {
                Text(drive.lastMessage, style = MaterialTheme.typography.bodySmall)
            }
            DetailLine("Last backup", formatFileDate(drive.lastSuccessUtc) ?: "never")
            drive.lastAttemptUtc?.takeIf { it != drive.lastSuccessUtc }?.let { DetailLine("Last try", formatFileDate(it) ?: it) }
            drive.lastFileCount?.let { count ->
                DetailLine("On the drive", "$count files" + (drive.lastBytes?.let { ", ${formatFileSize(it)}" } ?: ""))
            }
            if (drive.lastCopied != null || drive.lastRemoved != null) {
                DetailLine("Last change", "${drive.lastCopied ?: 0} copied, ${drive.lastRemoved ?: 0} removed")
            }
            drive.lastFreeBytes?.let { DetailLine("Free space", formatFileSize(it)) }
            if (isAdmin) {
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    TextButton(onClick = onSync, enabled = !busy && drive.enabled) { Text("Back up now") }
                    TextButton(onClick = onRestore, enabled = !busy) { Text("Bring files back") }
                    Spacer(Modifier.weight(1f))
                    TextButton(onClick = onEdit) { Text("Edit") }
                }
            }
        }
    }
}

@Composable
internal fun DetailLine(label: String, value: String) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = SentinelTextDim, modifier = Modifier.width(96.dp))
        Text(value, style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
    }
}

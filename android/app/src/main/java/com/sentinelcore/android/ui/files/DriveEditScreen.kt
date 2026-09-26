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
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.FamilyDrive
import com.sentinelcore.android.data.FamilyDriveInput
import com.sentinelcore.android.data.FamilyDriveTestResult
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.SaveButton
import com.sentinelcore.android.ui.components.SaveFeedbackHost
import com.sentinelcore.android.ui.components.rememberSaveFeedback
import com.sentinelcore.android.ui.screens.ScreenHeader
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.launch

// One backup drive's settings (Admin). "Test connection" tries the form as typed before it is
// saved, and for SFTP shows the host key the server saw so it can be pinned with one tap - the
// same trust-on-first-use step the dashboard's form has.
@Composable
fun DriveEditScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    driveId: String?,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val feedback = rememberSaveFeedback()

    var loaded by remember { mutableStateOf(driveId == null) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var hasSavedPassword by remember { mutableStateOf(false) }

    var name by remember { mutableStateOf("") }
    var kind by remember { mutableStateOf("folder") }
    var enabled by remember { mutableStateOf(true) }
    var folderPath by remember { mutableStateOf("") }
    var host by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("22") }
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var sftpFolder by remember { mutableStateOf("") }
    var fingerprint by remember { mutableStateOf("") }

    var isSaving by remember { mutableStateOf(false) }
    var isTesting by remember { mutableStateOf(false) }
    var testResult by remember { mutableStateOf<FamilyDriveTestResult?>(null) }
    var confirmDelete by remember { mutableStateOf(false) }
    var errors by remember { mutableStateOf<String?>(null) }

    fun fill(drive: FamilyDrive) {
        name = drive.name
        kind = drive.kind.ifBlank { "folder" }
        enabled = drive.enabled
        folderPath = drive.folderPath.orEmpty()
        host = drive.sftpHost.orEmpty()
        port = (drive.sftpPort ?: 22).toString()
        username = drive.sftpUsername.orEmpty()
        sftpFolder = drive.sftpFolder.orEmpty()
        fingerprint = drive.sftpHostKeyFingerprint.orEmpty()
        hasSavedPassword = drive.hasPassword == true
    }

    LaunchedEffect(driveId) {
        if (driveId == null) return@LaunchedEffect
        runCatching { model.familyDrives() }
            .onSuccess { drives ->
                val drive = drives.firstOrNull { it.id == driveId }
                if (drive == null) loadError = "That drive is no longer there." else fill(drive)
                loaded = true
            }
            .onFailure { loadError = it.message ?: "Could not load that drive." }
    }

    fun input(): FamilyDriveInput = FamilyDriveInput(
        id = driveId,
        name = name.trim(),
        kind = kind,
        enabled = enabled,
        folderPath = folderPath.trim(),
        sftpHost = host.trim(),
        sftpPort = port.trim().toIntOrNull() ?: 22,
        sftpUsername = username.trim(),
        sftpPassword = password,
        sftpFolder = sftpFolder.trim(),
        sftpHostKeyFingerprint = fingerprint.trim(),
    )

    val canSend = name.isNotBlank() && if (kind == "sftp") host.isNotBlank() && username.isNotBlank() else folderPath.isNotBlank()

    SaveFeedbackHost(feedback) {
        ScreenHeader(if (driveId == null) "New Drive" else "Edit Drive", state)
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item { FilesBackRow("Back to Drives", onBack) }

            loadError?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }
            if (!loaded && loadError == null) {
                item {
                    Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.width(10.dp))
                        Text("Loading…", color = SentinelTextDim)
                    }
                }
                return@LazyColumn
            }

            item {
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        OutlinedTextField(
                            value = name,
                            onValueChange = { name = it },
                            label = { Text("Name") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        Text("Where", style = MaterialTheme.typography.labelLarge, color = SentinelGreen)
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            FilterChip(selected = kind == "folder", onClick = { kind = "folder" }, label = { Text("A folder") })
                            FilterChip(selected = kind == "sftp", onClick = { kind = "sftp" }, label = { Text("Another computer (SFTP)") })
                        }
                        if (kind == "folder") {
                            OutlinedTextField(
                                value = folderPath,
                                onValueChange = { folderPath = it },
                                label = { Text("Folder on the server's machine") },
                                placeholder = { Text("/media/backup/family-files") },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            Text(
                                "A path the server can write to: a plugged-in USB disk, a mounted network share.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        } else {
                            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                OutlinedTextField(
                                    value = host,
                                    onValueChange = { host = it },
                                    label = { Text("Host") },
                                    singleLine = true,
                                    modifier = Modifier.weight(2f),
                                )
                                OutlinedTextField(
                                    value = port,
                                    onValueChange = { port = it.filter(Char::isDigit).take(5) },
                                    label = { Text("Port") },
                                    singleLine = true,
                                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                                    modifier = Modifier.weight(1f),
                                )
                            }
                            OutlinedTextField(
                                value = username,
                                onValueChange = { username = it },
                                label = { Text("Username") },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            OutlinedTextField(
                                value = password,
                                onValueChange = { password = it },
                                label = { Text(if (hasSavedPassword) "Password (blank keeps the saved one)" else "Password") },
                                singleLine = true,
                                visualTransformation = PasswordVisualTransformation(),
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                                modifier = Modifier.fillMaxWidth(),
                            )
                            OutlinedTextField(
                                value = sftpFolder,
                                onValueChange = { sftpFolder = it },
                                label = { Text("Folder on that computer") },
                                placeholder = { Text("/backup/family-files") },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            OutlinedTextField(
                                value = fingerprint,
                                onValueChange = { fingerprint = it },
                                label = { Text("Host key fingerprint") },
                                placeholder = { Text("SHA256:…") },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            Text(
                                "The computer's SSH host key, so a backup never goes to an impostor. Leave it blank, test the " +
                                    "connection, and use the fingerprint the server saw.",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text("Enabled", style = MaterialTheme.typography.bodyLarge)
                                Text("Included in the nightly pass and in \"back up to all drives\".", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
                            }
                            Switch(checked = enabled, onCheckedChange = { enabled = it })
                        }
                    }
                }
            }

            item {
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedButton(
                            onClick = {
                                scope.launch {
                                    isTesting = true
                                    testResult = null
                                    runCatching { model.testFamilyDrive(input()) }
                                        .onSuccess { testResult = it }
                                        .onFailure { feedback.failed(it.message ?: "The test could not be run.") }
                                    isTesting = false
                                }
                            },
                            enabled = canSend && !isTesting && !isSaving,
                            modifier = Modifier.fillMaxWidth(),
                        ) { Text(if (isTesting) "Testing…" else "Test connection") }
                        testResult?.let { result ->
                            Text(result.detail, color = if (result.ok) SentinelGreen else SentinelRed, style = MaterialTheme.typography.bodyMedium)
                            val seen = result.hostKeyFingerprint
                            if (kind == "sftp" && !seen.isNullOrBlank()) {
                                Text("Host key seen: $seen", fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall)
                                if (seen != fingerprint.trim()) {
                                    TextButton(onClick = { fingerprint = seen }) { Text("Use this fingerprint", color = SentinelAmber) }
                                }
                            }
                        }
                    }
                }
            }

            errors?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }

            item {
                Row(Modifier.fillMaxWidth().padding(horizontal = 14.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (driveId != null) {
                        OutlinedButton(onClick = { confirmDelete = true }, enabled = !isSaving, modifier = Modifier.weight(1f)) {
                            Text("Delete", color = SentinelRed)
                        }
                    }
                    SaveButton(
                        feedback = feedback,
                        isSaving = isSaving,
                        enabled = canSend,
                        modifier = Modifier.weight(1f),
                        onClick = {
                            scope.launch {
                                isSaving = true
                                errors = null
                                runCatching { model.saveFamilyDrive(driveId, input()) }
                                    .onSuccess {
                                        feedback.saved()
                                        onBack()
                                    }
                                    .onFailure {
                                        errors = it.message ?: "Could not save the drive."
                                        feedback.failed("Not saved.")
                                    }
                                isSaving = false
                            }
                        },
                    )
                }
            }
            item { Spacer(Modifier.size(20.dp)) }
        }
    }

    if (confirmDelete && driveId != null) {
        ConfirmDialog(
            title = "Delete \"${name.ifBlank { "this drive" }}\"?",
            text = "The server forgets the drive. Nothing already backed up onto it is touched.",
            confirmLabel = "Delete",
            onConfirm = {
                confirmDelete = false
                scope.launch {
                    isSaving = true
                    runCatching { model.deleteFamilyDrive(driveId) }
                        .onSuccess { onBack() }
                        .onFailure { feedback.failed(it.message ?: "Could not delete the drive.") }
                    isSaving = false
                }
            },
            onDismiss = { confirmDelete = false },
        )
    }
}

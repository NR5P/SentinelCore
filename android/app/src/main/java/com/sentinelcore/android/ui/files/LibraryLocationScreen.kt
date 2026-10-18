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
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.FamilyLibraryLocationInput
import com.sentinelcore.android.data.FamilyLibraryLocationResult
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.SaveFeedbackHost
import com.sentinelcore.android.ui.components.rememberSaveFeedback
import com.sentinelcore.android.ui.screens.ScreenHeader
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.launch

// Where the library's files live (Admin): a folder on the server, or a shared folder on another
// computer (Windows or Linux Samba). "Test" signs in and writes a test file without changing
// anything; "Use this location" runs the same check and then moves the library there. Files in
// the old place are never moved.
@Composable
fun LibraryLocationScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val feedback = rememberSaveFeedback()

    var loaded by remember { mutableStateOf(false) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var current by remember { mutableStateOf("") }

    var network by remember { mutableStateOf(false) }
    var localPath by remember { mutableStateOf("") }
    var networkPath by remember { mutableStateOf("") }
    var username by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }

    var isTesting by remember { mutableStateOf(false) }
    var isSaving by remember { mutableStateOf(false) }
    var result by remember { mutableStateOf<FamilyLibraryLocationResult?>(null) }
    var confirmMove by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        runCatching { model.familyFilesStatus() }
            .onSuccess { status ->
                current = status.libraryFolder.orEmpty()
                network = status.libraryOnNetwork
                if (network) networkPath = current else localPath = current
                loaded = true
            }
            .onFailure { loadError = it.message ?: "Could not load the library's location." }
    }

    fun input() = FamilyLibraryLocationInput.from(network, if (network) networkPath else localPath, username, password)

    fun save() {
        scope.launch {
            isSaving = true
            runCatching { model.setFamilyLibraryLocation(input()) }
                .onSuccess {
                    result = it
                    if (it.ok) {
                        feedback.saved(it.detail.ifBlank { "Saved" })
                        onBack()
                    } else {
                        feedback.failed("Not changed.")
                    }
                }
                .onFailure { feedback.failed(it.message ?: "Could not change the location.") }
            isSaving = false
        }
    }

    val canSend = !network || networkPath.isNotBlank()

    SaveFeedbackHost(feedback) {
        ScreenHeader("Library Location", state)
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
                        Text("Where the files live", style = MaterialTheme.typography.labelLarge, color = SentinelGreen)
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            FilterChip(selected = !network, onClick = { network = false; result = null }, label = { Text("This server") })
                            FilterChip(selected = network, onClick = { network = true; result = null }, label = { Text("Another computer") })
                        }
                        if (!network) {
                            OutlinedTextField(
                                value = localPath,
                                onValueChange = { localPath = it },
                                label = { Text("Folder on the server") },
                                placeholder = { Text("/mnt/bigdisk/Family Files") },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            Hint("A full path as the server sees it. Blank keeps the library under the server's data folder.")
                        } else {
                            OutlinedTextField(
                                value = networkPath,
                                onValueChange = { networkPath = it },
                                label = { Text("Shared folder") },
                                placeholder = { Text("smb://192.168.1.20/Family/Files") },
                                singleLine = true,
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                                modifier = Modifier.fillMaxWidth(),
                            )
                            Hint(
                                "The computer's name or address, then the shared folder's name, then any folder inside it (made if it " +
                                    "isn't there), like smb://192.168.1.20/Family/Files or \\\\DESKTOP-PC\\Family. On Windows, share the " +
                                    "folder (Properties → Sharing) and give the account \"Change\" permission. On Linux, share it with " +
                                    "Samba (smbpasswd -a for the user) with writable = yes.",
                            )
                            OutlinedTextField(
                                value = username,
                                onValueChange = { username = it },
                                label = { Text("Username") },
                                placeholder = { Text("the account on that computer") },
                                singleLine = true,
                                modifier = Modifier.fillMaxWidth(),
                            )
                            OutlinedTextField(
                                value = password,
                                onValueChange = { password = it },
                                label = { Text("Password") },
                                placeholder = { Text("blank = the one already saved") },
                                singleLine = true,
                                visualTransformation = PasswordVisualTransformation(),
                                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                                modifier = Modifier.fillMaxWidth(),
                            )
                            Hint(
                                "The sign-in is saved for that computer and shared with Movies libraries on it. Leave both blank " +
                                    "to use a saved sign-in, or to try as a guest.",
                            )
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
                                    result = null
                                    runCatching { model.testFamilyLibraryLocation(input()) }
                                        .onSuccess { result = it }
                                        .onFailure { feedback.failed(it.message ?: "The test could not be run.") }
                                    isTesting = false
                                }
                            },
                            enabled = canSend && !isTesting && !isSaving,
                            modifier = Modifier.fillMaxWidth(),
                        ) { Text(if (isTesting) "Testing…" else "Test") }
                        result?.let {
                            Text(it.detail, color = if (it.ok) SentinelGreen else SentinelRed, style = MaterialTheme.typography.bodyMedium)
                            if (!it.ok && it.needsLogin && network) {
                                Hint("Enter a username and password for an account on that computer that can change files in the folder.")
                            }
                        }
                    }
                }
            }

            item {
                Text(
                    "Changing this doesn't move anything: files in the old place stay there. To fill the new place, copy them " +
                        "across yourself, or use \"Bring files back\" on a backup drive.",
                    style = MaterialTheme.typography.bodySmall,
                    color = SentinelTextDim,
                    modifier = Modifier.padding(horizontal = 14.dp),
                )
            }

            item {
                Button(
                    onClick = { if (input().location == current) onBack() else confirmMove = true },
                    enabled = canSend && !isTesting && !isSaving,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                ) {
                    if (isSaving) {
                        CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.width(8.dp))
                        Text("Checking…")
                    } else {
                        Text("Use this location")
                    }
                }
            }
            item { Spacer(Modifier.size(20.dp)) }
        }
    }

    if (confirmMove) {
        val target = input().location
        ConfirmDialog(
            title = "Move the library to ${target.ifBlank { "the server's data folder" }}?",
            text = if (current.isBlank()) "Files already in the old place stay there and stop showing in the library."
            else "Files already in $current stay there and stop showing in the library.",
            confirmLabel = "Use this location",
            destructive = false,
            onConfirm = {
                confirmMove = false
                save()
            },
            onDismiss = { confirmMove = false },
        )
    }
}

@Composable
private fun Hint(text: String) {
    Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

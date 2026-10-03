package com.sentinelcore.android.ui.screens

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.CreateNewFolder
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.FolderShared
import androidx.compose.material.icons.filled.Home
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
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
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.data.MediaApi
import com.sentinelcore.android.data.MediaBrowseFolder
import com.sentinelcore.android.data.MediaBrowseListing
import com.sentinelcore.android.data.MediaDiscoveredComputer
import com.sentinelcore.android.data.MediaLibraryForm
import com.sentinelcore.android.data.MediaNetworkComputer
import com.sentinelcore.android.data.MediaNetworkSignInInput
import com.sentinelcore.android.data.MediaNetworkSignInResult
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.files.ConfirmDialog
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.launch

// Who the sign-in form is for: a saved computer being edited, or a new one (host prefilled when
// a folder answered needsLogin, blank for "Add a computer"). `reason` is the error that brought
// the form up, if any.
private data class SignInTarget(
    val existing: MediaNetworkComputer?,
    val host: String,
    val reason: String? = null,
)

// The library form's folder picker (GET /api/media/browse): the server's own disks and the
// shared folders of computers on the network, walked one folder at a time. A folder that can't
// be opened comes back as a normal answer with `error`, and `needsLogin` brings up the sign-in
// form for that computer. Shown in place of the library form rather than as its own route, so
// the chosen path goes straight back into the form.
@Composable
fun MediaFolderPicker(
    state: SentinelUiState,
    model: SentinelViewModel,
    startPath: String,
    onUse: (String) -> Unit,
    onClose: () -> Unit,
    // Music & Podcasts reuse this picker against /api/audio/browse, whose counts are audio files.
    routeBase: String = MediaApi.MEDIA_ROUTES,
    countLabel: (Int) -> String = MediaLibraryForm::videoCountLabel,
    pathPlaceholder: String = "/mnt/movies or smb://PC/Movies",
    backLabel: String = "Back to the library",
) {
    val scope = rememberCoroutineScope()
    var listing by remember { mutableStateOf<MediaBrowseListing?>(null) }
    var loading by remember { mutableStateOf(false) }
    var requestError by remember { mutableStateOf<String?>(null) }
    var pathField by remember { mutableStateOf(startPath) }

    var discovering by remember { mutableStateOf(false) }
    var discovered by remember { mutableStateOf<List<MediaDiscoveredComputer>?>(null) }
    var discoverError by remember { mutableStateOf<String?>(null) }

    var signIn by remember { mutableStateOf<SignInTarget?>(null) }
    var newFolderOpen by remember { mutableStateOf(false) }

    fun load(path: String) {
        scope.launch {
            loading = true
            requestError = null
            runCatching { model.mediaBrowse(path, routeBase) }
                .onSuccess { result ->
                    listing = result
                    pathField = result.path
                    if (result.needsLogin) {
                        // Match a saved sign-in for the same computer so the form edits it
                        // (and offers Forget) instead of adding a second one.
                        val host = result.host.orEmpty()
                        val saved = if (host.isBlank()) null else runCatching { model.mediaNetworkComputers(routeBase) }
                            .getOrNull()?.firstOrNull { it.host.equals(host, ignoreCase = true) }
                        signIn = SignInTarget(saved, saved?.host ?: host, result.error)
                    }
                }
                .onFailure { requestError = it.message ?: "Could not open that folder." }
            loading = false
        }
    }

    LaunchedEffect(Unit) { load(startPath) }

    val target = signIn
    BackHandler(enabled = target != null) { signIn = null }
    if (target != null) {
        MediaNetworkSignInForm(routeBase = routeBase,
            state = state,
            model = model,
            target = target,
            onSignedIn = { result ->
                signIn = null
                load(result.computer.path)
            },
            onForgotten = {
                signIn = null
                load("")
            },
            onBack = { signIn = null },
        )
        return
    }

    val current = listing
    Column(Modifier.fillMaxSize()) {
        ScreenHeader("Choose a Folder", state)
        TextButton(onClick = onClose, modifier = Modifier.fillMaxWidth()) {
            Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
            Text(backLabel, modifier = Modifier.weight(1f).padding(start = 8.dp))
        }
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            OutlinedTextField(
                value = pathField,
                onValueChange = { pathField = it },
                label = { Text("Folder") },
                placeholder = { Text(pathPlaceholder) },
                singleLine = true,
                textStyle = MaterialTheme.typography.bodyMedium.copy(fontFamily = FontFamily.Monospace),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Go),
                keyboardActions = KeyboardActions(onGo = { load(pathField.trim()) }),
                modifier = Modifier.weight(1f),
            )
            OutlinedButton(onClick = { load(pathField.trim()) }, enabled = !loading) { Text("Go") }
        }
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            TextButton(
                onClick = { current?.parent?.let { load(it) } },
                enabled = !loading && current?.canGoUp == true,
            ) {
                Icon(Icons.Filled.ArrowUpward, contentDescription = null, modifier = Modifier.size(18.dp))
                Text("Up", modifier = Modifier.padding(start = 4.dp))
            }
            TextButton(onClick = { load("") }, enabled = !loading && current?.isRoots != true) {
                Icon(Icons.Filled.Home, contentDescription = null, modifier = Modifier.size(18.dp))
                Text("Start", modifier = Modifier.padding(start = 4.dp))
            }
            if (current?.canMakeFolder == true) {
                TextButton(onClick = { newFolderOpen = true }, enabled = !loading) {
                    Icon(Icons.Filled.CreateNewFolder, contentDescription = null, modifier = Modifier.size(18.dp))
                    Text("New folder", modifier = Modifier.padding(start = 4.dp))
                }
            }
            Spacer(Modifier.weight(1f))
            if (loading) CircularProgressIndicator(Modifier.size(18.dp).padding(end = 8.dp), strokeWidth = 2.dp)
        }
        HorizontalDivider()

        LazyColumn(
            modifier = Modifier.weight(1f).fillMaxWidth(),
            verticalArrangement = Arrangement.spacedBy(2.dp),
        ) {
            requestError?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(14.dp)) }
            }
            if (current == null) return@LazyColumn

            current.error?.let { message ->
                item {
                    Column(Modifier.padding(horizontal = 14.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text(message, color = SentinelRed)
                        if (current.needsLogin) {
                            OutlinedButton(onClick = {
                                scope.launch {
                                    val host = current.host.orEmpty()
                                    val saved = runCatching { model.mediaNetworkComputers(routeBase) }.getOrNull()
                                        ?.firstOrNull { it.host.equals(host, ignoreCase = true) }
                                    signIn = SignInTarget(saved, saved?.host ?: host, message)
                                }
                            }) { Text("Sign in to ${current.host ?: "this computer"}…") }
                        }
                    }
                }
            }
            current.hint?.let { hint ->
                item {
                    Text(hint, style = MaterialTheme.typography.bodySmall, color = SentinelAmber, modifier = Modifier.padding(horizontal = 14.dp, vertical = 6.dp))
                }
            }
            val ownCount = current.videoCount ?: 0
            if (!current.isRoots && current.error == null && ownCount > 0) {
                item {
                    Text(
                        "${countLabel(ownCount)} in this folder",
                        style = MaterialTheme.typography.bodySmall,
                        color = SentinelGreen,
                        modifier = Modifier.padding(horizontal = 14.dp, vertical = 6.dp),
                    )
                }
            }

            if (current.isRoots) {
                item { PickerSectionTitle("On this server") }
                if (current.folders.isEmpty()) {
                    item { PickerNote("No folders found. Type a path above.") }
                }
                items(current.folders, key = { "local-" + it.path }) { folder ->
                    FolderRow(folder, Icons.Filled.Folder, showPath = true, countLabel = countLabel) { load(folder.path) }
                }

                item { PickerSectionTitle("Network computers") }
                if (current.computers.isEmpty()) {
                    item { PickerNote("None signed in to yet. Add a computer, or find the ones that share folders.") }
                }
                items(current.computers, key = { "pc-" + it.id }) { computer ->
                    ComputerRow(
                        title = computer.name,
                        subtitle = listOf(computer.host, computer.username.ifBlank { "guest" }).joinToString(" · "),
                        onOpen = { load(computer.path) },
                        onSignIn = { signIn = SignInTarget(computer, computer.host) },
                    )
                }
                item {
                    Row(
                        Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 6.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        OutlinedButton(onClick = { signIn = SignInTarget(null, "") }, modifier = Modifier.weight(1f)) {
                            Text("Add a computer")
                        }
                        OutlinedButton(
                            onClick = {
                                scope.launch {
                                    discovering = true
                                    discoverError = null
                                    runCatching { model.discoverMediaNetworkComputers(routeBase) }
                                        .onSuccess { discovered = it }
                                        .onFailure { discoverError = it.message ?: "Could not look for computers." }
                                    discovering = false
                                }
                            },
                            enabled = !discovering,
                            modifier = Modifier.weight(1f),
                        ) { Text(if (discovering) "Looking…" else "Find computers") }
                    }
                }
                if (discovering) {
                    item { LinearProgressIndicator(Modifier.fillMaxWidth().padding(horizontal = 14.dp), color = SentinelGreen) }
                }
                discoverError?.let { message ->
                    item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
                }
                discovered?.let { found ->
                    item { PickerSectionTitle("Found on the network") }
                    if (found.isEmpty()) {
                        item { PickerNote("No computers answered. Macs need File Sharing turned on.") }
                    }
                    items(found, key = { "found-" + it.path }) { computer ->
                        val saved = computer.savedId?.let { id -> current.computers.firstOrNull { it.id == id } }
                        ComputerRow(
                            title = computer.name,
                            subtitle = listOfNotNull(computer.host, computer.address?.takeIf { it != computer.host }, if (saved != null) "signed in" else null)
                                .joinToString(" · "),
                            onOpen = { load(computer.path) },
                            onSignIn = { signIn = SignInTarget(saved, saved?.host ?: computer.host) },
                        )
                    }
                }
                item {
                    PickerNote(
                        "Windows PCs don't show up under Find computers: add a Windows PC by its name or IP address.",
                    )
                }
            } else {
                if (current.error == null && current.folders.isEmpty()) {
                    item { PickerNote(if (current.kind == "computer") "No shared folders listed." else "No folders inside this one.") }
                }
                val icon = if (current.kind == "computer") Icons.Filled.FolderShared else Icons.Filled.Folder
                items(current.folders, key = { "f-" + it.path }) { folder ->
                    FolderRow(folder, icon, showPath = false, countLabel = countLabel) { load(folder.path) }
                }
            }
        }

        HorizontalDivider()
        Column(Modifier.fillMaxWidth().padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            if (current?.canUse == true) {
                Text(
                    current.path,
                    style = MaterialTheme.typography.bodySmall,
                    fontFamily = FontFamily.Monospace,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    color = SentinelTextDim,
                )
            } else if (current?.kind == "computer") {
                Text("Open one of its shared folders.", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
            }
            Button(
                onClick = { current?.let { onUse(it.path) } },
                enabled = !loading && current?.canUse == true,
                modifier = Modifier.fillMaxWidth(),
            ) { Text("Use this folder") }
        }
    }

    if (newFolderOpen && current != null) {
        NewFolderDialog(
            parent = current.path,
            onCreate = { name -> model.mediaCreateFolder(current.path, name, routeBase) },
            onCreated = { result ->
                newFolderOpen = false
                // The answer is the new folder's own page, so the picker is now inside it.
                listing = result
                pathField = result.path
                requestError = null
            },
            onDismiss = { newFolderOpen = false },
        )
    }
}

// POST {base}/browse/folder. The server refuses a bad name or a folder it can't write to with a
// 400 whose message shows under the field; the dialog stays open so the name can be fixed.
@Composable
private fun NewFolderDialog(
    parent: String,
    onCreate: suspend (String) -> MediaBrowseListing,
    onCreated: (MediaBrowseListing) -> Unit,
    onDismiss: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    var name by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    fun create() {
        if (busy || name.isBlank()) return
        scope.launch {
            busy = true
            error = null
            runCatching { onCreate(name.trim()) }
                .onSuccess(onCreated)
                .onFailure { error = it.message ?: "Could not make that folder." }
            busy = false
        }
    }

    AlertDialog(
        onDismissRequest = { if (!busy) onDismiss() },
        title = { Text("New folder") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    "Inside $parent",
                    style = MaterialTheme.typography.bodySmall,
                    fontFamily = FontFamily.Monospace,
                    color = SentinelTextDim,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it; error = null },
                    label = { Text("Folder name") },
                    placeholder = { Text("TV Shows") },
                    singleLine = true,
                    enabled = !busy,
                    isError = error != null,
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Words, imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = { create() }),
                    modifier = Modifier.fillMaxWidth(),
                )
                error?.let { Text(it, color = SentinelRed, style = MaterialTheme.typography.bodySmall) }
            }
        },
        confirmButton = {
            TextButton(onClick = { create() }, enabled = !busy && name.isNotBlank()) {
                Text(if (busy) "Creating…" else "Create")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss, enabled = !busy) { Text("Cancel") }
        },
    )
}

@Composable
private fun PickerSectionTitle(title: String) {
    Text(title, color = SentinelCyan, style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(start = 14.dp, end = 14.dp, top = 12.dp, bottom = 4.dp))
}

@Composable
private fun PickerNote(text: String) {
    Text(text, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim, modifier = Modifier.padding(horizontal = 14.dp, vertical = 4.dp))
}

@Composable
private fun FolderRow(folder: MediaBrowseFolder, icon: ImageVector, showPath: Boolean, countLabel: (Int) -> String, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 14.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(icon, contentDescription = null, tint = SentinelGreen, modifier = Modifier.size(22.dp))
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(folder.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (showPath && folder.path != folder.name) {
                Text(folder.path, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace, color = SentinelTextDim, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        val count = folder.videoCount ?: 0
        if (count > 0) {
            Surface(shape = RoundedCornerShape(10.dp), color = MaterialTheme.colorScheme.secondaryContainer) {
                Text(
                    countLabel(count),
                    style = MaterialTheme.typography.labelSmall,
                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 3.dp),
                )
            }
        }
    }
}

@Composable
private fun ComputerRow(title: String, subtitle: String, onOpen: () -> Unit, onSignIn: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onOpen).padding(start = 14.dp, end = 4.dp, top = 6.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Icons.Filled.Computer, contentDescription = null, tint = SentinelCyan, modifier = Modifier.size(22.dp))
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(subtitle, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        TextButton(onClick = onSignIn) { Text("Sign-in") }
    }
}

// Sign in to a computer's shared folders (POST /api/media/network, or PUT for a saved one).
// The server tests the sign-in before saving it, so a refusal keeps this form open with the
// server's message.
@Composable
private fun MediaNetworkSignInForm(routeBase: String,
    state: SentinelUiState,
    model: SentinelViewModel,
    target: SignInTarget,
    onSignedIn: (MediaNetworkSignInResult) -> Unit,
    onForgotten: () -> Unit,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val existing = target.existing
    var host by remember(target) { mutableStateOf(existing?.host ?: target.host) }
    var name by remember(target) { mutableStateOf(existing?.name?.takeIf { it != existing.host }.orEmpty()) }
    var username by remember(target) { mutableStateOf(existing?.username.orEmpty()) }
    var password by remember(target) { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var error by remember(target) { mutableStateOf<String?>(null) }
    var confirmForget by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader(if (existing != null) "Sign In: ${existing.name}" else "Sign In to a Computer", state)
        LazyColumn(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            item {
                TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
                    Text("Back to the folders", modifier = Modifier.weight(1f).padding(start = 8.dp))
                }
            }
            target.reason?.let { reason ->
                item { Text(reason, color = SentinelAmber, modifier = Modifier.padding(horizontal = 14.dp)) }
            }
            item {
                Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        OutlinedTextField(
                            value = host,
                            onValueChange = { host = it },
                            label = { Text("Computer name or IP address") },
                            placeholder = { Text("DESKTOP-PC or 192.168.1.20") },
                            singleLine = true,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = name,
                            onValueChange = { name = it },
                            label = { Text("Name to show (optional)") },
                            placeholder = { Text("Den PC") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = username,
                            onValueChange = { username = it },
                            label = { Text("Username on that computer") },
                            placeholder = { Text("Blank to try as a guest") },
                            singleLine = true,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = password,
                            onValueChange = { password = it },
                            label = { Text("Password") },
                            placeholder = if (existing?.hasPassword == true) {
                                { Text("blank keeps the saved password") }
                            } else null,
                            singleLine = true,
                            visualTransformation = PasswordVisualTransformation(),
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        Text(
                            "For a Windows PC, use the Windows account (a Microsoft account works: its email and password). " +
                                "On a Mac, turn on File Sharing. Windows computers don't show up under Find computers — " +
                                "type the PC's name or IP address.",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }
            error?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }
            item {
                Row(Modifier.fillMaxWidth().padding(horizontal = 14.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = onBack, enabled = !busy, modifier = Modifier.weight(1f)) { Text("Back") }
                    Button(
                        onClick = {
                            scope.launch {
                                busy = true
                                error = null
                                val input = MediaNetworkSignInInput(
                                    host = host.trim(),
                                    name = name.trim(),
                                    username = username.trim(),
                                    password = password,
                                    domain = existing?.domain.orEmpty(),
                                )
                                runCatching { model.saveMediaNetworkComputer(existing?.id, input, routeBase) }
                                    .onSuccess(onSignedIn)
                                    // A MediaNetworkSignInException is the server refusing the
                                    // sign-in; either way the form stays open with the message.
                                    .onFailure { error = it.message ?: "Could not sign in to that computer." }
                                busy = false
                            }
                        },
                        enabled = !busy && host.isNotBlank(),
                        modifier = Modifier.weight(1f),
                    ) { Text(if (busy) "Signing in…" else "Sign in") }
                }
            }
            if (existing != null) {
                item {
                    TextButton(
                        onClick = { confirmForget = true },
                        enabled = !busy,
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                    ) { Text("Forget this computer", color = SentinelRed) }
                }
            }
            item { Spacer(Modifier.size(20.dp)) }
        }
    }

    if (confirmForget && existing != null) {
        ConfirmDialog(
            title = "Forget \"${existing.name}\"?",
            text = "The server forgets this sign-in. Libraries on that computer fail to scan until it is signed in to again, " +
                "unless it lets guests in. Nothing on the computer is touched.",
            confirmLabel = "Forget",
            onConfirm = {
                confirmForget = false
                scope.launch {
                    busy = true
                    runCatching { model.deleteMediaNetworkComputer(existing.id, routeBase) }
                        .onSuccess { onForgotten() }
                        .onFailure { error = it.message ?: "Could not forget that computer." }
                    busy = false
                }
            },
            onDismiss = { confirmForget = false },
        )
    }
}

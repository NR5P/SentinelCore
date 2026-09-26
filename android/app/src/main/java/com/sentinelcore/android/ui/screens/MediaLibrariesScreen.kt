package com.sentinelcore.android.ui.screens

import androidx.activity.compose.BackHandler
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
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Lan
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
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
import com.sentinelcore.android.data.MediaLibrary
import com.sentinelcore.android.data.MediaLibraryForm
import com.sentinelcore.android.data.MediaLibraryInput
import com.sentinelcore.android.data.MediaScan
import com.sentinelcore.android.data.MediaStatus
import com.sentinelcore.android.data.ServerTimestamp
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.SaveButton
import com.sentinelcore.android.ui.components.SaveFeedbackHost
import com.sentinelcore.android.ui.components.relativeTime
import com.sentinelcore.android.ui.components.rememberSaveFeedback
import com.sentinelcore.android.ui.files.ConfirmDialog
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// Settings -> Movies & TV libraries (Admin): the dashboard's library list, so the folders the
// Movies tab reads from can be managed from the phone. The folder itself is picked with
// MediaFolderPicker, which walks the server's own disks and shared folders on other computers.
// Contract: core/docs/MOVIES.md, "Libraries" and the two folder sections after it.
@Composable
fun MediaLibrariesScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onBack: () -> Unit,
    onEditLibrary: (String?) -> Unit,
) {
    val scope = rememberCoroutineScope()
    var libraries by remember { mutableStateOf<List<MediaLibrary>?>(null) }
    var status by remember { mutableStateOf<MediaStatus?>(null) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var actionMessage by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(false) }
    var confirmRemove by remember { mutableStateOf<MediaLibrary?>(null) }

    suspend fun reload() {
        loading = true
        // The list comes from /libraries, which answers "turned off" as a 404 with its own
        // message; /status only adds the scan in progress.
        runCatching { model.mediaLibraries() }
            .onSuccess {
                libraries = it
                loadError = null
            }
            .onFailure { loadError = it.message ?: "Could not load the libraries." }
        runCatching { model.mediaStatus() }.onSuccess { status = it }
        loading = false
    }

    LaunchedEffect(Unit) { reload() }

    // Follow a scan while this screen is open; the loop ends (and the effect with it) as soon as
    // a reload sees the scan finished, which also brings in the fresh "last scanned" times.
    val scanning = status?.scanning == true
    LaunchedEffect(scanning) {
        while (scanning) {
            delay(3_000)
            reload()
        }
    }

    fun act(label: String, block: suspend () -> Unit) {
        scope.launch {
            actionMessage = null
            runCatching { block() }
                .onSuccess { reload() }
                .onFailure { actionMessage = it.message ?: "$label failed." }
        }
    }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader(
            "Movies & TV Libraries",
            state,
            onRefresh = { scope.launch { reload() } },
            actions = {
                IconButton(onClick = { onEditLibrary(null) }, enabled = loadError == null) {
                    Icon(Icons.Filled.Add, contentDescription = "Add library")
                }
            },
        )
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item {
                TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
                    Text("Back to Settings", modifier = Modifier.weight(1f).padding(start = 8.dp))
                }
            }

            item {
                Text(
                    "The folders the Movies tab reads from: on the server itself, or shared folders on other computers " +
                        "on your network. Adding a library starts a scan of it.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                )
            }

            loadError?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }

            if (loadError == null) {
                item {
                    Row(
                        Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Button(onClick = { onEditLibrary(null) }, modifier = Modifier.weight(1f)) { Text("Add library") }
                        OutlinedButton(
                            onClick = { act("Scan") { model.scanAllMedia() } },
                            enabled = !scanning && !libraries.isNullOrEmpty(),
                            modifier = Modifier.weight(1f),
                        ) { Text("Scan all") }
                    }
                }
            }

            status?.scan?.takeIf { scanning }?.let { scan ->
                item { ScanBanner(scan) }
            }
            if (scanning && status?.scan == null) {
                item { ScanBanner(MediaScan(phase = "files")) }
            }

            actionMessage?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }

            val list = libraries
            if (list == null && loadError == null && loading) {
                item {
                    Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.width(10.dp))
                        Text("Loading…", color = SentinelTextDim)
                    }
                }
            }
            if (list != null && list.isEmpty() && loadError == null) {
                item {
                    Text(
                        "No libraries yet. Add one for your movies folder and one for TV shows.",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(14.dp),
                    )
                }
            }

            items(list.orEmpty(), key = { it.id }) { library ->
                LibraryCard(
                    library = library,
                    scanning = scanning,
                    onEdit = { onEditLibrary(library.id) },
                    onScan = { act("Scan") { model.scanMediaLibrary(library.id) } },
                    onRemove = { confirmRemove = library },
                )
            }
            item { Spacer(Modifier.size(20.dp)) }
        }
    }

    confirmRemove?.let { library ->
        ConfirmDialog(
            title = "Remove \"${library.name}\"?",
            text = "Its titles and everyone's watched progress are removed from Sentinel Core. The video files are not touched.",
            confirmLabel = "Remove",
            onConfirm = {
                confirmRemove = null
                act("Remove") { model.deleteMediaLibrary(library.id) }
            },
            onDismiss = { confirmRemove = null },
        )
    }
}

private fun scanPhaseLabel(phase: String): String = when (phase) {
    "files" -> "Looking for videos"
    "metadata" -> "Fetching titles and artwork"
    "probing" -> "Reading video details"
    else -> "Scanning"
}

@Composable
private fun ScanBanner(scan: MediaScan) {
    Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            val where = scan.libraryName?.let { " $it" }.orEmpty()
            Text("Scanning$where…", style = MaterialTheme.typography.titleSmall, color = SentinelAmber)
            val counts = if (scan.total > 0) " · ${scan.processed} of ${scan.total}" else ""
            Text(scanPhaseLabel(scan.phase) + counts, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
            if (scan.total > 0) {
                LinearProgressIndicator(
                    progress = { (scan.processed.toFloat() / scan.total).coerceIn(0f, 1f) },
                    modifier = Modifier.fillMaxWidth(),
                    color = SentinelGreen,
                )
            } else {
                LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
            }
        }
    }
}

private fun lastScannedText(library: MediaLibrary): String? {
    val finished = library.lastScanFinishedAt ?: return "Not scanned yet"
    return runCatching { "Last scanned " + ServerTimestamp.parse(finished).relativeTime() }.getOrNull()
}

@Composable
private fun LibraryCard(
    library: MediaLibrary,
    scanning: Boolean,
    onEdit: () -> Unit,
    onScan: () -> Unit,
    onRemove: () -> Unit,
) {
    Card(Modifier.fillMaxWidth().padding(horizontal = 14.dp)) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(library.name, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                if (library.network) {
                    Icon(Icons.Filled.Lan, contentDescription = "On another computer", tint = SentinelTextDim, modifier = Modifier.size(18.dp))
                }
            }
            val titles = if (library.itemCount == 1) "1 title" else "${library.itemCount} titles"
            Text(
                "${MediaLibraryForm.kindLabel(library.kind)} · $titles",
                style = MaterialTheme.typography.bodySmall,
                color = SentinelTextDim,
            )
            Text(
                library.path.orEmpty(),
                style = MaterialTheme.typography.bodySmall,
                fontFamily = FontFamily.Monospace,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            val error = library.lastScanError
            if (error != null) {
                Text("Last scan failed: $error", style = MaterialTheme.typography.bodySmall, color = SentinelRed)
            } else {
                lastScannedText(library)?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim) }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                TextButton(onClick = onEdit) { Text("Edit", color = SentinelGreen) }
                TextButton(onClick = onScan, enabled = !scanning) { Text("Scan now") }
                Spacer(Modifier.weight(1f))
                TextButton(onClick = onRemove) { Text("Remove", color = SentinelRed) }
            }
        }
    }
}

// Add (libraryId null) or edit one library. The folder is typed or picked with Browse…, which
// opens MediaFolderPicker over this form so the chosen path lands straight in the field.
@Composable
fun MediaLibraryEditScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    libraryId: String?,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val feedback = rememberSaveFeedback()

    var loaded by remember { mutableStateOf(libraryId == null) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var name by remember { mutableStateOf(MediaLibraryForm.defaultName("movies")) }
    var kind by remember { mutableStateOf("movies") }
    var path by remember { mutableStateOf("") }
    var errors by remember { mutableStateOf<String?>(null) }
    var isSaving by remember { mutableStateOf(false) }
    var showPicker by remember { mutableStateOf(false) }

    LaunchedEffect(libraryId) {
        if (libraryId == null) return@LaunchedEffect
        runCatching { model.mediaLibraries() }
            .onSuccess { list ->
                val library = list.firstOrNull { it.id == libraryId }
                if (library == null) {
                    loadError = "That library is no longer there."
                } else {
                    name = library.name
                    kind = if (library.isShows) "shows" else "movies"
                    path = library.path.orEmpty()
                }
                loaded = true
            }
            .onFailure { loadError = it.message ?: "Could not load that library." }
    }

    BackHandler(enabled = showPicker) { showPicker = false }

    if (showPicker) {
        MediaFolderPicker(
            state = state,
            model = model,
            startPath = path.trim(),
            onUse = {
                path = it
                showPicker = false
            },
            onClose = { showPicker = false },
        )
        return
    }

    val canSave = loaded && loadError == null && name.isNotBlank() && path.isNotBlank()

    SaveFeedbackHost(feedback) {
        ScreenHeader(if (libraryId == null) "New Library" else "Edit Library", state)
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item {
                TextButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
                    Text("Back to Libraries", modifier = Modifier.weight(1f).padding(start = 8.dp))
                }
            }
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
                        Text("What's in it", style = MaterialTheme.typography.labelLarge, color = SentinelGreen)
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            listOf("movies", "shows").forEach { option ->
                                FilterChip(
                                    selected = kind == option,
                                    onClick = {
                                        name = MediaLibraryForm.nameAfterKindChange(name, option)
                                        kind = option
                                    },
                                    label = { Text(MediaLibraryForm.kindLabel(option)) },
                                )
                            }
                        }
                        OutlinedTextField(
                            value = path,
                            onValueChange = { path = it },
                            label = { Text("Folder") },
                            placeholder = { Text("/mnt/movies or smb://PC/Movies") },
                            singleLine = true,
                            textStyle = MaterialTheme.typography.bodyMedium.copy(fontFamily = FontFamily.Monospace),
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedButton(onClick = { showPicker = true }, modifier = Modifier.fillMaxWidth()) {
                            Text("Browse…")
                        }
                        Text(
                            if (kind == "shows") {
                                "One folder per show, with season folders or S01E02 in the file names."
                            } else {
                                "Movie files, loose or one per folder. The server reads the title and year from the name."
                            },
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }

            errors?.let { message ->
                item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 14.dp)) }
            }

            item {
                SaveButton(
                    feedback = feedback,
                    isSaving = isSaving,
                    enabled = canSave,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp),
                    onClick = {
                        scope.launch {
                            isSaving = true
                            errors = null
                            runCatching {
                                model.saveMediaLibrary(libraryId, MediaLibraryInput(name.trim(), kind, path.trim()))
                            }
                                .onSuccess {
                                    feedback.saved()
                                    onBack()
                                }
                                .onFailure {
                                    errors = it.message ?: "Could not save the library."
                                    feedback.failed("Not saved.")
                                }
                            isSaving = false
                        }
                    },
                )
            }
            item { Spacer(Modifier.size(20.dp)) }
        }
    }
}

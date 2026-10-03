package com.sentinelcore.android.ui.audio

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.AudioRuntime
import com.sentinelcore.android.audio.MusicLibrary
import com.sentinelcore.android.audio.MusicStatus
import com.sentinelcore.android.audio.PodcastStatus
import com.sentinelcore.android.data.MediaApi
import com.sentinelcore.android.data.MediaLibraryForm
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.screens.DetailHeader
import com.sentinelcore.android.ui.screens.MediaFolderPicker
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// The hand-built parts of the MUSIC and PODCASTS settings sections (§1). Shown under the schema's
// own fields in Server Settings, and Music → Libraries also as a screen of its own.

// The folder picker, pointed at /api/audio/browse. Full screen, as for Movies.
@Composable
fun AudioFolderPicker(state: SentinelUiState, model: SentinelViewModel, startPath: String, onUse: (String) -> Unit, onClose: () -> Unit) {
    MediaFolderPicker(
        state = state,
        model = model,
        startPath = startPath,
        onUse = onUse,
        onClose = onClose,
        routeBase = MediaApi.AUDIO_ROUTES,
        countLabel = MediaLibraryForm::audioCountLabel,
        pathPlaceholder = "/mnt/music or smb://PC/Music",
        backLabel = "Back",
    )
}

@Composable
fun MusicLibrariesScreen(state: SentinelUiState, model: SentinelViewModel, onBack: () -> Unit) {
    var picker by remember { mutableStateOf<Pair<String, (String) -> Unit>?>(null) }
    val form = remember { LibraryForm() }
    picker?.let { pick ->
        androidx.activity.compose.BackHandler { picker = null }
        AudioFolderPicker(state, model, pick.first, onUse = { pick.second(it); picker = null }, onClose = { picker = null })
        return
    }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Music libraries", onBack = onBack)
        Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp)) {
            MusicLibrariesPanel(model.audio, form) { start, use -> picker = start to use }
        }
    }
}

// The add/edit form, held by the screen rather than the panel: the folder picker takes the whole
// screen while it's open, and what was typed must be there when it closes.
@androidx.compose.runtime.Stable
class LibraryForm {
    var isOpen by mutableStateOf(false)
    var editing by mutableStateOf<MusicLibrary?>(null)
    var name by mutableStateOf("")
    var path by mutableStateOf("")

    fun open(library: MusicLibrary?) {
        editing = library
        name = library?.name ?: "Music"
        path = library?.path ?: ""
        isOpen = true
    }

    fun close() {
        isOpen = false
        editing = null
    }
}

// Libraries: list, add (folder picker), rename, remove, scan, and the scan's progress.
@Composable
fun MusicLibrariesPanel(runtime: AudioRuntime, form: LibraryForm = remember { LibraryForm() }, pickFolder: (String, (String) -> Unit) -> Unit) {
    val api = runtime.api
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf<MusicStatus?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var tick by remember { mutableStateOf(0) }
    var removing by remember { mutableStateOf<MusicLibrary?>(null) }
    LaunchedEffect(tick) {
        while (true) {
            runCatching { api.musicStatus() }.onSuccess { status = it; error = null }.onFailure { error = it.message }
            delay(if (status?.scanning == true) 1_500 else 15_000)
        }
    }
    fun run(block: suspend () -> Unit) {
        scope.launch {
            runCatching { block() }.onFailure { error = it.message ?: "That didn't work." }
            tick++
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text("LIBRARIES", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
        error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        val current = status
        if (current == null) {
            if (error == null) CircularProgressIndicator(Modifier.size(24.dp))
        } else {
            if (!current.ffmpegAvailable) Text("ffmpeg isn't installed on the server, so files can't be converted for devices that need it.", color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
            current.scan?.takeIf { current.scanning }?.let { scan ->
                Column {
                    Text("Scanning ${scan.libraryName ?: ""}: ${scan.phase ?: ""} ${scan.processed}/${scan.total}", style = MaterialTheme.typography.bodySmall)
                    LinearProgressIndicator(progress = { if (scan.total > 0) scan.processed.toFloat() / scan.total else 0f }, modifier = Modifier.fillMaxWidth())
                }
            }
            Text(
                "${current.trackCount} songs · ${current.albumCount} albums · ${current.artistCount} artists · ${AudioFormat.bytes(current.totalBytes)}" +
                    (current.lastScanFinishedAt?.let { " · scanned ${AudioFormat.day(it).lowercase()}" } ?: ""),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            current.lastScanError?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
            if (current.libraries.isEmpty()) Text("No music folders yet.", style = MaterialTheme.typography.bodyMedium)
            current.libraries.forEach { library ->
                Card(Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Icon(if (library.network) Icons.Default.Computer else Icons.Default.Folder, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                            Spacer(Modifier.width(8.dp))
                            Text(library.name, fontWeight = FontWeight.Medium, modifier = Modifier.weight(1f))
                            Text("${library.trackCount} songs", style = MaterialTheme.typography.labelSmall)
                        }
                        Text(library.path, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        library.lastScanError?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
                        Row {
                            TextButton(enabled = !current.scanning, onClick = { run { api.scanMusicLibrary(library.id) } }) { Text("Scan") }
                            TextButton(onClick = { form.open(library) }) { Text("Rename / move") }
                            TextButton(onClick = { removing = library }) { Text("Remove", color = MaterialTheme.colorScheme.error) }
                        }
                    }
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = { form.open(null) }) { Text("Add a music folder") }
                if (current.libraries.isNotEmpty()) OutlinedButton(enabled = !current.scanning, onClick = { run { api.scanMusicLibrary(null) } }) { Text("Scan all") }
            }
        }
    }
    if (form.isOpen) {
        val library = form.editing
        var saving by remember { mutableStateOf(false) }
        var formError by remember(library) { mutableStateOf<String?>(null) }
        AlertDialog(
            onDismissRequest = { form.close() },
            title = { Text(if (library == null) "Add a music folder" else "Edit ${library.name}") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(value = form.name, onValueChange = { form.name = it }, label = { Text("Name") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    OutlinedTextField(value = form.path, onValueChange = { form.path = it }, label = { Text("Folder") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                    OutlinedButton(onClick = { pickFolder(form.path) { chosen -> form.path = chosen } }) { Text("Choose…") }
                    formError?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
                }
            },
            confirmButton = {
                TextButton(enabled = form.name.isNotBlank() && form.path.isNotBlank() && !saving, onClick = {
                    saving = true
                    scope.launch {
                        runCatching { api.saveMusicLibrary(library?.id, form.name.trim(), form.path.trim()) }
                            .onSuccess { form.close(); tick++ }
                            .onFailure { formError = it.message }
                        saving = false
                    }
                }) { Text(if (saving) "Saving…" else "Save") }
            },
            dismissButton = { TextButton(onClick = { form.close() }) { Text("Cancel") } },
        )
    }
    removing?.let { library ->
        AlertDialog(
            onDismissRequest = { removing = null },
            title = { Text("Remove ${library.name}?") },
            text = { Text("Its songs leave the library. Nothing on disk is touched, and plays, loves and playlists are kept in case the folder comes back.") },
            confirmButton = { TextButton(onClick = { removing = null; run { api.deleteMusicLibrary(library.id) } }) { Text("Remove", color = MaterialTheme.colorScheme.error) } },
            dismissButton = { TextButton(onClick = { removing = null }) { Text("Cancel") } },
        )
    }
}

// Podcasts → Download folder: storage, the Choose… button (sets the form's downloadFolder field),
// and OPML.
@Composable
fun PodcastsFolderPanel(runtime: AudioRuntime, pickFolder: () -> Unit) {
    var status by remember { mutableStateOf<PodcastStatus?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) {
        runCatching { runtime.api.podcastStatus() }.onSuccess { status = it }.onFailure { error = it.message }
    }
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("DOWNLOAD FOLDER", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
        OutlinedButton(onClick = pickFolder) { Text("Choose…") }
        error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        status?.let { s ->
            s.downloadFolder?.let { Text(it, style = MaterialTheme.typography.bodySmall, fontFamily = FontFamily.Monospace) }
            Text(
                "Using ${AudioFormat.bytes(s.storageBytes)}" + (if (s.maxStorageBytes > 0) " of ${AudioFormat.bytes(s.maxStorageBytes)}" else "") +
                    " · ${s.podcastCount} shows, ${s.episodeCount} episodes" +
                    (if (s.downloadsActive + s.downloadsQueued > 0) " · ${s.downloadsActive} downloading, ${s.downloadsQueued} waiting" else ""),
                style = MaterialTheme.typography.bodySmall,
            )
            if (s.maxStorageBytes > 0) LinearProgressIndicator(progress = { (s.storageBytes.toFloat() / s.maxStorageBytes).coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth())
            Text(
                when {
                    !s.transcriptsEnabled -> "Transcripts are off."
                    s.transcriptsAvailable -> "Transcripts are working" + (if (s.transcriptsQueued > 0) ", ${s.transcriptsQueued} waiting." else ".")
                    else -> "Transcripts aren't working: ${s.transcriptsError ?: "whisper isn't set up"}"
                },
                style = MaterialTheme.typography.bodySmall,
                color = if (s.transcriptsEnabled && !s.transcriptsAvailable) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (!s.ffmpegAvailable) Text("ffmpeg isn't installed, so voice boost, trimming and clips won't work.", color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
        }
        val opml = com.sentinelcore.android.ui.podcasts.rememberOpmlActions()
        Text("OPML", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = opml.import) { Text("Import OPML…") }
            OutlinedButton(onClick = opml.export) { Text("Export OPML") }
        }
    }
}


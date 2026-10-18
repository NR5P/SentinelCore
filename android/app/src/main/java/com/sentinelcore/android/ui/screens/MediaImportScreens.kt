package com.sentinelcore.android.ui.screens

import android.content.ContentResolver
import android.net.Uri
import android.provider.OpenableColumns
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.UploadFile
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import com.sentinelcore.android.data.MediaImportApi
import com.sentinelcore.android.data.MediaImportCandidate
import com.sentinelcore.android.data.MediaImportFile
import com.sentinelcore.android.data.MediaImportFormat
import com.sentinelcore.android.data.MediaImportItem
import com.sentinelcore.android.data.MediaImportJob
import com.sentinelcore.android.data.MediaImportPreview
import com.sentinelcore.android.ui.files.ConfirmDialog
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// "Import a list" for Music and Movies (core/docs/MOVIE_DOWNLOADS.md "Import a list"; the web's
// media-import.js). Three pieces, shared by both tabs:
//   MediaImportScreen        pick files → preview checklist → start
//   ImportedListsSection     the "Imported lists" cards under Downloads
//   MediaImportDetailScreen  one list's items, with pause / search again / leave out / remove

const val MEDIA_IMPORT_ROUTE = "media-import/{kind}"
const val MEDIA_IMPORT_LIST_ROUTE = "media-import/{kind}/list/{id}"

// kind: "music" or "movies".
fun mediaImportRoute(kind: String): String = "media-import/$kind"

fun mediaImportListRoute(kind: String, id: String): String = "media-import/$kind/list/${Uri.encode(id)}"

private const val DETAIL_POLL_MS = 4_000L
private const val CARDS_BUSY_POLL_MS = 5_000L
private const val CARDS_IDLE_POLL_MS = 30_000L

// ---------------- Pick + preview ----------------

internal enum class ImportStage { PICK, PREVIEW }

internal class MediaImportModel(val api: MediaImportApi) : ViewModel() {
    val kind: String get() = api.kind
    var stage by mutableStateOf(ImportStage.PICK)
        private set
    var busy by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    var fileCount by mutableStateOf(0)
        private set
    var preview by mutableStateOf<MediaImportPreview?>(null)
        private set
    var items by mutableStateOf<List<MediaImportCandidate>>(emptyList())
        private set
    var checked by mutableStateOf<Set<String>>(emptySet())
        private set
    var filter by mutableStateOf("")
    var name by mutableStateOf("")

    fun read(resolver: ContentResolver, uris: List<Uri>) {
        if (uris.isEmpty() || busy) return
        viewModelScope.launch {
            busy = true
            error = null
            val files = runCatching { withContext(Dispatchers.IO) { uris.map { readFile(resolver, it) } } }
                .getOrElse {
                    busy = false
                    error = "That file could not be read."
                    return@launch
                }
            if (files.sumOf { it.text.length.toLong() } > MediaImportFormat.MAX_TOTAL_CHARS) {
                busy = false
                error = "That file is too big to be a list."
                return@launch
            }
            runCatching { api.preview(files) }
                .onSuccess { result ->
                    preview = result
                    fileCount = files.size
                    items = MediaImportFormat.candidates(kind, result)
                    checked = MediaImportFormat.initialChecked(items)
                    name = MediaImportFormat.defaultName(kind, files.map { it.name })
                    filter = ""
                    stage = ImportStage.PREVIEW
                }
                .onFailure { error = it.message ?: "Could not read the list." }
            busy = false
        }
    }

    fun toggle(key: String, on: Boolean) {
        checked = if (on) checked + key else checked - key
    }

    // "all", "none", or "missing" (everything not already in the library).
    fun select(which: String) {
        checked = when (which) {
            "none" -> emptySet()
            "all" -> items.map { it.key }.toSet()
            else -> MediaImportFormat.initialChecked(items)
        }
    }

    fun back() {
        stage = ImportStage.PICK
        preview = null
        error = null
    }

    fun start(onStarted: (String) -> Unit) {
        val chosen = items.filter { it.key in checked }
        if (chosen.isEmpty() || busy) return
        viewModelScope.launch {
            busy = true
            error = null
            runCatching { api.start(name.ifBlank { MediaImportFormat.defaultName(kind, emptyList()) }, chosen) }
                .onSuccess { onStarted(MediaImportFormat.startedMessage(kind, chosen.size)) }
                .onFailure { error = it.message ?: "Could not start the list." }
            busy = false
        }
    }

    private fun readFile(resolver: ContentResolver, uri: Uri): MediaImportFile {
        val name = runCatching {
            resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) cursor.getString(0) else null
            }
        }.getOrNull() ?: uri.lastPathSegment?.substringAfterLast('/') ?: "list.csv"
        val text = resolver.openInputStream(uri)?.use { it.readBytes().toString(Charsets.UTF_8) }
            ?: throw java.io.IOException("Could not open $name")
        // A UTF-8 byte-order mark (Excel adds one) would end up in the first column's name.
        return MediaImportFile(name, text.removePrefix("﻿"))
    }
}

@Composable
fun MediaImportScreen(
    api: MediaImportApi,
    onBack: () -> Unit,
    // The list was accepted: the message to show, then usually Downloads.
    onStarted: (String) -> Unit,
) {
    val import: MediaImportModel = viewModel(key = "media-import-${api.kind}") { MediaImportModel(api) }
    val resolver = LocalContext.current.contentResolver
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        import.read(resolver, uris)
    }
    BackHandler(enabled = import.stage == ImportStage.PREVIEW) { import.back() }
    val words = MediaImportFormat.words(import.kind)
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Import a list of ${words.things}", onBack = { if (import.stage == ImportStage.PREVIEW) import.back() else onBack() })
        when (import.stage) {
            ImportStage.PICK -> ImportPick(import, onChoose = { picker.launch(MediaImportFormat.PICKER_TYPES) })
            ImportStage.PREVIEW -> ImportPreview(import, onChooseOther = { import.back() }, onStart = { import.start(onStarted) })
        }
    }
}

@Composable
private fun HelpLine(text: AnnotatedString) {
    Row {
        Text("•", modifier = Modifier.padding(end = 8.dp), color = SentinelTextDim)
        Text(text, style = MaterialTheme.typography.bodyMedium)
    }
}

private fun help(kind: String, linkColor: androidx.compose.ui.graphics.Color): List<AnnotatedString> {
    val bold = SpanStyle(fontWeight = FontWeight.SemiBold)
    val italic = SpanStyle(fontStyle = FontStyle.Italic)
    val mono = SpanStyle(fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace)
    return if (kind == "movies") listOf(
        buildAnnotatedString {
            withStyle(bold) { append("Letterboxd:") }
            append(" Settings → Data → ")
            withStyle(italic) { append("Export your data") }
            append("; choose ")
            withStyle(mono) { append("watchlist.csv") }
            append(" (or ")
            withStyle(mono) { append("watched.csv") }
            append(").")
        },
        buildAnnotatedString {
            withStyle(bold) { append("IMDb:") }
            append(" open your Watchlist or a list → ⋯ → ")
            withStyle(italic) { append("Export") }
            append(".")
        },
        buildAnnotatedString {
            append("Also reads Trakt JSON, or any CSV with ")
            withStyle(italic) { append("Title") }
            append(" and ")
            withStyle(italic) { append("Year") }
            append(" columns. TV shows are left out: add those with ")
            withStyle(bold) { append("+ Add") }
            append(" so you can pick seasons.")
        },
    ) else listOf(
        buildAnnotatedString {
            withStyle(bold) { append("Spotify, quickest:") }
            append(" open ")
            withLink(LinkAnnotation.Url("https://exportify.net", TextLinkStyles(SpanStyle(color = linkColor, textDecoration = TextDecoration.Underline)))) {
                append("exportify.net")
            }
            append(", sign in with Spotify, and press ")
            withStyle(italic) { append("Export") }
            append(" next to Liked Songs or any playlist. Choose the .csv here.")
        },
        buildAnnotatedString {
            withStyle(bold) { append("Spotify, everything:") }
            append(" Spotify → Account → Privacy settings → ")
            withStyle(italic) { append("Download your data") }
            append(" (takes a few days). Choose ")
            withStyle(mono) { append("YourLibrary.json") }
            append(" and the ")
            withStyle(mono) { append("Playlist*.json") }
            append(" files here.")
        },
        buildAnnotatedString {
            append("Also reads TuneMyMusic and Soundiiz exports, or any spreadsheet saved as CSV with ")
            withStyle(italic) { append("Artist") }
            append(", ")
            withStyle(italic) { append("Album") }
            append(" and ")
            withStyle(italic) { append("Track") }
            append(" columns.")
        },
    )
}

@Composable
private fun ImportPick(import: MediaImportModel, onChoose: () -> Unit) {
    val linkColor = MaterialTheme.colorScheme.primary
    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        items(help(import.kind, linkColor)) { HelpLine(it) }
        if (import.kind == "music") {
            item {
                Text(
                    "Torrent sites share whole albums, not songs, so songs are grouped into the albums they come from and each album is downloaded once.",
                    style = MaterialTheme.typography.bodySmall,
                    color = SentinelTextDim,
                )
            }
        }
        item {
            Card(
                Modifier
                    .fillMaxWidth()
                    .padding(top = 6.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .clickable(enabled = !import.busy, onClick = onChoose),
            ) {
                Column(
                    Modifier.fillMaxWidth().padding(vertical = 28.dp, horizontal = 16.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    if (import.busy) {
                        CircularProgressIndicator(Modifier.size(28.dp), strokeWidth = 3.dp)
                        Text("Reading…", fontWeight = FontWeight.SemiBold)
                    } else {
                        Icon(Icons.Default.UploadFile, contentDescription = null, modifier = Modifier.size(32.dp))
                        Text("Choose files", fontWeight = FontWeight.SemiBold)
                    }
                    Text(".csv or .json · several files at once is fine", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
                }
            }
        }
        import.error?.let { message -> item { Text(message, color = SentinelRed) } }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun ImportPreview(import: MediaImportModel, onChooseOther: () -> Unit, onStart: () -> Unit) {
    val preview = import.preview ?: return
    val kind = import.kind
    val words = MediaImportFormat.words(kind)
    val items = import.items
    val shown = remember(items, import.filter) { items.filter { MediaImportFormat.matches(it, import.filter) } }
    Column(Modifier.fillMaxSize()) {
        LazyColumn(
            Modifier.weight(1f).fillMaxWidth(),
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 4.dp, bottom = 12.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            item {
                Text(
                    MediaImportFormat.facts(kind, preview).joinToString("  ·  "),
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                )
            }
            val notes = MediaImportFormat.notes(kind, preview)
            items(notes) { note ->
                Text(
                    note,
                    style = MaterialTheme.typography.bodySmall,
                    color = SentinelAmber,
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(8.dp))
                        .background(SentinelAmber.copy(alpha = 0.12f))
                        .padding(horizontal = 10.dp, vertical = 8.dp),
                )
            }
            if (items.isEmpty()) {
                item {
                    Text(
                        "No ${words.things} found in ${if (import.fileCount == 1) "that file" else "those files"}.",
                        color = SentinelTextDim,
                        modifier = Modifier.padding(vertical = 16.dp),
                    )
                }
            } else {
                item {
                    OutlinedTextField(
                        value = import.filter,
                        onValueChange = { import.filter = it },
                        placeholder = { Text("Filter") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth().padding(top = 6.dp),
                    )
                }
                item {
                    FlowRow(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                        TextButton(onClick = { import.select("all") }) { Text("All") }
                        TextButton(onClick = { import.select("none") }) { Text("None") }
                        TextButton(onClick = { import.select("missing") }) { Text("Only missing") }
                    }
                }
                items(shown.take(MediaImportFormat.SHOWN_AT_ONCE), key = { it.key }) { candidate ->
                    CandidateRow(kind, candidate, candidate.key in import.checked) { import.toggle(candidate.key, it) }
                }
                if (shown.size > MediaImportFormat.SHOWN_AT_ONCE) {
                    item {
                        Text(
                            "…and ${shown.size - MediaImportFormat.SHOWN_AT_ONCE} more (all still included). Filter to find one.",
                            style = MaterialTheme.typography.bodySmall,
                            color = SentinelTextDim,
                        )
                    }
                }
                item {
                    OutlinedTextField(
                        value = import.name,
                        onValueChange = { import.name = it.take(120) },
                        label = { Text("Name this list") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth().padding(top = 10.dp),
                    )
                }
                item {
                    Text(
                        "The server searches your sites for each one through the VPN, picks the best release with people sharing it, and keeps a few downloading at a time. You can leave this screen; it carries on, and the list shows under Downloads.",
                        style = MaterialTheme.typography.bodySmall,
                        color = SentinelTextDim,
                    )
                }
            }
            import.error?.let { message -> item { Text(message, color = SentinelRed) } }
        }
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            OutlinedButton(onClick = onChooseOther, enabled = !import.busy) { Text("Choose other files") }
            Spacer(Modifier.weight(1f))
            if (items.isNotEmpty()) {
                Button(onClick = onStart, enabled = import.checked.isNotEmpty() && !import.busy) {
                    Text(MediaImportFormat.startLabel(kind, import.checked.size, import.busy), maxLines = 1)
                }
            }
        }
    }
}

@Composable
private fun CandidateRow(kind: String, item: MediaImportCandidate, checked: Boolean, onChecked: (Boolean) -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .clickable { onChecked(!checked) }
            .padding(vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Checkbox(checked = checked, onCheckedChange = onChecked)
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(
                    item.title,
                    fontWeight = FontWeight.Medium,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                    color = if (item.inLibrary) SentinelTextDim else androidx.compose.ui.graphics.Color.Unspecified,
                )
                item.year?.let { DownloadTag(it.toString()) }
                if (item.inLibrary) DownloadTag("in library", SentinelGreen)
                if (item.single) DownloadTag("single")
            }
            if (kind == "music") {
                Text(
                    listOfNotNull(
                        item.artist?.takeIf { it.isNotBlank() },
                        item.trackCount.takeIf { it > 0 }?.let { "${MediaImportFormat.plural(it, "song")} on your list" },
                    ).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    color = SentinelTextDim,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

// ---------------- Shared list actions ----------------

// Pause/resume, search missing again, remove - the card and the details screen offer the same.
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun JobButtons(
    job: MediaImportJob,
    busy: Boolean,
    onDetails: (() -> Unit)?,
    onPause: (Boolean) -> Unit,
    onRetry: () -> Unit,
    onRemove: () -> Unit,
) {
    FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        onDetails?.let { OutlinedButton(onClick = it) { Text("Details") } }
        if (MediaImportFormat.canPause(job)) {
            TextButton(onClick = { onPause(!job.paused) }, enabled = !busy) { Text(if (job.paused) "Resume" else "Pause") }
        }
        if (MediaImportFormat.canRetry(job)) {
            TextButton(onClick = onRetry, enabled = !busy) { Text("Search missing again") }
        }
        TextButton(onClick = onRemove, enabled = !busy) { Text("Remove list", color = SentinelRed) }
    }
}

@Composable
private fun JobSummary(job: MediaImportJob) {
    val status = MediaImportFormat.status(job)
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            DownloadTag(status, if (status == "working") SentinelGreen else SentinelTextDim)
            Text(
                MediaImportFormat.summaryBits(job.counts).joinToString(" · "),
                style = MaterialTheme.typography.bodySmall,
                color = SentinelTextDim,
            )
        }
        LinearProgressIndicator(
            progress = { MediaImportFormat.settledFraction(job.counts) },
            modifier = Modifier.fillMaxWidth().height(4.dp),
        )
        if (job.message != null && !job.paused) {
            Text(job.message, style = MaterialTheme.typography.bodySmall, color = SentinelAmber)
        }
    }
}

private const val REMOVE_TEXT = "Nothing more on it is searched for. Downloads it already started carry on under Downloads."

// ---------------- "Imported lists" under Downloads ----------------

// A self-contained block (it loads and polls on its own) to drop into a Downloads screen. Draws
// nothing when the server has no imports (an older server, or downloads off).
@Composable
fun ImportedListsSection(
    api: MediaImportApi,
    onImport: () -> Unit,
    onOpen: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val scope = rememberCoroutineScope()
    var jobs by remember { mutableStateOf<List<MediaImportJob>?>(null) }
    var supported by remember { mutableStateOf(true) }
    var error by remember { mutableStateOf<String?>(null) }
    var busyId by remember { mutableStateOf<String?>(null) }
    var confirmRemove by remember { mutableStateOf<MediaImportJob?>(null) }
    var tick by remember { mutableStateOf(0) }

    suspend fun refresh() {
        runCatching { api.list() }
            .onSuccess { result ->
                if (result == null) supported = false else { jobs = result; error = null }
            }
            .onFailure { error = it.message ?: "Could not load the imported lists." }
    }

    LaunchedEffect(tick) {
        while (true) {
            refresh()
            if (!supported) break
            delay(if (MediaImportFormat.anyBusy(jobs.orEmpty())) CARDS_BUSY_POLL_MS else CARDS_IDLE_POLL_MS)
        }
    }

    fun act(id: String, block: suspend () -> Unit) {
        scope.launch {
            busyId = id
            runCatching { block() }.onFailure { error = it.message ?: "That didn't work." }
            busyId = null
            tick++
        }
    }

    if (!supported) return
    val words = MediaImportFormat.words(api.kind)
    Column(modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Imported lists", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
            OutlinedButton(onClick = onImport) {
                Icon(Icons.Default.UploadFile, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(4.dp))
                Text("Import a list")
            }
        }
        error?.let { Text(it, color = SentinelRed, style = MaterialTheme.typography.bodySmall) }
        val list = jobs
        when {
            list == null -> if (error == null) Box(Modifier.fillMaxWidth().padding(8.dp)) { CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) }
            list.isEmpty() -> Text(
                "Got a ${if (api.kind == "movies") "Letterboxd or IMDb" else "Spotify"} list? Import a list downloads everything on it.",
                style = MaterialTheme.typography.bodyMedium,
                color = SentinelTextDim,
            )
            else -> list.forEach { job ->
                Card(Modifier.fillMaxWidth().clickable { onOpen(job.id) }) {
                    Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text(job.name, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                            DownloadTag(MediaImportFormat.plural(job.counts.total, words.thing, words.things))
                        }
                        JobSummary(job)
                        JobButtons(
                            job,
                            busy = busyId == job.id,
                            onDetails = { onOpen(job.id) },
                            onPause = { paused -> act(job.id) { api.pause(job.id, paused) } },
                            onRetry = { act(job.id) { api.retry(job.id) } },
                            onRemove = { confirmRemove = job },
                        )
                    }
                }
            }
        }
    }

    confirmRemove?.let { job ->
        ConfirmDialog(
            title = "Remove this list?",
            text = REMOVE_TEXT,
            confirmLabel = "Remove list",
            onConfirm = {
                confirmRemove = null
                act(job.id) { api.remove(job.id) }
            },
            onDismiss = { confirmRemove = null },
        )
    }
}

// A one-line way into the import from an Add screen, as the web's "Have a … list? Import a list".
@Composable
fun ImportListLink(kind: String, onImport: () -> Unit, modifier: Modifier = Modifier) {
    Row(modifier, verticalAlignment = Alignment.CenterVertically) {
        Text(
            if (kind == "movies") "Have a Letterboxd or IMDb watchlist?" else "Have a whole Spotify library or playlist?",
            style = MaterialTheme.typography.bodySmall,
            color = SentinelTextDim,
            modifier = Modifier.weight(1f, fill = false),
        )
        TextButton(onClick = onImport) { Text("Import a list") }
    }
}

// ---------------- One list's details ----------------

@Composable
fun MediaImportDetailScreen(api: MediaImportApi, id: String, onBack: () -> Unit) {
    val scope = rememberCoroutineScope()
    var job by remember { mutableStateOf<MediaImportJob?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var actionError by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }
    var filter by remember { mutableStateOf("all") }
    var confirmRemove by remember { mutableStateOf(false) }
    var tick by remember { mutableStateOf(0) }

    LaunchedEffect(id, tick) {
        while (true) {
            runCatching { api.get(id) }
                .onSuccess { job = it; error = null }
                .onFailure { error = it.message ?: "Could not load the list." }
            if (job?.finished == true || (job == null && error != null)) break
            delay(DETAIL_POLL_MS)
        }
    }

    fun act(block: suspend () -> MediaImportJob?) {
        scope.launch {
            busy = true
            actionError = null
            runCatching { block() }
                .onSuccess { updated -> updated?.let { job = it } }
                .onFailure { actionError = it.message ?: "That didn't work." }
            busy = false
            tick++
        }
    }

    val current = job
    Column(Modifier.fillMaxSize()) {
        DetailHeader(current?.name ?: "Imported list", onBack) {
            IconButton(onClick = { tick++ }) { Icon(Icons.Default.Refresh, contentDescription = "Refresh") }
        }
        if (current == null) {
            if (error != null) ErrorPanel(error.orEmpty(), onRetry = { tick++ }, modifier = Modifier.fillMaxWidth())
            else Box(Modifier.fillMaxWidth().padding(32.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            return@Column
        }
        val shown = current.items.filter { MediaImportFormat.inFilter(filter, it.state) }
        LazyColumn(
            Modifier.weight(1f).fillMaxWidth(),
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            item { JobSummary(current) }
            item {
                JobButtons(
                    current,
                    busy = busy,
                    onDetails = null,
                    onPause = { paused -> act { api.pause(current.id, paused) } },
                    onRetry = { act { api.retry(current.id) } },
                    onRemove = { confirmRemove = true },
                )
            }
            actionError?.let { message -> item { Text(message, color = SentinelRed) } }
            item {
                Row(
                    Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(vertical = 4.dp),
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    MediaImportFormat.DETAIL_FILTERS.forEach { (value, label) ->
                        FilterChip(selected = filter == value, onClick = { filter = value }, label = { Text(label) })
                    }
                }
            }
            if (shown.isEmpty()) {
                item { Text("Nothing here.", color = SentinelTextDim, modifier = Modifier.padding(vertical = 12.dp)) }
            }
            items(shown.take(MediaImportFormat.SHOWN_AT_ONCE * 2), key = { it.id.ifBlank { it.title + it.artist } }) { item ->
                ImportItemRow(item, enabled = !busy) { act { api.skip(current.id, item.id) } }
            }
        }
    }

    if (confirmRemove) {
        ConfirmDialog(
            title = "Remove this list?",
            text = REMOVE_TEXT,
            confirmLabel = "Remove list",
            onConfirm = {
                confirmRemove = false
                val target = job ?: return@ConfirmDialog
                scope.launch {
                    busy = true
                    runCatching { api.remove(target.id) }
                        .onSuccess { onBack() }
                        .onFailure { actionError = it.message ?: "Could not remove the list." }
                    busy = false
                }
            },
            onDismiss = { confirmRemove = false },
        )
    }
}

@Composable
private fun ImportItemRow(item: MediaImportItem, enabled: Boolean, onSkip: () -> Unit) {
    val stateColor = when (item.state) {
        "done", "inLibrary" -> SentinelGreen
        "notFound", "failed" -> SentinelRed
        "canceled" -> SentinelTextDim
        else -> MaterialTheme.colorScheme.primary
    }
    Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(item.title, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                item.year?.let { DownloadTag(it.toString()) }
            }
            item.artist?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim, maxLines = 1, overflow = TextOverflow.Ellipsis) }
            item.releaseTitle?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim, maxLines = 1, overflow = TextOverflow.Ellipsis) }
            item.message?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = SentinelAmber, maxLines = 2, overflow = TextOverflow.Ellipsis) }
            if (item.state == "downloading" && item.progress != null) {
                LinearProgressIndicator(
                    progress = { item.progress.toFloat().coerceIn(0f, 1f) },
                    modifier = Modifier.fillMaxWidth().height(3.dp),
                )
            }
        }
        Spacer(Modifier.width(8.dp))
        Text(MediaImportFormat.itemStateLabel(item), style = MaterialTheme.typography.labelMedium, color = stateColor, fontWeight = FontWeight.SemiBold)
        if (MediaImportFormat.canSkip(item.state)) {
            IconButton(onClick = onSkip, enabled = enabled) { Icon(Icons.Default.Close, contentDescription = "Leave this one out") }
        }
    }
}

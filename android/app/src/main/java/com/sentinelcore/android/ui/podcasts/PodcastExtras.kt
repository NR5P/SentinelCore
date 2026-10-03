package com.sentinelcore.android.ui.podcasts

import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Bookmarks
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentCut
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.NoteAdd
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Podcasts
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Share
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.Bookmark
import com.sentinelcore.android.audio.Clip
import com.sentinelcore.android.audio.Episode
import com.sentinelcore.android.audio.PlayContext
import com.sentinelcore.android.ui.audio.ArtImage
import com.sentinelcore.android.ui.audio.AudioUi
import com.sentinelcore.android.ui.audio.EmptyPanel
import com.sentinelcore.android.ui.audio.HeroHeader
import com.sentinelcore.android.ui.audio.LoadableContent
import com.sentinelcore.android.ui.audio.LocalAudioUi
import com.sentinelcore.android.ui.audio.EpisodeRow
import com.sentinelcore.android.ui.audio.rememberLoadable
import com.sentinelcore.android.ui.music.SwitchLine
import com.sentinelcore.android.ui.screens.DetailHeader
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

// ---------------- OPML ----------------

class OpmlActions(val import: () -> Unit, val export: () -> Unit)

// Import picks an .opml with the system file picker (SAF); export hands the file to the share
// sheet. Both need no storage permission.
@Composable
fun rememberOpmlActions(): OpmlActions {
    val ui = LocalAudioUi.current
    val context = LocalContext.current
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        uri ?: return@rememberLauncherForActivityResult
        ui.launch("Couldn't import that file.") {
            val xml = withContext(Dispatchers.IO) { context.contentResolver.openInputStream(uri)?.use { it.readBytes().toString(Charsets.UTF_8) } }
                ?: throw java.io.IOException("Couldn't read that file.")
            if (!xml.contains("<opml", ignoreCase = true)) throw java.io.IOException("That isn't an OPML file.")
            val result = ui.api.opmlImport(xml)
            ui.message(
                buildString {
                    append(if (result.added == 1) "Following 1 new show" else "Following ${result.added} new shows")
                    if (result.existing > 0) append(", ${result.existing} already followed")
                    if (result.failed.isNotEmpty()) append(", ${result.failed.size} couldn't be added")
                    append(". New ones fill in as their feeds are read.")
                },
            )
            delay(5_000)
            ui.libraryVersion++
        }
    }
    return remember(ui) {
        OpmlActions(
            import = { picker.launch(arrayOf("text/x-opml", "text/xml", "application/xml", "application/octet-stream", "*/*")) },
            export = {
                ui.launch("Couldn't export.") {
                    val opml = ui.api.opmlExport()
                    val file = withContext(Dispatchers.IO) {
                        File(context.cacheDir, "audio-share").apply { mkdirs() }.let { File(it, "Sentinel Core podcasts.opml") }.also { it.writeText(opml) }
                    }
                    share(context, file, "text/x-opml", "Export podcasts")
                }
            },
        )
    }
}

fun share(context: Context, file: File, mime: String, title: String) {
    val uri: Uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
    context.startActivity(
        Intent.createChooser(
            Intent(Intent.ACTION_SEND).setType(mime).putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),
            title,
        ),
    )
}

// ---------------- add by URL, fill my drive ----------------

@Composable
fun AddByUrlDialog(onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    var url by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Add a show by its feed") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("Paste the RSS feed address. Private feeds (Patreon, Supercast) work too.", style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(value = url, onValueChange = { url = it }, placeholder = { Text("https://example.com/feed.xml") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            }
        },
        confirmButton = {
            TextButton(enabled = url.isNotBlank(), onClick = {
                val feed = url.trim()
                onDismiss()
                ui.nav.openPreview(feed, null)
            }) { Text("Look") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

// "Fill my drive" (§6 fill): pick how long the trip is, get episodes that fit, play them.
@Composable
fun FillMyDriveDialog(onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    var minutes by remember { mutableFloatStateOf(30f) }
    var preview by remember { mutableStateOf<com.sentinelcore.android.audio.FillResult?>(null) }
    LaunchedEffect(minutes.toInt()) {
        delay(300)
        preview = runCatching { ui.api.fill(minutes.toInt()) }.getOrNull()
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Fill my drive") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("${minutes.toInt()} minutes", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
                Slider(value = minutes, onValueChange = { minutes = (it / 5).toInt() * 5f }, valueRange = 5f..180f)
                val fill = preview
                if (fill == null) LinearProgressIndicator(Modifier.fillMaxWidth()) else if (fill.episodes.isEmpty()) {
                    Text("Nothing unplayed fits in that time.", style = MaterialTheme.typography.bodySmall)
                } else {
                    Text("${fill.episodes.size} episodes, ${AudioFormat.duration(fill.totalSeconds)}:", style = MaterialTheme.typography.bodySmall)
                    fill.episodes.take(5).forEach {
                        Text("• ${it.title}", style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    if (fill.episodes.size > 5) Text("and ${fill.episodes.size - 5} more", style = MaterialTheme.typography.bodySmall)
                }
            }
        },
        confirmButton = {
            TextButton(enabled = preview?.episodes?.isNotEmpty() == true, onClick = {
                preview?.let { ui.engine.playEpisodes(it.episodes, 0, PlayContext("queue", null, "Fill my drive")) }
                onDismiss()
                ui.nowPlayingOpen = true
            }) { Text("Play") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

// ---------------- bookmarks ----------------

@Composable
fun BookmarkRow(bookmark: Bookmark, onChanged: () -> Unit = {}) {
    val ui = LocalAudioUi.current
    var editing by remember { mutableStateOf(false) }
    Row(
        Modifier.fillMaxWidth().clickable { playAt(ui, bookmark.episodeId, bookmark.positionSeconds) }.padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.Top,
    ) {
        ArtImage(bookmark.artUrl, Modifier.size(48.dp), sizePx = 144, fallbackIcon = Icons.Default.Podcasts, seed = bookmark.podcastTitle)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text("${AudioFormat.clock(bookmark.positionSeconds)} · ${bookmark.podcastTitle}", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary)
            Text(bookmark.episodeTitle, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (bookmark.note.isNotBlank()) Text(bookmark.note, style = MaterialTheme.typography.bodyMedium)
            bookmark.transcriptSnippet?.let { Text("“$it”", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 3, overflow = TextOverflow.Ellipsis) }
            Row(horizontalArrangement = Arrangement.spacedBy(2.dp)) {
                TextButton(onClick = {
                    ui.launch("Couldn't save it to a note. Is the Organizer on?") {
                        ui.api.bookmarkToNote(bookmark.id)
                        ui.message("Saved to your Organizer notes")
                    }
                }) {
                    Icon(Icons.Default.NoteAdd, contentDescription = null, modifier = Modifier.size(16.dp))
                    Spacer(Modifier.width(4.dp))
                    Text("Save to Note")
                }
                IconButton(onClick = { editing = true }) { Icon(Icons.Default.Edit, contentDescription = "Edit note", modifier = Modifier.size(18.dp)) }
                IconButton(onClick = {
                    ui.launch("Couldn't delete it.") { ui.api.deleteBookmark(bookmark.id); ui.libraryVersion++; onChanged() }
                }) { Icon(Icons.Default.Delete, contentDescription = "Delete bookmark", modifier = Modifier.size(18.dp)) }
            }
        }
    }
    if (editing) {
        var note by remember { mutableStateOf(bookmark.note) }
        AlertDialog(
            onDismissRequest = { editing = false },
            title = { Text("Bookmark note") },
            text = { OutlinedTextField(value = note, onValueChange = { note = it }, modifier = Modifier.fillMaxWidth()) },
            confirmButton = {
                TextButton(onClick = {
                    editing = false
                    ui.launch("Couldn't save the note.") { ui.api.updateBookmark(bookmark.id, note.trim()); ui.libraryVersion++; onChanged() }
                }) { Text("Save") }
            },
            dismissButton = { TextButton(onClick = { editing = false }) { Text("Cancel") } },
        )
    }
}

// Plays an episode from a moment: the one playing just seeks, anything else loads first.
fun playAt(ui: AudioUi, episodeId: String, seconds: Double) {
    val current = ui.engine.state.value.current
    if (current?.isEpisode == true && current.id == episodeId) {
        ui.engine.seekTo(seconds)
        ui.engine.resume()
        return
    }
    ui.launch("Couldn't open that episode.") {
        val episode = ui.api.episode(episodeId)
        ui.playEpisode(episode, startAt = seconds)
    }
}

@Composable
fun BookmarksScreen() {
    val ui = LocalAudioUi.current
    val bookmarks = rememberLoadable(Unit) { ui.api.bookmarks() }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Bookmarks", onBack = ui.nav.back)
        LoadableContent(bookmarks) { list ->
            if (list.isEmpty()) {
                EmptyPanel(Icons.Default.Bookmarks, "No bookmarks yet", "Tap Bookmark in Now Playing to save a moment, with a note and the words around it.")
                return@LoadableContent
            }
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                list.groupBy { it.podcastTitle }.forEach { (show, marks) ->
                    item(key = "h$show") { Text(show, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(start = 16.dp, top = 16.dp)) }
                    items(marks, key = { it.id }) { bookmark -> BookmarkRow(bookmark) { bookmarks.reload() } }
                }
            }
        }
    }
}

// ---------------- transcript search ----------------

@Composable
fun TranscriptSearchScreen() {
    val ui = LocalAudioUi.current
    var query by rememberSaveable { mutableStateOf("") }
    val hits = rememberLoadable(query.trim()) {
        val q = query.trim()
        if (q.length < 3) null else {
            delay(400)
            ui.api.searchTranscripts(q)
        }
    }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Search what was said", onBack = ui.nav.back)
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            placeholder = { Text("Words from any transcript") },
            leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
            trailingIcon = { if (query.isNotEmpty()) IconButton(onClick = { query = "" }) { Icon(Icons.Default.Close, contentDescription = "Clear") } },
            singleLine = true,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        )
        val list = hits.value
        when {
            query.trim().length < 3 -> EmptyPanel(Icons.Default.Search, "Search inside episodes", "Find the episode where someone said it - every transcript on the server is searched, and a result plays from that moment.")
            hits.error != null -> Text(hits.error.orEmpty(), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp))
            list == null -> LinearProgressIndicator(Modifier.fillMaxWidth().padding(16.dp))
            list.isEmpty() -> EmptyPanel(Icons.Default.Search, "Not found", "No transcript mentions \"${query.trim()}\".")
            else -> LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                items(list, key = { it.episodeId + it.startSeconds }) { hit ->
                    Column(Modifier.fillMaxWidth().clickable { playAt(ui, hit.episodeId, hit.startSeconds) }.padding(horizontal = 16.dp, vertical = 10.dp)) {
                        Text("${hit.podcastTitle} · ${AudioFormat.clock(hit.startSeconds)}", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary)
                        Text(hit.episodeTitle, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(highlight(hit.snippet, query.trim()), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    HorizontalDivider(Modifier.padding(horizontal = 16.dp))
                }
            }
        }
    }
}

// The searched words in bold inside a snippet.
@Composable
fun highlight(text: String, query: String) = buildAnnotatedString {
    val words = query.lowercase().split(Regex("\\s+")).filter { it.length > 1 }
    var index = 0
    val lower = text.lowercase()
    while (index < text.length) {
        val next = words.mapNotNull { word -> lower.indexOf(word, index).takeIf { it >= 0 }?.let { it to word } }.minByOrNull { it.first }
        if (next == null) {
            append(text.substring(index))
            break
        }
        append(text.substring(index, next.first))
        withStyle(SpanStyle(fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSurface)) { append(text.substring(next.first, next.first + next.second.length)) }
        index = next.first + next.second.length
    }
}

// ---------------- clips ----------------

@Composable
fun ClipsScreen() {
    val ui = LocalAudioUi.current
    val context = LocalContext.current
    val clips = rememberLoadable(Unit) { ui.api.clips() }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Clips", onBack = ui.nav.back)
        LoadableContent(clips) { list ->
            if (list.isEmpty()) {
                EmptyPanel(Icons.Default.ContentCut, "No clips yet", "Make one from an episode's page or Now Playing's menu, and share it as an audio file.")
                return@LoadableContent
            }
            LazyColumn(Modifier.fillMaxSize()) {
                items(list, key = { it.id }) { clip ->
                    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Default.ContentCut, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f)) {
                            Text(clip.title?.ifBlank { null } ?: "Clip", fontWeight = FontWeight.Medium)
                            Text(listOfNotNull(AudioFormat.clock(clip.durationSeconds), AudioFormat.day(clip.createdAt).ifBlank { null }).joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        IconButton(onClick = { shareClip(ui, context, clip) }) { Icon(Icons.Default.Share, contentDescription = "Share") }
                        IconButton(onClick = { ui.launch("Couldn't delete it.") { ui.api.deleteClip(clip.id); clips.reload() } }) { Icon(Icons.Default.Delete, contentDescription = "Delete") }
                    }
                }
            }
        }
    }
}

// Fetches the clip's m4a and offers it to the share sheet.
fun shareClip(ui: AudioUi, context: Context, clip: Clip, name: String? = null) {
    ui.launch("Couldn't fetch the clip.") {
        val file = withContext(Dispatchers.IO) {
            val dir = File(context.cacheDir, "audio-share").apply { mkdirs() }
            val safe = (name ?: clip.title ?: "Clip").replace(Regex("[^A-Za-z0-9 ._-]"), "").trim().ifBlank { "Clip" }.take(60)
            val target = File(dir, "$safe.m4a")
            val connection = ui.runtime.openConnection(clip.url)
            try {
                if (connection.responseCode !in 200..299) throw java.io.IOException("The server said HTTP ${connection.responseCode}.")
                connection.inputStream.use { input -> target.outputStream().use { input.copyTo(it) } }
            } finally {
                connection.disconnect()
            }
            target
        }
        share(context, file, "audio/mp4", "Share clip")
    }
}

// The clip maker: a start and a length (5-180 s), preview it, then the server cuts it with ffmpeg.
@Composable
fun ClipMakerScreen(episodeId: String) {
    val ui = LocalAudioUi.current
    val context = LocalContext.current
    val episode = rememberLoadable(episodeId) { ui.api.episode(episodeId) }
    val state by ui.engine.state.collectAsState()
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Make a clip", onBack = ui.nav.back)
        LoadableContent(episode) { value ->
            val playingThis = state.current?.isEpisode == true && state.current?.id == value.id
            var start by remember { mutableFloatStateOf(if (playingThis) (ui.engine.currentPositionSeconds() - 15).coerceAtLeast(0.0).toFloat() else (value.progress?.positionSeconds ?: 0.0).toFloat()) }
            var length by remember { mutableFloatStateOf(30f) }
            var title by remember { mutableStateOf("") }
            var making by remember { mutableStateOf(false) }
            val duration = value.durationSeconds.coerceAtLeast(1.0).toFloat()
            Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                HeroHeader(
                    art = { ArtImage(value.artUrl, Modifier.fillMaxSize(), sizePx = 500, fallbackIcon = Icons.Default.Podcasts, seed = value.podcastTitle) },
                    title = value.title,
                    subtitle = value.podcastTitle,
                    detail = null,
                )
                Text("Starts at ${AudioFormat.clock(start.toDouble())}", style = MaterialTheme.typography.titleMedium)
                Slider(value = start.coerceIn(0f, duration), onValueChange = { start = it }, valueRange = 0f..duration)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { start = (start - 5).coerceAtLeast(0f) }) { Text("-5 s") }
                    OutlinedButton(onClick = { start = (start + 5).coerceAtMost(duration) }) { Text("+5 s") }
                    if (playingThis) OutlinedButton(onClick = { start = ui.engine.currentPositionSeconds().toFloat() }) { Text("Now") }
                }
                Text("${length.toInt()} seconds long", style = MaterialTheme.typography.titleMedium)
                Slider(value = length, onValueChange = { length = it.toInt().toFloat() }, valueRange = 5f..180f)
                OutlinedTextField(value = title, onValueChange = { title = it }, label = { Text("Title (optional)") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                FilledTonalButton(onClick = {
                    // Plays the clip's stretch here, then pauses.
                    playAt(ui, value.id, start.toDouble())
                    ui.scope.launchPreviewStop(ui, value.id, start.toDouble() + length)
                }, modifier = Modifier.fillMaxWidth()) {
                    Icon(Icons.Default.PlayArrow, contentDescription = null)
                    Text("Preview")
                }
                Button(enabled = !making, onClick = {
                    making = true
                    ui.launch("The server couldn't cut the clip.") {
                        try {
                            val clip = ui.api.makeClip(value.id, start.toDouble(), length.toDouble(), title.trim())
                            ui.message("Clip made")
                            shareClip(ui, context, clip, title.trim().ifBlank { "${value.podcastTitle} clip" })
                        } finally {
                            making = false
                        }
                    }
                }, modifier = Modifier.fillMaxWidth()) {
                    if (making) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp) else Icon(Icons.Default.Share, contentDescription = null)
                    Spacer(Modifier.width(8.dp))
                    Text(if (making) "Cutting…" else "Make and share")
                }
                Text("The server cuts it from the episode and keeps it under Clips.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

private fun kotlinx.coroutines.CoroutineScope.launchPreviewStop(ui: AudioUi, episodeId: String, endSeconds: Double) {
    launch {
        delay(1_500)
        while (true) {
            val current = ui.engine.state.value.current
            if (current?.id != episodeId || !ui.engine.state.value.playing) return@launch
            if (ui.engine.currentPositionSeconds() >= endSeconds) {
                ui.engine.pause()
                return@launch
            }
            delay(250)
        }
    }
}

// ---------------- preview before following ----------------

@Composable
fun PodcastPreviewScreen(feedUrl: String?, itunesId: Long?) {
    val ui = LocalAudioUi.current
    val preview = rememberLoadable(feedUrl, itunesId) { ui.api.preview(feedUrl, itunesId) }
    var following by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize()) {
        DetailHeader(preview.value?.podcast?.title ?: "Podcast", onBack = ui.nav.back)
        LoadableContent(preview) { value ->
            val podcast = value.podcast
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                item {
                    HeroHeader(
                        art = { ArtImage(podcast.artUrl, Modifier.fillMaxSize(), sizePx = 700, fallbackIcon = Icons.Default.Podcasts, seed = podcast.title) },
                        title = podcast.title,
                        subtitle = podcast.author,
                        detail = listOfNotNull(podcast.categories.firstOrNull(), "${value.episodes.size} episodes", "Explicit".takeIf { podcast.explicit }).joinToString(" · "),
                    )
                }
                item {
                    Button(
                        enabled = !following,
                        onClick = {
                            following = true
                            ui.launch("Couldn't follow it.") {
                                try {
                                    val subscribed = ui.api.subscribe(feedUrl ?: podcast.feedUrl.ifBlank { null }, if (feedUrl == null && podcast.feedUrl.isBlank()) itunesId else null)
                                    ui.libraryVersion++
                                    ui.message("Following ${subscribed.title}")
                                    ui.nav.back()
                                    ui.nav.openShow(subscribed.id)
                                } finally {
                                    following = false
                                }
                            }
                        },
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                    ) { Text(if (following) "Following…" else "Follow") }
                }
                if (podcast.description.isNotBlank()) {
                    item { Text(podcast.description, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(16.dp), maxLines = 8, overflow = TextOverflow.Ellipsis) }
                }
                item { Text("Latest episodes", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(start = 16.dp, top = 8.dp)) }
                items(value.episodes.take(30), key = { it.id }) { episode -> PreviewEpisodeRow(episode) }
            }
        }
    }
}

// An episode from a feed not followed yet: no progress, plays straight from the server's relay.
@Composable
private fun PreviewEpisodeRow(episode: Episode) {
    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
        Text(listOf(AudioFormat.day(episode.publishedAt), AudioFormat.duration(episode.durationSeconds)).filter { it.isNotBlank() }.joinToString(" · "), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(episode.title, fontWeight = FontWeight.Medium, maxLines = 2, overflow = TextOverflow.Ellipsis)
        if (episode.summary.isNotBlank()) Text(episode.summary, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2, overflow = TextOverflow.Ellipsis)
    }
}


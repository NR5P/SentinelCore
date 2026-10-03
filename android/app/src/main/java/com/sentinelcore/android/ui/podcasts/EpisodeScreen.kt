package com.sentinelcore.android.ui.podcasts

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
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
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.QueueMusic
import androidx.compose.material.icons.filled.BookmarkAdd
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.CloudDone
import androidx.compose.material.icons.filled.CloudDownload
import androidx.compose.material.icons.filled.ContentCut
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.DownloadDone
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Podcasts
import androidx.compose.material.icons.filled.Subject
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.fromHtml
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.AudioItem
import com.sentinelcore.android.audio.Episode
import com.sentinelcore.android.audio.TimedText
import com.sentinelcore.android.ui.audio.ArtImage
import com.sentinelcore.android.ui.audio.BookmarkDialog
import com.sentinelcore.android.ui.audio.LoadableContent
import com.sentinelcore.android.ui.audio.LocalAudioUi
import com.sentinelcore.android.ui.audio.SectionTitle
import com.sentinelcore.android.ui.audio.rememberLoadable
import com.sentinelcore.android.ui.screens.DetailHeader

// An episode's page (§5-§6): play or resume, Up Next, downloads, the show notes (sanitized HTML,
// links work), chapters, the transcript, bookmarks, Transcribe and the clip maker.
@Composable
fun EpisodeScreen(episodeId: String) {
    val ui = LocalAudioUi.current
    val episode = rememberLoadable(episodeId) { ui.api.episode(episodeId) }
    Column(Modifier.fillMaxSize()) {
        DetailHeader(episode.value?.podcastTitle ?: "Episode", onBack = ui.nav.back)
        LoadableContent(episode) { EpisodeContent(it) { changed -> episode.value = changed } }
    }
}

@Composable
private fun EpisodeContent(episode: Episode, onChanged: (Episode) -> Unit) {
    val ui = LocalAudioUi.current
    val state by ui.engine.state.collectAsState()
    val progress by ui.engine.progress.collectAsState()
    val downloads by ui.runtime.downloads.entries.collectAsState()
    val isCurrent = state.current?.isEpisode == true && state.current?.id == episode.id
    val local = downloads.firstOrNull { it.type == AudioItem.TYPE_EPISODE && it.id == episode.id }
    var showTranscript by remember { mutableStateOf(false) }
    var transcriptQuery by remember { mutableStateOf("") }
    var bookmarking by remember { mutableStateOf(false) }
    val chapters = rememberLoadable(episode.id, episode.hasChapters) { if (episode.hasChapters) ui.api.chapters(episode.id) else emptyList() }
    val bookmarks = rememberLoadable(episode.id) { ui.api.bookmarks(episode.id) }
    val transcript = rememberLoadable(episode.id, showTranscript) { if (showTranscript) ui.api.transcript(episode.id) else null }
    val position = if (isCurrent) progress.positionSeconds else episode.resumeSeconds

    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 32.dp)) {
        item {
            Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.Top) {
                ArtImage(episode.artUrl, Modifier.size(112.dp), sizePx = 336, fallbackIcon = Icons.Default.Podcasts, seed = episode.podcastTitle)
                Spacer(Modifier.width(14.dp))
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(
                        episode.podcastTitle,
                        color = MaterialTheme.colorScheme.primary,
                        style = MaterialTheme.typography.labelLarge,
                        modifier = Modifier.clickable(enabled = episode.podcastId.isNotBlank()) { ui.nav.openShow(episode.podcastId) },
                    )
                    Text(episode.title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                    Text(
                        listOfNotNull(
                            AudioFormat.day(episode.publishedAt).ifBlank { null },
                            AudioFormat.duration(episode.durationSeconds),
                            episode.season?.let { s -> "Season $s" + (episode.episode?.let { ", episode $it" } ?: "") } ?: episode.episode?.let { "Episode $it" },
                            episode.episodeType.takeIf { it != "full" }?.replaceFirstChar(Char::uppercase),
                            "Explicit".takeIf { episode.explicit },
                        ).joinToString(" · "),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    if (episode.archived) Text("No longer in the feed - kept on the server", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.tertiary)
                }
            }
        }
        item {
            val total = episode.progress?.durationSeconds?.takeIf { it > 0 } ?: episode.durationSeconds
            if (position > 0 && total > 0 && !episode.played) {
                Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp)) {
                    LinearProgressIndicator(progress = { (position / total).toFloat().coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth().height(4.dp).clip(RoundedCornerShape(2.dp)))
                    Text(AudioFormat.minutesLeft(total - position), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp))
                }
            }
        }
        item {
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Button(onClick = { if (isCurrent) ui.engine.togglePlay() else ui.playEpisode(episode) }, modifier = Modifier.weight(1f)) {
                    Icon(if (isCurrent && state.playing) Icons.Default.Pause else Icons.Default.PlayArrow, contentDescription = null)
                    Spacer(Modifier.width(6.dp))
                    Text(
                        when {
                            isCurrent && state.playing -> "Pause"
                            episode.played -> "Play again"
                            position > 0 -> "Resume"
                            else -> "Play"
                        },
                    )
                }
                FilledTonalButton(onClick = {
                    ui.launch {
                        if (episode.inQueue) ui.api.dequeue(episode.id) else ui.api.enqueue(episode.id, "last")
                        onChanged(episode.copy(inQueue = !episode.inQueue))
                        ui.libraryVersion++
                    }
                }, modifier = Modifier.weight(1f)) {
                    Icon(if (episode.inQueue) Icons.Default.Check else Icons.AutoMirrored.Filled.QueueMusic, contentDescription = null)
                    Spacer(Modifier.width(6.dp))
                    Text(if (episode.inQueue) "In Up Next" else "Up Next")
                }
            }
        }
        item {
            Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), horizontalArrangement = Arrangement.SpaceEvenly) {
                ActionButton(if (episode.played) Icons.Default.Check else Icons.Default.Check, if (episode.played) "Played" else "Mark played", episode.played) {
                    ui.launch {
                        val result = ui.api.setEpisodePlayed(episode.id, !episode.played)
                        onChanged(episode.copy(progress = result ?: episode.progress?.copy(played = !episode.played)))
                        ui.libraryVersion++
                    }
                }
                ActionButton(
                    when (local?.state) {
                        "done" -> Icons.Default.DownloadDone
                        else -> Icons.Default.Download
                    },
                    when (local?.state) {
                        "done" -> "On phone"
                        "downloading" -> "${(local.progress * 100).toInt()}%"
                        "queued" -> "Waiting"
                        "failed" -> "Retry"
                        else -> "Download"
                    },
                    local?.done == true,
                ) {
                    when {
                        local?.done == true -> ui.runtime.downloads.remove(AudioItem.TYPE_EPISODE, episode.id)
                        local?.state == "failed" -> ui.runtime.downloads.retry(local)
                        local == null -> ui.runtime.downloads.downloadEpisode(episode)
                    }
                }
                val server = episode.download?.state
                ActionButton(
                    if (server == "done") Icons.Default.CloudDone else Icons.Default.CloudDownload,
                    when (server) {
                        "done" -> if (episode.download?.enhanced == true) "Enhanced" else "On server"
                        "downloading" -> "${((episode.download?.progress ?: 0.0) * 100).toInt()}%"
                        "queued" -> "Queued"
                        "failed" -> "Failed"
                        else -> "Keep"
                    },
                    server == "done",
                ) {
                    if (server != "done" && server != "downloading") {
                        ui.launch("Couldn't start the download.") { onChanged(ui.api.downloadEpisodeOnServer(episode.id)) }
                    }
                }
                ActionButton(Icons.Default.BookmarkAdd, "Bookmark", false) { bookmarking = true }
                ActionButton(Icons.Default.ContentCut, "Clip", false) { ui.nav.open("podcasts/clip/${episode.id}") }
            }
        }
        episode.download?.takeIf { it.timeSavedSeconds > 1 }?.let { download ->
            item {
                Text(
                    "Trimmed silences save ${AudioFormat.duration(download.timeSavedSeconds)} on this one.",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.tertiary,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                )
            }
        }
        item { HorizontalDivider(Modifier.padding(vertical = 8.dp)) }

        // Show notes: the server already sanitized it to a small set of tags.
        item {
            val html = episode.description
            val notes: AnnotatedString = remember(html) {
                if (html.isNullOrBlank()) AnnotatedString(episode.descriptionText ?: episode.summary)
                else AnnotatedString.fromHtml(html, linkStyles = TextLinkStyles(style = SpanStyle(textDecoration = TextDecoration.Underline)))
            }
            Text(notes, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(horizontal = 16.dp))
        }

        val chapterList = chapters.value.orEmpty()
        if (chapterList.isNotEmpty()) {
            item { SectionTitle("Chapters") }
            val active = if (isCurrent) TimedText.activeChapter(chapterList, progress.positionSeconds) else -1
            itemsIndexed(chapterList, key = { index, _ -> "ch$index" }) { index, chapter ->
                Row(
                    Modifier.fillMaxWidth().clickable { playAt(ui, episode.id, chapter.startSeconds) }.padding(horizontal = 16.dp, vertical = 10.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(AudioFormat.clock(chapter.startSeconds), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary, modifier = Modifier.width(64.dp))
                    Text(chapter.title, fontWeight = if (index == active) FontWeight.Bold else FontWeight.Normal, modifier = Modifier.weight(1f), maxLines = 2, overflow = TextOverflow.Ellipsis)
                }
            }
        }

        item { SectionTitle("Transcript") }
        if (!showTranscript) {
            item {
                Row(Modifier.padding(horizontal = 8.dp)) {
                    if (episode.hasTranscript) {
                        TextButton(onClick = { showTranscript = true }) {
                            Icon(Icons.Default.Subject, contentDescription = null)
                            Spacer(Modifier.width(6.dp))
                            Text("Read the transcript" + (episode.transcriptSource?.let { if (it == "whisper") " (made on the server)" else "" } ?: ""))
                        }
                    } else {
                        TextButton(onClick = {
                            ui.launch("Transcripts aren't set up on the server.") {
                                ui.api.transcribe(episode.id)
                                ui.message("Transcribing on the server - it'll appear here when it's done")
                            }
                        }) { Text("Transcribe this episode") }
                    }
                }
            }
        } else {
            val segments = transcript.value?.segments
            when {
                transcript.loading && segments == null -> item { CircularProgressIndicator(Modifier.padding(16.dp)) }
                segments.isNullOrEmpty() -> item { Text(transcript.error ?: "No transcript yet.", modifier = Modifier.padding(16.dp)) }
                else -> {
                    item {
                        OutlinedTextField(
                            value = transcriptQuery,
                            onValueChange = { transcriptQuery = it },
                            placeholder = { Text("Search the transcript") },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                        )
                    }
                    val shown = if (transcriptQuery.isBlank()) segments.indices.toList() else TimedText.search(segments, transcriptQuery)
                    val active = if (isCurrent) TimedText.activeSegment(segments, progress.positionSeconds) else -1
                    items(shown.take(800), key = { "seg$it" }) { index ->
                        val segment = segments[index]
                        Row(
                            Modifier.fillMaxWidth().clickable { playAt(ui, episode.id, segment.start) }.padding(horizontal = 16.dp, vertical = 5.dp),
                        ) {
                            Text(AudioFormat.clock(segment.start), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.width(52.dp).padding(top = 2.dp))
                            Column {
                                segment.speaker?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary) }
                                Text(segment.text, fontWeight = if (index == active) FontWeight.Bold else FontWeight.Normal, style = MaterialTheme.typography.bodyMedium)
                            }
                        }
                    }
                }
            }
        }

        val marks = bookmarks.value.orEmpty()
        if (marks.isNotEmpty()) {
            item { SectionTitle("Your bookmarks") }
            items(marks, key = { "bm" + it.id }) { bookmark -> BookmarkRow(bookmark) { bookmarks.reload() } }
        }
    }
    if (bookmarking) {
        BookmarkDialog(episode.id, if (isCurrent) ui.engine.currentPositionSeconds() else position, onDismiss = { bookmarking = false; bookmarks.reload() })
    }
}

@Composable
private fun ActionButton(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, active: Boolean, onClick: () -> Unit) {
    Column(
        Modifier.clip(RoundedCornerShape(10.dp)).clickable(onClick = onClick).padding(horizontal = 8.dp, vertical = 6.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(icon, contentDescription = label, tint = if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
        Text(label, style = MaterialTheme.typography.labelSmall, color = if (active) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
    }
}


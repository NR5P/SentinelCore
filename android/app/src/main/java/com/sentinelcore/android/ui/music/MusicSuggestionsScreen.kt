package com.sentinelcore.android.ui.music

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.MusicSuggestedAlbum
import com.sentinelcore.android.audio.MusicSuggestions
import com.sentinelcore.android.audio.MusicSuggestionsFormat
import com.sentinelcore.android.ui.audio.ArtImage
import com.sentinelcore.android.ui.audio.AudioRoutes
import com.sentinelcore.android.ui.audio.AudioUi
import com.sentinelcore.android.ui.audio.EmptyPanel
import com.sentinelcore.android.ui.audio.LocalAudioUi
import com.sentinelcore.android.ui.audio.SectionTitle
import com.sentinelcore.android.ui.audio.Shelf
import com.sentinelcore.android.ui.screens.DetailHeader
import kotlinx.coroutines.delay

// "Suggested for you" (core/docs/MUSIC_AND_PODCASTS.md "Suggestions"): a shelf on Music's Home and
// a full screen with checkboxes. Downloading is an imported list named "Suggested for you".

// What both the shelf and the screen read: the server's answer, polled every 3 s while it is
// still building, plus what was downloaded or dismissed here so the rows change at once.
@Stable
internal class SuggestionsState(private val ui: AudioUi) {
    var data by mutableStateOf<MusicSuggestions?>(null)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    var localQueued by mutableStateOf(setOf<String>())
        private set
    private var hiddenKeys by mutableStateOf(setOf<String>())
    private var hiddenArtists by mutableStateOf(setOf<String>())
    var downloading by mutableStateOf(false)
        private set
    var tick by mutableIntStateOf(0)
        private set
    private var refreshNext = false

    val albums: List<MusicSuggestedAlbum>
        get() = data?.albums.orEmpty().filter { it.key !in hiddenKeys && it.artist.lowercase() !in hiddenArtists }

    val working: Boolean get() = data?.working == true || (data == null && error == null)

    fun refresh() {
        refreshNext = true
        tick++
    }

    suspend fun poll() {
        while (true) {
            val refresh = refreshNext
            refreshNext = false
            runCatching { ui.api.musicSuggestions(refresh) }
                .onSuccess { data = it; error = null }
                .onFailure { error = it.message ?: "Couldn't load suggestions." }
            if (data?.working != true) break
            delay(MusicSuggestionsFormat.POLL_MS)
        }
    }

    fun isQueued(album: MusicSuggestedAlbum) = MusicSuggestionsFormat.isQueued(album, localQueued)

    fun download(albums: List<MusicSuggestedAlbum>, after: () -> Unit = {}) {
        val wanted = albums.filter { !isQueued(it) }
        if (wanted.isEmpty() || downloading) return
        downloading = true
        ui.launch("Couldn't start the download.") {
            try {
                ui.runtime.musicImports.start(MusicSuggestionsFormat.LIST_NAME, MusicSuggestionsFormat.importItems(wanted))
                localQueued = localQueued + wanted.map { it.key }
                ui.message(MusicSuggestionsFormat.startedMessage(wanted.size), action = "Downloads") { ui.nav.open(AudioRoutes.ALBUM_DOWNLOADS) }
                after()
            } finally {
                downloading = false
            }
        }
    }

    fun dismiss(album: MusicSuggestedAlbum, wholeArtist: Boolean) {
        ui.launch("Couldn't save that.") {
            ui.api.dismissMusicSuggestion(album.artist, if (wholeArtist) null else album.title)
            if (wholeArtist) hiddenArtists = hiddenArtists + album.artist.lowercase() else hiddenKeys = hiddenKeys + album.key
            ui.message(if (wholeArtist) "No more suggestions from ${album.artist}" else "Won't suggest ${album.title} again")
        }
    }
}

@Composable
internal fun rememberSuggestions(): SuggestionsState {
    val ui = LocalAudioUi.current
    val state = remember(ui) { SuggestionsState(ui) }
    LaunchedEffect(state, state.tick) { state.poll() }
    return state
}

// ---------------- Home shelf ----------------

// Drawn as LazyColumn rows on Music's Home; nothing until there is at least one album.
@Composable
internal fun SuggestionsShelf() {
    val ui = LocalAudioUi.current
    val suggestions = rememberSuggestions()
    val albums = suggestions.albums
    if (albums.isEmpty()) return
    Column {
        SectionTitle(
            "Suggested for you",
            subtitle = MusicSuggestionsFormat.basedOn(suggestions.data?.seeds.orEmpty(), shown = 3),
            actionLabel = "See all",
        ) { ui.nav.open(AudioRoutes.SUGGESTIONS) }
        Shelf {
            items(albums, key = { "sug" + it.key }) { album ->
                SuggestionCard(album, queued = suggestions.isQueued(album)) { ui.nav.open(AudioRoutes.SUGGESTIONS) }
            }
        }
    }
}

@Composable
private fun SuggestionCard(album: MusicSuggestedAlbum, queued: Boolean, onClick: () -> Unit) {
    Column(
        Modifier.width(148.dp).clip(RoundedCornerShape(10.dp)).clickable(onClick = onClick),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        ArtImage(album.artUrl, Modifier.fillMaxWidth().aspectRatio(1f), seed = album.title)
        Text(album.title, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(
            listOfNotNull(album.artist, album.year?.toString()).joinToString(" · "),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        val line = if (queued) "On its way" else MusicSuggestionsFormat.because(album)
        line?.let {
            Text(
                it,
                style = MaterialTheme.typography.labelSmall,
                color = if (queued) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

// ---------------- Full screen ----------------

@Composable
fun MusicSuggestionsScreen() {
    val ui = LocalAudioUi.current
    val suggestions = rememberSuggestions()
    var ticked by remember { mutableStateOf(setOf<String>()) }
    val data = suggestions.data
    val albums = suggestions.albums
    val tickable = albums.filter { it.key in ticked && !suggestions.isQueued(it) }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Suggested for you", onBack = ui.nav.back) {
            if (suggestions.working) {
                Box(Modifier.size(48.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) }
            } else {
                IconButton(onClick = { suggestions.refresh() }) { Icon(Icons.Default.Refresh, contentDescription = "Refresh") }
            }
        }
        LazyColumn(Modifier.weight(1f).fillMaxWidth(), contentPadding = PaddingValues(bottom = 16.dp)) {
            MusicSuggestionsFormat.basedOn(data?.seeds.orEmpty())?.let { line ->
                item {
                    Text(line, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp))
                }
            }
            if (suggestions.working) {
                item {
                    Row(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                        Spacer(Modifier.width(10.dp))
                        Text("Finding albums you might like…", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
            val message = suggestions.error ?: data?.message?.takeIf { data.failed }
            message?.let { item { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp)) } }
            if (albums.isEmpty() && !suggestions.working) {
                item { EmptyPanel(Icons.Default.AutoAwesome, "No suggestions yet", MusicSuggestionsFormat.EMPTY) }
            }
            items(albums, key = { it.key }) { album ->
                SuggestionRow(
                    album,
                    queued = suggestions.isQueued(album),
                    ticked = album.key in ticked,
                    busy = suggestions.downloading,
                    onTick = { on -> ticked = if (on) ticked + album.key else ticked - album.key },
                    onDownload = { suggestions.download(listOf(album)) { ticked = ticked - album.key } },
                    onDismiss = { whole -> suggestions.dismiss(album, whole) },
                )
            }
        }
        if (albums.any { !suggestions.isQueued(it) }) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = {
                    val open = albums.filter { !suggestions.isQueued(it) }.map { it.key }.toSet()
                    ticked = if (open.all { it in ticked }) emptySet() else open
                }) { Text(if (albums.filter { !suggestions.isQueued(it) }.all { it.key in ticked }) "Untick all" else "Tick all") }
                Spacer(Modifier.weight(1f))
                Button(
                    onClick = { suggestions.download(tickable) { ticked = emptySet() } },
                    enabled = tickable.isNotEmpty() && !suggestions.downloading,
                ) { Text(if (tickable.isEmpty()) "Download all ticked" else "Download all ticked (${tickable.size})") }
            }
        }
    }
}

@Composable
private fun SuggestionRow(
    album: MusicSuggestedAlbum,
    queued: Boolean,
    ticked: Boolean,
    busy: Boolean,
    onTick: (Boolean) -> Unit,
    onDownload: () -> Unit,
    onDismiss: (wholeArtist: Boolean) -> Unit,
) {
    var menu by remember { mutableStateOf(false) }
    Row(
        Modifier.fillMaxWidth().clickable(enabled = !queued) { onTick(!ticked) }.padding(start = 4.dp, end = 4.dp, top = 6.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Checkbox(checked = ticked && !queued, onCheckedChange = onTick, enabled = !queued)
        ArtImage(album.artUrl, Modifier.size(56.dp), sizePx = 168, seed = album.title)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(album.title, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(
                listOfNotNull(album.artist, album.year?.toString()).joinToString(" · "),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            MusicSuggestionsFormat.because(album)?.let {
                Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
        }
        if (queued) {
            Text("On its way", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(horizontal = 8.dp))
        } else {
            TextButton(onClick = onDownload, enabled = !busy) { Text("Download") }
        }
        Box {
            IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = "More") }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                DropdownMenuItem(text = { Text("Not interested") }, onClick = { menu = false; onDismiss(false) })
                DropdownMenuItem(text = { Text("Not interested in ${album.artist}") }, onClick = { menu = false; onDismiss(true) })
            }
        }
    }
}

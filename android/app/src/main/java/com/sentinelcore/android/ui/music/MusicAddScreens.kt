package com.sentinelcore.android.ui.music

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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Album
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.MusicLookup
import com.sentinelcore.android.audio.MusicRelease
import com.sentinelcore.android.audio.MusicReleaseSearch
import com.sentinelcore.android.data.MediaDownload
import com.sentinelcore.android.data.MediaDownloadFormat
import com.sentinelcore.android.ui.audio.ArtImage
import com.sentinelcore.android.ui.audio.AudioRoutes
import com.sentinelcore.android.ui.audio.EmptyPanel
import com.sentinelcore.android.ui.audio.LocalAudioUi
import com.sentinelcore.android.ui.audio.SectionTitle
import com.sentinelcore.android.ui.screens.DetailHeader
import com.sentinelcore.android.ui.screens.ImportListLink
import com.sentinelcore.android.ui.screens.ImportedListsSection
import com.sentinelcore.android.ui.screens.mediaImportListRoute
import com.sentinelcore.android.ui.screens.mediaImportRoute
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import androidx.compose.foundation.background

// "+ Add" for music (§4 Getting music): find the album on MusicBrainz, search the sites for it in
// the background (the VPN may take a minute to come up), pick a release, and it lands in the
// library when it's done.
@Composable
fun MusicAddScreen() {
    val ui = LocalAudioUi.current
    var query by rememberSaveable { mutableStateOf("") }
    var lookups by remember { mutableStateOf<List<MusicLookup>?>(null) }
    var looking by remember { mutableStateOf(false) }
    var lookupError by remember { mutableStateOf<String?>(null) }
    var picked by remember { mutableStateOf<MusicLookup?>(null) }
    val status = com.sentinelcore.android.ui.audio.rememberLoadable(Unit) { ui.api.musicDownloadStatus() }

    fun lookup() {
        val q = query.trim()
        if (q.isEmpty()) return
        looking = true
        ui.scope.launch {
            runCatching { ui.api.musicLookup(q) }
                .onSuccess { lookups = it; lookupError = null }
                .onFailure { lookupError = it.message ?: "The lookup failed." }
            looking = false
        }
    }

    val chosen = picked
    if (chosen != null) {
        ReleaseSearch(chosen, onBack = { picked = null })
        return
    }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Add an album", onBack = ui.nav.back) {
            TextButton(onClick = { ui.nav.open(AudioRoutes.ALBUM_DOWNLOADS) }) { Text("Downloads") }
        }
        status.value?.let { s ->
            if (!s.musicLibrary) {
                Text(
                    "Add a music library on this server's own disk first - downloads are filed there.",
                    color = MaterialTheme.colorScheme.error,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                )
            }
        }
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            placeholder = { Text("Artist and album, e.g. \"Fleetwood Mac Rumours\"") },
            leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
            trailingIcon = { if (query.isNotEmpty()) IconButton(onClick = { query = ""; lookups = null }) { Icon(Icons.Default.Close, contentDescription = "Clear") } },
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
            keyboardActions = KeyboardActions(onSearch = { lookup() }),
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        )
        if (lookups == null) {
            ImportListLink("music", onImport = { ui.nav.open(mediaImportRoute("music")) }, modifier = Modifier.padding(horizontal = 16.dp))
        }
        if (looking) LinearProgressIndicator(Modifier.fillMaxWidth().padding(horizontal = 16.dp))
        lookupError?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
        val list = lookups
        when {
            list == null -> EmptyPanel(Icons.Default.Album, "Find an album", "Search MusicBrainz for the album you want. Type the artist and the album for the best results.")
            list.isEmpty() -> EmptyPanel(Icons.Default.Search, "Nothing found", "Try the artist's name on its own, or check the spelling.")
            else -> LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                items(list, key = { it.mbid.ifBlank { it.title + it.artist } }) { album ->
                    Row(
                        Modifier.fillMaxWidth().clickable { picked = album }.padding(horizontal = 16.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        // Cover Art Archive answers 404 when it has no picture; the placeholder shows.
                        ArtImage(album.artUrl, Modifier.size(60.dp), sizePx = 180, seed = album.title)
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f)) {
                            Text(album.title, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(
                                listOfNotNull(album.artist, album.year?.toString(), album.type, album.trackCount?.let { "$it tracks" }).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                        if (album.inLibrary) {
                            Icon(Icons.Default.CheckCircle, contentDescription = "Already in the library", tint = MaterialTheme.colorScheme.primary)
                        }
                    }
                }
            }
        }
    }
}


@Composable
private fun ReleaseSearch(album: MusicLookup, onBack: () -> Unit) {
    val ui = LocalAudioUi.current
    var search by remember { mutableStateOf<MusicReleaseSearch?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var showOthers by remember { mutableStateOf(false) }
    var confirm by remember { mutableStateOf<MusicRelease?>(null) }
    var attempt by remember { mutableStateOf(0) }
    LaunchedEffect(album, attempt) {
        error = null
        search = null
        try {
            var current = ui.api.startMusicSearch(album.artist, album.title, album.year, album.mbid.ifBlank { null }, album.artUrl)
            search = current
            while (!current.finished) {
                delay(2_000)
                current = ui.api.musicSearch(current.id)
                search = current
            }
        } catch (failure: Exception) {
            if (failure is kotlinx.coroutines.CancellationException) throw failure
            error = failure.message ?: "The search failed."
        }
    }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("${album.artist} - ${album.title}", onBack = onBack)
        if (album.inLibrary) {
            Card(Modifier.fillMaxWidth().padding(16.dp), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer)) {
                Text("You already have an album with this name by ${album.artist}.", modifier = Modifier.padding(12.dp), color = MaterialTheme.colorScheme.onPrimaryContainer)
            }
        }
        val current = search
        when {
            error != null || current?.phase == "failed" -> Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(error ?: current?.error ?: "The search failed.", color = MaterialTheme.colorScheme.error)
                TextButton(onClick = { attempt++ }) {
                    Icon(Icons.Default.Refresh, contentDescription = null)
                    Text("Search again")
                }
            }
            current == null || !current.finished -> Column(Modifier.fillMaxWidth().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
                CircularProgressIndicator()
                Text(current?.message ?: if (current?.phase == "searching") "Searching the sites…" else "Connecting to the VPN…", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            current.results.isEmpty() && current.others.isEmpty() -> EmptyPanel(Icons.Default.Search, "No releases found", "Nobody is sharing this album on your sites right now. Try again later.")
            else -> LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                if (current.results.isNotEmpty()) item { SectionTitle("Best matches", subtitle = "Lossless first when your library is mostly lossless, then the most seeders") }
                items(current.results, key = { "r" + it.id }) { release -> ReleaseRow(release) { confirm = release } }
                if (current.others.isNotEmpty()) {
                    item {
                        TextButton(onClick = { showOthers = !showOthers }, modifier = Modifier.padding(horizontal = 8.dp)) {
                            Text(if (showOthers) "Hide the rest" else "Show ${current.others.size} more")
                        }
                    }
                    if (showOthers) items(current.others, key = { "o" + it.id }) { release -> ReleaseRow(release) { confirm = release } }
                }
            }
        }
    }
    val release = confirm
    val searchId = search?.id
    if (release != null && searchId != null) {
        AlertDialog(
            onDismissRequest = { confirm = null },
            title = { Text("Download this release?") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Text(release.title)
                    Text(listOfNotNull(release.format, MediaDownloadFormat.sizeLabel(release.sizeBytes), release.site).joinToString(" · "), style = MaterialTheme.typography.bodySmall)
                    release.warning?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
                }
            },
            confirmButton = {
                TextButton(onClick = {
                    confirm = null
                    ui.launch("Couldn't start the download.") {
                        ui.api.grabMusic(searchId, release.id)
                        ui.message("Downloading ${album.title}. It shows up under Recently added when it's done.")
                        ui.nav.back()
                        ui.nav.open(AudioRoutes.ALBUM_DOWNLOADS)
                    }
                }) { Text("Download") }
            },
            dismissButton = { TextButton(onClick = { confirm = null }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun ReleaseRow(release: MusicRelease, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(release.title, style = MaterialTheme.typography.bodyMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                release.format?.let { format ->
                    Text(
                        format,
                        style = MaterialTheme.typography.labelSmall,
                        color = if (release.lossless) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSecondaryContainer,
                        modifier = Modifier.clip(RoundedCornerShape(4.dp))
                            .background(if (release.lossless) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.secondaryContainer)
                            .padding(horizontal = 5.dp, vertical = 1.dp),
                    )
                }
                Text(
                    listOfNotNull(MediaDownloadFormat.sizeLabel(release.sizeBytes), release.seeders?.let { "$it seeders" }, release.site, AudioFormat.day(release.publishedAt).ifBlank { null }).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            release.warning?.let {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Default.Warning, contentDescription = null, tint = MaterialTheme.colorScheme.error, modifier = Modifier.size(14.dp))
                    Spacer(Modifier.width(4.dp))
                    Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.labelSmall)
                }
            }
            if (!release.matches) Text("May not be this album", color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.labelSmall)
        }
    }
}

// The album download queue: progress while active, then done (and in the library) or failed.
@Composable
fun AlbumDownloadsScreen() {
    val ui = LocalAudioUi.current
    var list by remember { mutableStateOf<com.sentinelcore.android.data.MediaDownloadList?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var tick by remember { mutableStateOf(0) }
    LaunchedEffect(tick) {
        while (true) {
            runCatching { ui.api.musicDownloads() }.onSuccess { list = it; error = null }.onFailure { error = it.message }
            delay(if (list?.active?.isNotEmpty() == true) 2_500 else 10_000)
        }
    }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Album downloads", onBack = ui.nav.back) {
            TextButton(onClick = { ui.nav.open(mediaImportRoute("music")) }) { Text("Import a list") }
            TextButton(onClick = { ui.nav.open(AudioRoutes.ADD) }) { Text("+ Add") }
        }
        error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
        val value = list
        when {
            value == null -> Column(Modifier.fillMaxWidth().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally) { CircularProgressIndicator() }
            else -> LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                if (value.active.isEmpty() && value.recent.isEmpty()) {
                    item { EmptyPanel(Icons.Default.Album, "No album downloads", "Use + Add to find an album.") }
                }
                if (value.active.isNotEmpty()) item { SectionTitle("Downloading") }
                items(value.active, key = { "a" + it.id }) { download -> AlbumDownloadRow(download) { tick++ } }
                item(key = "imported-lists") {
                    ImportedListsSection(
                        ui.runtime.musicImports,
                        onImport = { ui.nav.open(mediaImportRoute("music")) },
                        onOpen = { ui.nav.open(mediaImportListRoute("music", it)) },
                        modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                    )
                }
                if (value.recent.isNotEmpty()) item { SectionTitle("Recent") }
                items(value.recent, key = { "r" + it.id }) { download -> AlbumDownloadRow(download) { tick++ } }
            }
        }
    }
}

@Composable
private fun AlbumDownloadRow(download: MediaDownload, onChanged: () -> Unit) {
    val ui = LocalAudioUi.current
    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        ArtImage(download.posterUrl, Modifier.size(56.dp), sizePx = 168, seed = download.title)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(download.title, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            download.subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            if (download.isActive) {
                LinearProgressIndicator(progress = { download.progress.toFloat().coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth().height(4.dp))
            }
            Text(
                listOfNotNull(
                    MediaDownloadFormat.stateWord(download.state),
                    if (download.isActive) MediaDownloadFormat.percentLabel(download.progress) else null,
                    MediaDownloadFormat.speedLabel(download.speedBytesPerSecond),
                    MediaDownloadFormat.etaLabel(download.etaSeconds),
                ).joinToString(" · "),
                style = MaterialTheme.typography.labelSmall,
                color = if (download.isFailed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
            )
            download.error?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = if (download.isFailed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        if (download.isFailed) {
            IconButton(onClick = { ui.launch("Couldn't retry it.") { ui.api.retryMusicDownload(download.id); onChanged() } }) { Icon(Icons.Default.Refresh, contentDescription = "Retry") }
        }
        IconButton(onClick = { ui.launch("Couldn't remove it.") { ui.api.deleteMusicDownload(download.id); onChanged() } }) {
            Icon(Icons.Default.Close, contentDescription = if (download.isActive) "Cancel" else "Clear")
        }
    }
}

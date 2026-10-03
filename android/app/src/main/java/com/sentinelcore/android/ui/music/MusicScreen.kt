package com.sentinelcore.android.ui.music

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
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
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.LibraryMusic
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.MusicHome
import com.sentinelcore.android.audio.MusicSearch
import com.sentinelcore.android.audio.PlayContext
import com.sentinelcore.android.audio.Track
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.audio.AlbumCard
import com.sentinelcore.android.ui.audio.ArtImage
import com.sentinelcore.android.ui.audio.ArtistCircle
import com.sentinelcore.android.ui.audio.AudioMoreMenu
import com.sentinelcore.android.ui.audio.AudioRoutes
import com.sentinelcore.android.ui.audio.ContinueBanner
import com.sentinelcore.android.ui.audio.EmptyPanel
import com.sentinelcore.android.ui.audio.Item
import com.sentinelcore.android.ui.audio.LoadableContent
import com.sentinelcore.android.ui.audio.LocalAudioUi
import com.sentinelcore.android.ui.audio.MixCard
import com.sentinelcore.android.ui.audio.PlaylistCard
import com.sentinelcore.android.ui.audio.SectionTitle
import com.sentinelcore.android.ui.audio.Shelf
import com.sentinelcore.android.ui.audio.TrackMenuTarget
import com.sentinelcore.android.ui.audio.TrackRow
import com.sentinelcore.android.ui.audio.combinedClickableCompat
import com.sentinelcore.android.ui.audio.rememberLoadable
import com.sentinelcore.android.ui.screens.ScreenHeader
import kotlinx.coroutines.delay
import java.text.NumberFormat

private enum class MusicPane(val title: String) { HOME("Home"), LIBRARY("Library"), SEARCH("Search") }

// The Music tab (MUSIC_AND_PODCASTS.md §4, §11): Home's rows, the Library and Search.
@Composable
fun MusicScreen(state: SentinelUiState) {
    val ui = LocalAudioUi.current
    var paneIndex by rememberSaveable { mutableIntStateOf(ui.runtime.prefs.lastMusicPane.coerceIn(0, MusicPane.entries.lastIndex)) }
    val pane = MusicPane.entries[paneIndex]
    val canDownload = rememberLoadable(Unit) { ui.api.musicDownloadStatus()?.canDownload == true }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader(
            "Music",
            state,
            actions = {
                IconButton(onClick = { ui.engine.shuffleAll() }) { Icon(Icons.Default.Shuffle, contentDescription = "Shuffle all") }
                if (canDownload.value == true) {
                    IconButton(onClick = { ui.nav.open(AudioRoutes.ADD) }) { Icon(Icons.Default.Add, contentDescription = "Add an album") }
                }
                AudioMoreMenu { close ->
                    if (canDownload.value == true) Item(Icons.Default.LibraryMusic, "Album downloads") { close(); ui.nav.open(AudioRoutes.ALBUM_DOWNLOADS) }
                    if (ui.isAdmin) Item(Icons.Default.LibraryMusic, "Music libraries") { close(); ui.nav.open(AudioRoutes.MUSIC_LIBRARIES) }
                }
            },
        )
        TabRow(selectedTabIndex = paneIndex) {
            MusicPane.entries.forEachIndexed { index, entry ->
                Tab(
                    selected = paneIndex == index,
                    onClick = {
                        paneIndex = index
                        ui.runtime.prefs.lastMusicPane = index
                    },
                    text = { Text(entry.title) },
                )
            }
        }
        when (pane) {
            MusicPane.HOME -> MusicHomePane()
            MusicPane.LIBRARY -> MusicLibraryPane()
            MusicPane.SEARCH -> MusicSearchPane()
        }
    }
}

// ---------------- Home ----------------

@Composable
private fun MusicHomePane() {
    val ui = LocalAudioUi.current
    val home = rememberLoadable(Unit) { ui.api.musicHome() }
    LoadableContent(home) { value ->
        if (value.trackCount == 0 && value.recentlyAdded.isEmpty()) {
            EmptyPanel(
                Icons.Default.LibraryMusic,
                "No music yet",
                if (ui.isAdmin) "Add a folder of music on the server or another computer, and it shows up here once it's scanned."
                else "Ask whoever runs Sentinel Core to add a music library.",
            ) {
                if (ui.isAdmin) Button(onClick = { ui.nav.open(AudioRoutes.MUSIC_LIBRARIES) }) { Text("Add a music library") }
            }
            return@LoadableContent
        }
        HomeRows(value)
    }
}

@Composable
private fun HomeRows(home: MusicHome) {
    val ui = LocalAudioUi.current
    val numbers = NumberFormat.getIntegerInstance()
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
        item { ContinueBanner("music") }
        item {
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                AssistChip(onClick = { ui.engine.shuffleAll() }, label = { Text("Shuffle all") }, leadingIcon = { Icon(Icons.Default.Shuffle, null, Modifier.size(18.dp)) })
                if (home.lovedTracks.isNotEmpty()) {
                    AssistChip(
                        onClick = { ui.launch { ui.playTracks(ui.api.tracks(loved = true, sort = "added", order = "desc", limit = 500).items, 0, PlayContext("playlist", null, "Loved"), shuffle = true) } },
                        label = { Text("Loved") },
                        leadingIcon = { Icon(Icons.Default.Favorite, null, Modifier.size(18.dp), tint = Color(0xFFE5486B)) },
                    )
                }
            }
        }
        if (home.pinned.isNotEmpty()) {
            item { SectionTitle("Pinned") }
            item { Shelf { items(home.pinned, key = { "pin" + it.id }) { PlaylistCard(it) { ui.nav.openPlaylist(it.id) } } } }
        }
        if (home.recentlyPlayed.isNotEmpty()) {
            item { SectionTitle("Recently played") }
            item {
                Shelf {
                    itemsIndexed(home.recentlyPlayed, key = { index, _ -> "recent$index" }) { _, recent ->
                        when {
                            recent.album != null -> AlbumCard(recent.album) { ui.nav.openAlbum(recent.album.id) }
                            recent.playlist != null -> PlaylistCard(recent.playlist) { ui.nav.openPlaylist(recent.playlist.id) }
                            recent.artist != null -> ArtistCircle(recent.artist, width = 148.dp) { ui.nav.openArtist(recent.artist.id) }
                            recent.mix != null -> MixCard(recent.mix) { ui.engine.playMix(recent.mix) }
                        }
                    }
                }
            }
        }
        if (home.mixes.isNotEmpty()) {
            item { SectionTitle("Made for you", subtitle = "Mixes from what you play") }
            item { Shelf { items(home.mixes, key = { "mix" + it.id }) { mix -> MixCard(mix) { ui.engine.playMix(mix); ui.message("Playing ${mix.name}") } } } }
        }
        if (home.recentlyAdded.isNotEmpty()) {
            item { SectionTitle("Recently added", actionLabel = "See all") { ui.nav.open("music/albums?sort=added") } }
            item { Shelf { items(home.recentlyAdded, key = { "add" + it.id }) { AlbumCard(it, onLongClick = null) { ui.nav.openAlbum(it.id) } } } }
        }
        if (home.heavyRotation.isNotEmpty()) {
            item { SectionTitle("Heavy rotation", subtitle = "Your most played this month") }
            item { Shelf { items(home.heavyRotation, key = { "heavy" + it.id }) { AlbumCard(it) { ui.nav.openAlbum(it.id) } } } }
        }
        home.onThisDay?.let { day ->
            item { SectionTitle("On this day", subtitle = "What you played on ${day.label}", actionLabel = "Play") { ui.playTracks(day.tracks, 0, PlayContext("queue", null, "On this day")) } }
            item { TrackShelf(day.tracks, "On this day") }
        }
        if (home.forgottenFavorites.isNotEmpty()) {
            item { SectionTitle("Forgotten favorites", subtitle = "Loved once, not heard in a while", actionLabel = "Play") { ui.playTracks(home.forgottenFavorites, 0, PlayContext("queue", null, "Forgotten favorites")) } }
            item { TrackShelf(home.forgottenFavorites, "Forgotten favorites") }
        }
        if (home.discover.isNotEmpty()) {
            item { SectionTitle("Discover", subtitle = "Albums you've never played") }
            item { Shelf { items(home.discover, key = { "disc" + it.id }) { AlbumCard(it) { ui.nav.openAlbum(it.id) } } } }
        }
        if (home.lovedTracks.isNotEmpty()) {
            item { SectionTitle("Loved", actionLabel = "Play") { ui.playTracks(home.lovedTracks, 0, PlayContext("playlist", null, "Loved")) } }
            item { TrackShelf(home.lovedTracks, "Loved") }
        }
        if (home.decades.isNotEmpty()) {
            item { SectionTitle("Decades") }
            item {
                Shelf {
                    items(home.decades, key = { "dec" + it.decade }) { decade ->
                        DecadeTile(decade.label, decade.trackCount) { ui.nav.openDecade(decade.decade) }
                    }
                }
            }
        }
        item {
            Text(
                "${numbers.format(home.trackCount)} songs · ${numbers.format(home.albumCount)} albums · ${numbers.format(home.artistCount)} artists",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.fillMaxWidth().padding(24.dp),
            )
        }
    }
}

// Songs on a shelf: tap one to play the row from there.
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun TrackShelf(tracks: List<Track>, contextName: String, width: Dp = 132.dp) {
    val ui = LocalAudioUi.current
    Shelf {
        itemsIndexed(tracks, key = { index, track -> "$contextName$index${track.id}" }) { index, track ->
            Column(
                Modifier.width(width).clip(RoundedCornerShape(10.dp)).combinedClickableCompat(
                    onClick = { ui.playTracks(tracks, index, PlayContext("queue", null, contextName)) },
                    onLongClick = { ui.trackMenu = TrackMenuTarget(track) },
                ),
                verticalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                Box(Modifier.fillMaxWidth().aspectRatio(1f)) {
                    ArtImage(track.artUrl, Modifier.fillMaxSize(), seed = track.album)
                    Icon(
                        Icons.Default.PlayArrow,
                        contentDescription = null,
                        tint = Color.White,
                        modifier = Modifier.align(Alignment.BottomEnd).padding(6.dp).size(28.dp)
                            .background(Color.Black.copy(alpha = 0.5f), RoundedCornerShape(14.dp)).padding(3.dp),
                    )
                }
                Text(track.title, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(track.artist, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

private val decadeColors = listOf(
    Color(0xFFB5651D), Color(0xFF8E44AD), Color(0xFF2980B9), Color(0xFF16A085), Color(0xFFC0392B), Color(0xFF27AE60), Color(0xFFD35400), Color(0xFF34495E),
)

@Composable
private fun DecadeTile(label: String, count: Int, onClick: () -> Unit) {
    val color = decadeColors[(label.hashCode() and 0x7fffffff) % decadeColors.size]
    Box(
        Modifier.width(120.dp).height(80.dp).clip(RoundedCornerShape(12.dp))
            .background(Brush.linearGradient(listOf(color, color.copy(alpha = 0.55f))))
            .clickable(onClick = onClick)
            .padding(12.dp),
    ) {
        Text(label, color = Color.White, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Black)
        Text("$count songs", color = Color.White.copy(alpha = 0.85f), style = MaterialTheme.typography.labelSmall, modifier = Modifier.align(Alignment.BottomStart))
    }
}

// ---------------- Search ----------------

@Composable
private fun MusicSearchPane() {
    val ui = LocalAudioUi.current
    var query by rememberSaveable { mutableStateOf("") }
    var result by mutableStateOfSaveableSearch()
    var error by rememberSaveable { mutableStateOf<String?>(null) }
    LaunchedEffect(query) {
        val q = query.trim()
        if (q.length < 2) {
            result = null
            return@LaunchedEffect
        }
        delay(280)
        runCatching { ui.api.search(q) }
            .onSuccess { result = it; error = null }
            .onFailure { error = it.message }
    }
    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            placeholder = { Text("Artists, albums, songs, playlists") },
            leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
            trailingIcon = { if (query.isNotEmpty()) IconButton(onClick = { query = "" }) { Icon(Icons.Default.Close, contentDescription = "Clear") } },
            singleLine = true,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        )
        val found = result
        when {
            error != null -> Text(error.orEmpty(), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp))
            query.trim().length < 2 -> GenreBrowse()
            found == null -> Unit
            found.isEmpty -> EmptyPanel(Icons.Default.Search, "Nothing found", "No artists, albums, songs or playlists match \"${query.trim()}\".")
            else -> SearchResults(found)
        }
    }
}

@Composable
private fun mutableStateOfSaveableSearch() = androidx.compose.runtime.remember { mutableStateOf<MusicSearch?>(null) }

@Composable
private fun SearchResults(result: MusicSearch) {
    val ui = LocalAudioUi.current
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
        if (result.artists.isNotEmpty()) {
            item { SectionTitle("Artists") }
            item { Shelf { items(result.artists, key = { it.id }) { ArtistCircle(it) { ui.nav.openArtist(it.id) } } } }
        }
        if (result.tracks.isNotEmpty()) {
            item { SectionTitle("Songs", actionLabel = "Play all") { ui.playTracks(result.tracks, 0, PlayContext("search", null, "Search")) } }
            itemsIndexed(result.tracks, key = { _, track -> "t" + track.id }) { index, track ->
                TrackRow(track, showAlbum = true, onMore = { ui.trackMenu = TrackMenuTarget(track) }) {
                    ui.playTracks(result.tracks, index, PlayContext("search", null, "Search"))
                }
            }
        }
        if (result.albums.isNotEmpty()) {
            item { SectionTitle("Albums") }
            item { Shelf { items(result.albums, key = { it.id }) { AlbumCard(it) { ui.nav.openAlbum(it.id) } } } }
        }
        if (result.playlists.isNotEmpty()) {
            item { SectionTitle("Playlists") }
            item { Shelf { items(result.playlists, key = { it.id }) { PlaylistCard(it) { ui.nav.openPlaylist(it.id) } } } }
        }
        if (result.genres.isNotEmpty()) {
            item { SectionTitle("Genres") }
            item {
                Shelf {
                    items(result.genres) { genre -> AssistChip(onClick = { ui.nav.openGenre(genre) }, label = { Text(genre) }) }
                }
            }
        }
    }
}

// Before anything is typed: the genres as big tiles, to browse instead.
@Composable
private fun GenreBrowse() {
    val ui = LocalAudioUi.current
    val genres = rememberLoadable(Unit) { ui.api.genres().sortedByDescending { it.trackCount } }
    LoadableContent(genres) { list ->
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
            item { SectionTitle("Browse by genre") }
            items(list.chunked(2), key = { row -> row.first().name }) { row ->
                Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 5.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    row.forEach { genre ->
                        Box(
                            Modifier.weight(1f).height(84.dp).clip(RoundedCornerShape(12.dp))
                                .background(com.sentinelcore.android.ui.audio.placeholderBrush(genre.name))
                                .clickable { ui.nav.openGenre(genre.name) },
                        ) {
                            genre.artUrls.firstOrNull()?.let {
                                ArtImage(
                                    it,
                                    Modifier.align(Alignment.BottomEnd).padding(end = 8.dp, bottom = 8.dp).size(56.dp),
                                    sizePx = 168,
                                    shape = RoundedCornerShape(6.dp),
                                    seed = genre.name,
                                )
                            }
                            Column(Modifier.padding(12.dp)) {
                                Text(genre.name, color = Color.White, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                Text("${genre.albumCount} albums", color = Color.White.copy(alpha = 0.8f), style = MaterialTheme.typography.labelSmall)
                            }
                        }
                    }
                    if (row.size == 1) Spacer(Modifier.weight(1f))
                }
            }
        }
    }
}

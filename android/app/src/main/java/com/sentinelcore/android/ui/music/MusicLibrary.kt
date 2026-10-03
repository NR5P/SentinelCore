package com.sentinelcore.android.ui.music

import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Sort
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.filled.QueueMusic
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Category
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.Album
import com.sentinelcore.android.audio.Artist
import com.sentinelcore.android.audio.AudioApi
import com.sentinelcore.android.audio.Page
import com.sentinelcore.android.audio.PlayContext
import com.sentinelcore.android.audio.Track
import com.sentinelcore.android.ui.audio.AlbumCard
import com.sentinelcore.android.ui.audio.ArtImage
import com.sentinelcore.android.ui.audio.ArtistCircle
import com.sentinelcore.android.ui.audio.AudioRoutes
import com.sentinelcore.android.ui.audio.EmptyPanel
import com.sentinelcore.android.ui.audio.ErrorPanel
import com.sentinelcore.android.ui.audio.LoadableContent
import com.sentinelcore.android.ui.audio.LocalAudioUi
import com.sentinelcore.android.ui.audio.MosaicArt
import com.sentinelcore.android.ui.audio.TrackMenuTarget
import com.sentinelcore.android.ui.audio.TrackRow
import com.sentinelcore.android.ui.audio.playlistSubtitle
import com.sentinelcore.android.ui.audio.rememberLoadable
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// ---------------- paging ----------------

// A list the server pages (§4 Browse: offset/limit/total). Loads the first page when the keys
// change and the next as the end comes into view.
@Stable
class Paged<T>(private val fetch: suspend (offset: Int) -> Page<T>) {
    val items = mutableStateListOf<T>()
    var total by mutableIntStateOf(-1)
    var loading by mutableStateOf(false)
    var error by mutableStateOf<String?>(null)
    private var job: Job? = null

    fun reload(scope: CoroutineScope) {
        job?.cancel()
        loading = true
        job = scope.launch {
            runCatching { fetch(0) }
                .onSuccess { page ->
                    items.clear()
                    items.addAll(page.items)
                    total = page.total
                    error = null
                }
                .onFailure { if (it !is kotlinx.coroutines.CancellationException) error = it.message ?: "Couldn't load this." }
            loading = false
        }
    }

    fun more(scope: CoroutineScope) {
        if (loading || error != null || total < 0 || items.size >= total) return
        loading = true
        val offset = items.size
        job = scope.launch {
            runCatching { fetch(offset) }
                .onSuccess { page -> items.addAll(page.items); total = page.total }
                .onFailure { if (it !is kotlinx.coroutines.CancellationException) error = it.message }
            loading = false
        }
    }
}

@Composable
fun <T> rememberPaged(vararg keys: Any?, fetch: suspend (offset: Int) -> Page<T>): Paged<T> {
    val ui = LocalAudioUi.current
    val scope = rememberCoroutineScope()
    val paged = remember(*keys) { Paged(fetch) }
    LaunchedEffect(paged, ui.libraryVersion) {
        // A short pause lets a fast typist finish before the first request.
        delay(150)
        paged.reload(scope)
    }
    return paged
}

private enum class LibrarySegment(val title: String) { ARTISTS("Artists"), ALBUMS("Albums"), SONGS("Songs"), GENRES("Genres"), PLAYLISTS("Playlists"), FOLDERS("Folders") }

private val albumSorts = listOf("title" to "Title", "artist" to "Artist", "year" to "Year", "added" to "Recently added", "plays" to "Most played", "rating" to "Rating")
private val songSorts = listOf(
    "title" to "Title", "artist" to "Artist", "album" to "Album", "year" to "Year", "added" to "Recently added",
    "plays" to "Most played", "lastPlayed" to "Recently played", "rating" to "Rating", "duration" to "Length",
)
private val artistSorts = listOf("name" to "Name", "plays" to "Most played", "added" to "Recently added")

// A–Z sorts read up, everything else reads newest/most first.
private fun defaultOrder(sort: String) = if (sort in setOf("title", "name", "artist", "album")) "asc" else "desc"

@Composable
fun MusicLibraryPane() {
    var segment by rememberSaveable { mutableIntStateOf(1) }
    Column(Modifier.fillMaxSize()) {
        Row(
            Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 6.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            LibrarySegment.entries.forEachIndexed { index, entry ->
                FilterChip(selected = segment == index, onClick = { segment = index }, label = { Text(entry.title) })
            }
        }
        when (LibrarySegment.entries[segment]) {
            LibrarySegment.ARTISTS -> ArtistsList()
            LibrarySegment.ALBUMS -> AlbumsGrid()
            LibrarySegment.SONGS -> SongsList()
            LibrarySegment.GENRES -> GenresList()
            LibrarySegment.PLAYLISTS -> PlaylistsList()
            LibrarySegment.FOLDERS -> FoldersBrowser(null, null)
        }
    }
}

// Sort menu + A↓/Z↑ + a filter box: the toolbar every library list shares.
@Composable
private fun LibraryToolbar(
    filter: String,
    onFilter: (String) -> Unit,
    sorts: List<Pair<String, String>>,
    sort: String,
    order: String,
    onSort: (String, String) -> Unit,
    extra: @Composable () -> Unit = {},
) {
    var menu by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        OutlinedTextField(
            value = filter,
            onValueChange = onFilter,
            placeholder = { Text("Filter") },
            leadingIcon = { Icon(Icons.Default.FilterList, contentDescription = null) },
            trailingIcon = { if (filter.isNotEmpty()) IconButton(onClick = { onFilter("") }) { Icon(Icons.Default.Close, contentDescription = "Clear") } },
            singleLine = true,
            modifier = Modifier.weight(1f),
        )
        Box {
            IconButton(onClick = { menu = true }) { Icon(Icons.AutoMirrored.Filled.Sort, contentDescription = "Sort") }
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                sorts.forEach { (key, label) ->
                    DropdownMenuItem(
                        text = { Text(label) },
                        leadingIcon = { if (key == sort) Icon(Icons.Default.Check, contentDescription = null) },
                        onClick = { menu = false; onSort(key, defaultOrder(key)) },
                    )
                }
            }
        }
        IconButton(onClick = { onSort(sort, if (order == "asc") "desc" else "asc") }) {
            Icon(if (order == "asc") Icons.Default.ArrowUpward else Icons.Default.ArrowDownward, contentDescription = if (order == "asc") "Ascending" else "Descending")
        }
        extra()
    }
}

@Composable
private fun PagedFooter(paged: Paged<*>, scope: CoroutineScope) {
    when {
        paged.loading -> Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(28.dp)) }
        paged.error != null -> ErrorPanel(paged.error.orEmpty()) { paged.error = null; paged.reload(scope) }
        paged.total >= 0 && paged.items.isNotEmpty() -> Text(
            "${paged.total} in all",
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.fillMaxWidth().padding(16.dp),
        )
    }
}


@Composable
private fun ArtistsList() {
    val ui = LocalAudioUi.current
    var filter by rememberSaveable { mutableStateOf("") }
    var sort by rememberSaveable { mutableStateOf("name") }
    var order by rememberSaveable { mutableStateOf("asc") }
    var albumArtistsOnly by rememberSaveable { mutableStateOf(true) }
    val paged = rememberPaged(filter, sort, order, albumArtistsOnly) { offset ->
        ui.api.artists(q = filter.trim(), sort = sort, order = order, albumArtistsOnly = albumArtistsOnly, offset = offset)
    }
    val scope = rememberCoroutineScope()
    Column(Modifier.fillMaxSize()) {
        LibraryToolbar(filter, { filter = it }, artistSorts, sort, order, { s, o -> sort = s; order = o })
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Album artists only", style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
            Switch(checked = albumArtistsOnly, onCheckedChange = { albumArtistsOnly = it })
        }
        LazyVerticalGrid(GridCells.Adaptive(104.dp), Modifier.fillMaxSize(), contentPadding = PaddingValues(12.dp), horizontalArrangement = Arrangement.spacedBy(10.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            itemsIndexed(paged.items, key = { _, artist -> artist.id }) { index, artist ->
                if (index >= paged.items.size - 12) LaunchedEffect(index) { paged.more(scope) }
                ArtistCircle(artist, width = 104.dp) { ui.nav.openArtist(artist.id) }
            }
            item(span = { GridItemSpan(maxLineSpan) }) {
                if (paged.items.isEmpty() && !paged.loading && paged.error == null) EmptyPanel(Icons.Default.Category, "No artists", "Nothing matches.")
                else PagedFooter(paged, scope)
            }
        }
    }
}

@Composable
fun AlbumsGrid(initialSort: String = "title", genre: String? = null, decade: Int? = null, showToolbar: Boolean = true) {
    val ui = LocalAudioUi.current
    var filter by rememberSaveable { mutableStateOf("") }
    var sort by rememberSaveable { mutableStateOf(initialSort) }
    var order by rememberSaveable { mutableStateOf(defaultOrder(initialSort)) }
    var genreFilter by rememberSaveable { mutableStateOf(genre) }
    val genres = rememberLoadable(Unit) { if (genre == null && decade == null) ui.api.genres().sortedByDescending { it.albumCount }.take(16).map { it.name } else emptyList() }
    val paged = rememberPaged(filter, sort, order, genreFilter, decade) { offset ->
        ui.api.albums(q = filter.trim(), genre = genreFilter.orEmpty(), decade = decade, sort = sort, order = order, offset = offset)
    }
    val scope = rememberCoroutineScope()
    var albumMenu by remember { mutableStateOf<Album?>(null) }
    Column(Modifier.fillMaxSize()) {
        if (showToolbar) LibraryToolbar(filter, { filter = it }, albumSorts, sort, order, { s, o -> sort = s; order = o })
        val genreList = genres.value.orEmpty()
        if (genreList.isNotEmpty()) {
            Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                FilterChip(selected = genreFilter == null, onClick = { genreFilter = null }, label = { Text("All genres") })
                genreList.forEach { name -> FilterChip(selected = genreFilter == name, onClick = { genreFilter = if (genreFilter == name) null else name }, label = { Text(name) }) }
            }
        }
        LazyVerticalGrid(GridCells.Adaptive(150.dp), Modifier.fillMaxSize(), contentPadding = PaddingValues(16.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            itemsIndexed(paged.items, key = { _, album -> album.id }) { index, album ->
                if (index >= paged.items.size - 12) LaunchedEffect(index) { paged.more(scope) }
                Box { AlbumCard(album, width = 400.dp, onLongClick = { albumMenu = album }) { ui.nav.openAlbum(album.id) } }
            }
            item(span = { GridItemSpan(maxLineSpan) }) {
                if (paged.items.isEmpty() && !paged.loading && paged.error == null) EmptyPanel(Icons.Default.QueueMusic, "No albums", "Nothing matches.")
                else PagedFooter(paged, scope)
            }
        }
    }
    albumMenu?.let { album -> AlbumQuickMenu(album) { albumMenu = null } }
}

// Long-press on an album: the album-level versions of the track menu.
@Composable
fun AlbumQuickMenu(album: Album, onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(album.title, maxLines = 2, overflow = TextOverflow.Ellipsis) },
        text = {
            Column {
                listOf(
                    "Play" to { ui.launch { ui.playTracks(ui.api.album(album.id).tracks, 0, PlayContext("album", album.id, album.title)) } },
                    "Shuffle" to { ui.launch { ui.playTracks(ui.api.album(album.id).tracks, 0, PlayContext("album", album.id, album.title), shuffle = true) } },
                    "Play next" to { ui.launch { ui.playNext(ui.api.album(album.id).tracks) } },
                    "Add to queue" to { ui.launch { ui.addToQueue(ui.api.album(album.id).tracks) } },
                    "Add to playlist…" to { ui.launch { ui.addToPlaylist = ui.api.album(album.id).tracks.map { it.id } } },
                    "Start radio" to { ui.engine.startRadio("album", album.id, album.title) },
                    "Download" to { ui.launch { ui.download(ui.api.album(album.id).tracks) } },
                    (if (album.loved) "Unlove" else "Love") to { ui.launch { ui.api.love("albums", album.id, !album.loved); ui.libraryVersion++ } },
                ).forEach { (label, action) ->
                    Text(
                        label,
                        style = MaterialTheme.typography.bodyLarge,
                        modifier = Modifier.fillMaxWidth().clickable { action(); onDismiss() }.padding(vertical = 12.dp),
                    )
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Close") } },
    )
}

@Composable
fun SongsList(genre: String? = null, decade: Int? = null, initialSort: String = "title", contextName: String = "Songs") {
    val ui = LocalAudioUi.current
    var filter by rememberSaveable { mutableStateOf("") }
    var sort by rememberSaveable { mutableStateOf(initialSort) }
    var order by rememberSaveable { mutableStateOf(defaultOrder(initialSort)) }
    var lovedOnly by rememberSaveable { mutableStateOf(false) }
    val paged = rememberPaged(filter, sort, order, lovedOnly, genre, decade) { offset ->
        ui.api.tracks(q = filter.trim(), genre = genre.orEmpty(), decade = decade, loved = lovedOnly, sort = sort, order = order, offset = offset)
    }
    val scope = rememberCoroutineScope()
    Column(Modifier.fillMaxSize()) {
        LibraryToolbar(filter, { filter = it }, songSorts, sort, order, { s, o -> sort = s; order = o })
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 2.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(selected = lovedOnly, onClick = { lovedOnly = !lovedOnly }, label = { Text("Loved") })
            TextButton(onClick = {
                // Up to 500 at a time from what's listed - plenty for a shuffle.
                ui.launch {
                    val all = ui.api.tracks(q = filter.trim(), genre = genre.orEmpty(), decade = decade, loved = lovedOnly, sort = sort, order = order, limit = 500).items
                    ui.playTracks(all, 0, PlayContext("queue", null, contextName), shuffle = true)
                }
            }) { Text("Shuffle these") }
        }
        LazyColumn(Modifier.fillMaxSize()) {
            itemsIndexed(paged.items, key = { index, track -> "$index${track.id}" }) { index, track ->
                if (index >= paged.items.size - 15) LaunchedEffect(index) { paged.more(scope) }
                TrackRow(track, showAlbum = true, onMore = { ui.trackMenu = TrackMenuTarget(track) }) {
                    ui.playTracks(paged.items.toList(), index, PlayContext("queue", null, contextName))
                }
            }
            item {
                if (paged.items.isEmpty() && !paged.loading && paged.error == null) EmptyPanel(Icons.Default.QueueMusic, "No songs", "Nothing matches.")
                else PagedFooter(paged, scope)
            }
        }
    }
}

@Composable
private fun GenresList() {
    val ui = LocalAudioUi.current
    val genres = rememberLoadable(Unit) { ui.api.genres() }
    LoadableContent(genres) { list ->
        LazyColumn(Modifier.fillMaxSize()) {
            items(list, key = { it.name }) { genre ->
                Row(
                    Modifier.fillMaxWidth().clickable { ui.nav.openGenre(genre.name) }.padding(horizontal = 16.dp, vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    MosaicArt(genre.artUrls, Modifier.size(52.dp), seed = genre.name, sizePx = 156)
                    Spacer(Modifier.width(12.dp))
                    Column(Modifier.weight(1f)) {
                        Text(genre.name, fontWeight = FontWeight.Medium)
                        Text("${genre.albumCount} albums · ${genre.trackCount} songs", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    IconButton(onClick = { ui.engine.startRadio("genre", genre.name, genre.name) }) {
                        Icon(Icons.Default.AutoAwesome, contentDescription = "${genre.name} radio")
                    }
                }
            }
        }
    }
}

@Composable
private fun PlaylistsList() {
    val ui = LocalAudioUi.current
    val playlists = rememberLoadable(Unit) { ui.api.playlists() }
    var creating by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { creating = true }, modifier = Modifier.weight(1f)) {
                Icon(Icons.Default.Add, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(4.dp))
                Text("New playlist")
            }
            OutlinedButton(onClick = { ui.nav.open("music/smart/new") }, modifier = Modifier.weight(1f)) {
                Icon(Icons.Default.AutoAwesome, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(4.dp))
                Text("Smart playlist")
            }
        }
        LoadableContent(playlists) { list ->
            if (list.isEmpty()) {
                EmptyPanel(Icons.Default.QueueMusic, "No playlists yet", "Make one, or add songs to a new one from any song's menu.")
                return@LoadableContent
            }
            LazyColumn(Modifier.fillMaxSize()) {
                val (mine, others) = list.sortedWith(compareByDescending<com.sentinelcore.android.audio.Playlist> { it.pinned }.thenBy { it.name.lowercase() }).partition { it.mine }
                if (mine.isNotEmpty()) item { com.sentinelcore.android.ui.audio.SectionTitle("Yours") }
                items(mine, key = { it.id }) { PlaylistRow(it) }
                if (others.isNotEmpty()) item { com.sentinelcore.android.ui.audio.SectionTitle("Shared in the household") }
                items(others, key = { it.id }) { PlaylistRow(it) }
            }
        }
    }
    if (creating) NewPlaylistDialog(onDismiss = { creating = false })
}

@Composable
private fun PlaylistRow(playlist: com.sentinelcore.android.audio.Playlist) {
    val ui = LocalAudioUi.current
    Row(
        Modifier.fillMaxWidth().clickable { ui.nav.openPlaylist(playlist.id) }.padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        MosaicArt(playlist.artUrls, Modifier.size(56.dp), seed = playlist.name, sizePx = 168)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(playlist.name, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(
                listOfNotNull(
                    playlistSubtitle(playlist),
                    "Pinned".takeIf { playlist.pinned },
                    "Collaborative".takeIf { playlist.collaborative },
                    "Shared".takeIf { playlist.shared && playlist.mine },
                ).joinToString(" · "),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

@Composable
fun NewPlaylistDialog(onDismiss: () -> Unit, trackIds: List<String> = emptyList()) {
    val ui = LocalAudioUi.current
    var name by remember { mutableStateOf("") }
    var description by remember { mutableStateOf("") }
    var shared by remember { mutableStateOf(false) }
    var collaborative by remember { mutableStateOf(false) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("New playlist") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(value = name, onValueChange = { name = it }, label = { Text("Name") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(value = description, onValueChange = { description = it }, label = { Text("Description (optional)") }, modifier = Modifier.fillMaxWidth())
                SwitchLine("Shared with the household", shared) { shared = it; if (!it) collaborative = false }
                SwitchLine("Anyone can add and reorder", collaborative) { collaborative = it; if (it) shared = true }
            }
        },
        confirmButton = {
            TextButton(enabled = name.isNotBlank(), onClick = {
                ui.launch("Couldn't make the playlist.") {
                    val created = ui.api.createPlaylist(name.trim(), description.trim(), shared, collaborative, trackIds = trackIds)
                    ui.libraryVersion++
                    ui.nav.openPlaylist(created.id)
                }
                onDismiss()
            }) { Text("Create") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

@Composable
fun SwitchLine(label: String, checked: Boolean, detail: String? = null, enabled: Boolean = true, onChange: (Boolean) -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(enabled = enabled) { onChange(!checked) }.padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(label, style = MaterialTheme.typography.bodyMedium)
            detail?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        Switch(checked = checked, onCheckedChange = onChange, enabled = enabled)
    }
}

// Browse by folder (§4 folders): the libraries first, then their folders and the songs in each.
@Composable
fun FoldersBrowser(libraryId: String?, path: String?) {
    val ui = LocalAudioUi.current
    var location by rememberSaveable { mutableStateOf(libraryId to path) }
    val listing = rememberLoadable(location) { ui.api.folders(location.first, location.second) }
    Column(Modifier.fillMaxSize()) {
        if (location.first != null) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = {
                    val current = listing.value
                    location = if (current?.parent == null || location.second.isNullOrBlank()) (null to null) else (location.first to current.parent)
                }) { Text("↑ Up") }
                Text(location.second?.ifBlank { null } ?: "Library root", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        LoadableContent(listing) { value ->
            LazyColumn(Modifier.fillMaxSize()) {
                items(value.folders, key = { "f" + it.path }) { folder ->
                    Row(
                        Modifier.fillMaxWidth().clickable {
                            location = if (location.first == null) (folder.path to "") else (location.first to folder.path)
                        }.padding(horizontal = 16.dp, vertical = 12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Icon(Icons.Default.Folder, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                        Spacer(Modifier.width(14.dp))
                        Text(folder.name, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                        if (folder.trackCount > 0) Text("${folder.trackCount}", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                if (value.tracks.isNotEmpty()) {
                    item {
                        TextButton(onClick = { ui.playTracks(value.tracks, 0, PlayContext("queue", null, value.path.substringAfterLast('/').ifBlank { "Folder" })) }, modifier = Modifier.padding(horizontal = 8.dp)) {
                            Text("Play this folder (${value.tracks.size})")
                        }
                    }
                    itemsIndexed(value.tracks, key = { _, track -> "t" + track.id }) { index, track ->
                        TrackRow(track, number = track.trackNumber ?: (index + 1), showArt = false, onMore = { ui.trackMenu = TrackMenuTarget(track) }) {
                            ui.playTracks(value.tracks, index, PlayContext("queue", null, value.path.substringAfterLast('/')))
                        }
                    }
                }
                if (value.folders.isEmpty() && value.tracks.isEmpty()) {
                    item { EmptyPanel(Icons.Default.Folder, "Empty folder", "No music in here.") }
                }
            }
        }
    }
}


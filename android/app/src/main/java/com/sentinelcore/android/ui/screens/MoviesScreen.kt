package com.sentinelcore.android.ui.screens

import android.graphics.Bitmap
import android.net.Uri
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Movie
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import com.sentinelcore.android.data.MediaApi
import com.sentinelcore.android.data.MediaDownloadList
import com.sentinelcore.android.data.MediaDownloadStatus
import com.sentinelcore.android.data.MediaHome
import com.sentinelcore.android.data.MediaItemSummary
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.EmptyState
import com.sentinelcore.android.ui.theme.SentinelBackground
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.launch

// The Movies tab: a family media library in the style of Jellyfin. The contract is
// core/docs/MOVIES.md. Home shows shelves (continue watching, My List, recently added, most played,
// genres…); Movies and TV Shows are paged poster grids with sort, genre, unwatched and My List
// filters; search spans both.

const val MOVIE_DETAIL_ROUTE = "movies/item/{id}"
const val MOVIE_PLAYER_ROUTE = "movies/play/{id}?start={start}"

fun movieDetailRoute(id: String): String = "movies/item/${Uri.encode(id)}"

// start null resumes from the saved point; 0 starts over.
fun moviePlayerRoute(id: String, start: Double?): String =
    "movies/play/${Uri.encode(id)}" + (start?.let { "?start=${MediaApi.formatSeconds(it)}" } ?: "")

private enum class MoviesPane(val title: String, val kind: String?) {
    HOME("Home", null),
    MOVIES("Movies", "movie"),
    SHOWS("TV Shows", "show"),
}

private val SORTS = listOf(
    "title" to "Title",
    "added" to "Date added",
    "year" to "Year",
    "rating" to "Rating",
    "plays" to "Most played",
)

internal data class LibraryFilter(
    val sort: String = "title",
    val order: String = "asc",
    val genre: String? = null,
    val unwatched: Boolean = false,
    // Only this account's My List.
    val inList: Boolean = false,
)

// One paged grid's worth of results. Replaced only when a response arrives, so coming back to the
// tab from a detail screen refreshes progress without the grid blanking or losing its scroll.
internal class LibraryGrid {
    val items = mutableStateListOf<MediaItemSummary>()
    var total by mutableIntStateOf(0)
    var genres by mutableStateOf<List<String>>(emptyList())
    var loading by mutableStateOf(false)
    var error by mutableStateOf<String?>(null)
    var loadedKey by mutableStateOf<String?>(null)
    var generation = 0
    var job: Job? = null
}

// Kept per back-stack entry (see viewModel() below), so the shelves and grids survive a trip to a
// detail screen and a rotation. Data calls go straight through to SentinelViewModel.
internal class MoviesBrowseModel(private val app: SentinelViewModel) : ViewModel() {
    var home by mutableStateOf<MediaHome?>(null)
    var homeLoading by mutableStateOf(false)
    var homeError by mutableStateOf<String?>(null)
    var surprising by mutableStateOf(false)
    private val grids = mutableMapOf<String, LibraryGrid>()
    val filters = mutableMapOf<String, LibraryFilter>()

    // Movie downloads: null status means the feature is off (or the server predates it), which
    // hides "+ Add", Downloads and the banner. `downloads` feeds the Home banner.
    var downloadStatus by mutableStateOf<MediaDownloadStatus?>(null)
    var downloads by mutableStateOf<MediaDownloadList?>(null)
    private val downloadStates = mutableMapOf<String, String>()

    fun loadDownloads() {
        viewModelScope.launch {
            downloadStatus = runCatching { app.mediaDownloadStatus() }.getOrNull()
            if (downloadStatus?.canDownload == true) pollDownloads() else downloads = null
        }
    }

    // One look at the queue. A download that has just become "done" is now in the library, so
    // Home reloads to show it under Recently Added.
    suspend fun pollDownloads() {
        val list = runCatching { app.mediaDownloads() }.getOrNull() ?: return
        var landed = false
        for (download in list.active + list.recent) {
            val before = downloadStates[download.id]
            if (before != null && before != download.state && download.isDone) landed = true
            downloadStates[download.id] = download.state
        }
        downloads = list
        if (landed) loadHome()
    }

    fun grid(kind: String?): LibraryGrid = grids.getOrPut(kind ?: "all") { LibraryGrid() }

    fun filter(kind: String?): LibraryFilter = filters[kind ?: "all"] ?: LibraryFilter()

    fun setFilter(kind: String?, filter: LibraryFilter) {
        filters[kind ?: "all"] = filter
    }

    fun loadHome() {
        viewModelScope.launch {
            homeLoading = true
            runCatching { app.mediaHome() }
                .onSuccess { home = it; homeError = null }
                .onFailure { homeError = it.message ?: "Could not load the library." }
            homeLoading = false
        }
    }

    // "Surprise me": opens a random title's page, or shows the server's reason (a 404 when nothing
    // matches) where Home shows its other errors.
    fun surprise(onItem: (MediaItemSummary) -> Unit) {
        if (surprising) return
        viewModelScope.launch {
            surprising = true
            runCatching { app.mediaRandom() }
                .onSuccess { homeError = null; onItem(it) }
                .onFailure { homeError = it.message ?: "Could not pick something." }
            surprising = false
        }
    }

    // Loads the first page for these parameters. The same parameters again (coming back to the
    // screen, or Refresh) reload in place; different ones clear the grid first.
    fun reload(kind: String?, query: String, filter: LibraryFilter) {
        val grid = grid(kind)
        val key = listOf(kind, query.trim(), filter).toString()
        grid.job?.cancel()
        val generation = ++grid.generation
        if (grid.loadedKey != key) {
            grid.items.clear()
            grid.total = 0
        }
        grid.loading = true
        grid.job = viewModelScope.launch {
            runCatching {
                app.mediaItems(kind, query, filter.genre, filter.sort, filter.order, filter.unwatched, offset = 0, list = filter.inList)
            }.onSuccess { page ->
                if (generation != grid.generation) return@onSuccess
                grid.items.clear()
                grid.items.addAll(page.items)
                grid.total = page.total
                if (page.genres.isNotEmpty() || filter.genre == null) grid.genres = page.genres
                grid.error = null
                grid.loadedKey = key
            }.onFailure {
                if (generation == grid.generation) grid.error = it.message ?: "Could not load the library."
            }
            if (generation == grid.generation) grid.loading = false
        }
    }

    fun loadMore(kind: String?, query: String, filter: LibraryFilter) {
        val grid = grid(kind)
        // A failed page waits for Refresh rather than retrying itself in a loop.
        if (grid.loading || grid.error != null || grid.items.size >= grid.total || grid.loadedKey == null) return
        val generation = grid.generation
        val offset = grid.items.size
        grid.loading = true
        grid.job = viewModelScope.launch {
            runCatching {
                app.mediaItems(kind, query, filter.genre, filter.sort, filter.order, filter.unwatched, offset = offset, list = filter.inList)
            }.onSuccess { page ->
                if (generation != grid.generation) return@onSuccess
                val known = grid.items.map { it.id }.toHashSet()
                grid.items.addAll(page.items.filter { it.id !in known })
                grid.total = page.total
            }.onFailure {
                if (generation == grid.generation) grid.error = it.message ?: "Could not load more."
            }
            if (generation == grid.generation) grid.loading = false
        }
    }
}

@Composable
fun MoviesScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    onItem: (String) -> Unit,
    onPlay: (String, Double?) -> Unit,
    // Settings -> Movies & TV libraries, offered to an admin when there are no libraries yet.
    onManageLibraries: () -> Unit = {},
    // Movie downloads: "+ Add" and the Downloads queue, offered only when the server allows it.
    onAddDownload: () -> Unit = {},
    onDownloads: () -> Unit = {},
) {
    val browse: MoviesBrowseModel = viewModel { MoviesBrowseModel(model) }
    val canDownload = browse.downloadStatus?.canDownload == true
    var paneIndex by rememberSaveable { mutableIntStateOf(0) }
    val pane = MoviesPane.entries[paneIndex]
    var query by rememberSaveable { mutableStateOf("") }
    var searchOpen by rememberSaveable { mutableStateOf(false) }
    var refreshTick by remember { mutableIntStateOf(0) }
    val isAdmin = state.authStatus?.role?.equals("Admin", ignoreCase = true) != false

    // Whether downloads are on is asked each time the tab comes into view and on Refresh.
    LaunchedEffect(refreshTick) { browse.loadDownloads() }

    // Tapping a shelf or poster: a show opens its page; a movie or episode its own.
    val open: (MediaItemSummary) -> Unit = { onItem(it.id) }

    Column(Modifier.fillMaxSize()) {
        ScreenHeader(
            "Movies",
            state,
            onRefresh = { refreshTick++ },
            actions = {
                if (pane == MoviesPane.HOME && query.isBlank()) {
                    IconButton(onClick = { browse.surprise(open) }, enabled = !browse.surprising) {
                        Icon(Icons.Default.Shuffle, contentDescription = "Surprise me")
                    }
                }
                if (canDownload) {
                    IconButton(onClick = onAddDownload) {
                        Icon(Icons.Default.Add, contentDescription = "Add a movie or show")
                    }
                    IconButton(onClick = onDownloads) {
                        Icon(Icons.Default.Download, contentDescription = "Downloads")
                    }
                }
                IconButton(onClick = {
                    searchOpen = !searchOpen
                    if (!searchOpen) query = ""
                }) {
                    Icon(if (searchOpen) Icons.Default.Close else Icons.Default.Search, contentDescription = "Search")
                }
            },
        )
        if (searchOpen || query.isNotBlank()) {
            OutlinedTextField(
                value = query,
                onValueChange = { query = it },
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
                singleLine = true,
                placeholder = { Text("Search movies and shows") },
                leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
                trailingIcon = {
                    if (query.isNotEmpty()) {
                        IconButton(onClick = { query = "" }) { Icon(Icons.Default.Close, contentDescription = "Clear") }
                    }
                },
            )
        }
        TabRow(selectedTabIndex = paneIndex) {
            MoviesPane.entries.forEachIndexed { index, entry ->
                Tab(selected = paneIndex == index, onClick = { paneIndex = index }, text = { Text(entry.title, maxLines = 1) })
            }
        }

        when {
            pane == MoviesPane.HOME && query.isBlank() -> MoviesHome(
                browse = browse,
                model = model,
                isAdmin = isAdmin,
                refreshTick = refreshTick,
                onManageLibraries = onManageLibraries,
                onItem = open,
                onPlay = onPlay,
                onDownloads = onDownloads,
            )
            else -> LibraryGridPane(
                browse = browse,
                model = model,
                kind = pane.kind,
                query = query,
                refreshTick = refreshTick,
                onItem = open,
            )
        }
    }
}

// ---------------- Home ----------------

@Composable
private fun MoviesHome(
    browse: MoviesBrowseModel,
    model: SentinelViewModel,
    isAdmin: Boolean,
    refreshTick: Int,
    onManageLibraries: () -> Unit,
    onItem: (MediaItemSummary) -> Unit,
    onPlay: (String, Double?) -> Unit,
    onDownloads: () -> Unit,
) {
    LaunchedEffect(refreshTick) { browse.loadHome() }
    val home = browse.home
    // Follow the queue while something is downloading, for the banner (and to reload Home when
    // a download lands in the library).
    val activeDownloads = browse.downloads?.active.orEmpty()
    val downloading = browse.downloadStatus?.canDownload == true && activeDownloads.isNotEmpty()
    LaunchedEffect(downloading) {
        while (downloading) {
            delay(2_500)
            browse.pollDownloads()
        }
    }

    Box(Modifier.fillMaxSize()) {
        when {
            home == null && browse.homeLoading -> CircularProgressIndicator(Modifier.align(Alignment.Center), color = SentinelGreen)
            home == null -> ErrorPanel(
                message = browse.homeError ?: "Could not load the library.",
                onRetry = { browse.loadHome() },
                modifier = Modifier.align(Alignment.Center),
            )
            home.libraries.isEmpty() -> EmptyLibrary(isAdmin, onManageLibraries, Modifier.align(Alignment.Center))
            else -> LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(bottom = 24.dp),
                verticalArrangement = Arrangement.spacedBy(20.dp),
            ) {
                if (browse.homeLoading) item { LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen) }
                if (downloading) {
                    item { DownloadBanner(activeDownloads, onClick = onDownloads, modifier = Modifier.padding(top = 8.dp)) }
                }
                browse.homeError?.let { error -> item { Text(error, color = SentinelRed, modifier = Modifier.padding(horizontal = 16.dp)) } }
                home.featured?.let { featured ->
                    item { HeroBanner(model, featured, onItem = onItem, onPlay = onPlay) }
                }
                if (home.continueWatching.isNotEmpty()) {
                    item {
                        Shelf("Continue Watching") {
                            items(home.continueWatching, key = { "cw-" + it.id }) { entry ->
                                WideCard(model, entry, onClick = { onPlay(entry.id, null) })
                            }
                        }
                    }
                }
                if (home.myList.isNotEmpty()) {
                    item { PosterShelf(model, "My List", home.myList, keyPrefix = "ml-", onItem = onItem) }
                }
                if (home.recentMovies.isNotEmpty()) {
                    item { PosterShelf(model, "Recently Added Movies", home.recentMovies, keyPrefix = "rm-", onItem = onItem) }
                }
                if (home.recentShows.isNotEmpty()) {
                    item { PosterShelf(model, "Recently Added Shows", home.recentShows, keyPrefix = "rs-", onItem = onItem) }
                }
                if (home.recentEpisodes.isNotEmpty()) {
                    item {
                        Shelf("New Episodes") {
                            items(home.recentEpisodes, key = { "re-" + it.id }) { entry ->
                                WideCard(model, entry, onClick = { onItem(entry) })
                            }
                        }
                    }
                }
                if (home.mostPlayed.isNotEmpty()) {
                    item { PosterShelf(model, "Most Played", home.mostPlayed, keyPrefix = "mp-", onItem = onItem) }
                }
                if (home.topRated.isNotEmpty()) {
                    item { PosterShelf(model, "Top Rated", home.topRated, keyPrefix = "tr-", onItem = onItem) }
                }
                // The index keeps two shelves' card keys apart even if a server ever repeated a genre.
                home.genreShelves.forEachIndexed { index, shelf ->
                    item {
                        PosterShelf(model, shelf.genre, shelf.items, keyPrefix = "g$index-", onItem = onItem)
                    }
                }
                if (home.recentlyWatched.isNotEmpty()) {
                    item { PosterShelf(model, "Watch Again", home.recentlyWatched, keyPrefix = "wa-", onItem = onItem) }
                }
                if (home.featured == null && home.continueWatching.isEmpty() && home.myList.isEmpty() &&
                    home.recentMovies.isEmpty() && home.recentShows.isEmpty() && home.recentEpisodes.isEmpty() &&
                    home.mostPlayed.isEmpty() && home.topRated.isEmpty() && home.genreShelves.isEmpty() &&
                    home.recentlyWatched.isEmpty()
                ) {
                    item {
                        EmptyState(
                            title = "Nothing here yet",
                            message = "The library is still being scanned, or its folders have no videos in them yet.",
                            modifier = Modifier.fillMaxWidth(),
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun EmptyLibrary(isAdmin: Boolean, onManageLibraries: () -> Unit, modifier: Modifier = Modifier) {
    Column(modifier, horizontalAlignment = Alignment.CenterHorizontally) {
        EmptyState(
            title = "No libraries yet",
            message = if (isAdmin) {
                "Add your movie and TV folders in Settings → Movies & TV libraries."
            } else {
                "An admin can add movie and TV folders in Settings → Movies & TV libraries."
            },
        )
        if (isAdmin) {
            Button(onClick = onManageLibraries) { Text("Add a library") }
        }
    }
}

@Composable
internal fun ErrorPanel(message: String, onRetry: () -> Unit, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier.padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(message, color = SentinelRed, textAlign = TextAlign.Center)
        OutlinedButton(onClick = onRetry) { Text("Try again") }
    }
}

@Composable
private fun Shelf(title: String, content: androidx.compose.foundation.lazy.LazyListScope.() -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(
            title,
            style = MaterialTheme.typography.titleMedium,
            fontWeight = FontWeight.SemiBold,
            modifier = Modifier.padding(horizontal = 16.dp),
        )
        LazyRow(
            contentPadding = PaddingValues(horizontal = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            content = content,
        )
    }
}

// A row of posters; keyPrefix keeps its keys apart from the other rows, which can hold the same titles.
@Composable
private fun PosterShelf(
    model: SentinelViewModel,
    title: String,
    entries: List<MediaItemSummary>,
    keyPrefix: String,
    onItem: (MediaItemSummary) -> Unit,
) {
    Shelf(title) {
        items(entries, key = { keyPrefix + it.id }) { entry ->
            PosterCard(model, entry, onClick = { onItem(entry) }, modifier = Modifier.width(120.dp))
        }
    }
}

@Composable
private fun HeroBanner(
    model: SentinelViewModel,
    item: MediaItemSummary,
    onItem: (MediaItemSummary) -> Unit,
    onPlay: (String, Double?) -> Unit,
) {
    Box(
        Modifier
            .fillMaxWidth()
            .height(240.dp)
            .clickable { onItem(item) },
    ) {
        PosterImage(
            model = model,
            path = item.backdropUrl ?: item.posterUrl,
            title = item.title,
            modifier = Modifier.fillMaxSize(),
            maxWidthPx = 1280,
        )
        Box(
            Modifier
                .fillMaxSize()
                .background(Brush.verticalGradient(0.35f to Color.Transparent, 1f to SentinelBackground)),
        )
        Column(
            Modifier.align(Alignment.BottomStart).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Text(
                item.title,
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = FontWeight.Bold,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            metaLine(item)?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim) }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                val playId = if (item.isShow) item.nextEpisodeId else item.id
                if (playId != null) {
                    Button(onClick = { onPlay(playId, null) }) {
                        Icon(Icons.Default.PlayArrow, contentDescription = null)
                        Text(if (item.progressFraction != null) "Resume" else "Play")
                    }
                }
                OutlinedButton(onClick = { onItem(item) }) { Text("More info") }
            }
        }
    }
}

// "2009 · PG-13 · 1h 42m" - whatever parts the item has.
internal fun metaLine(item: MediaItemSummary): String? {
    val parts = buildList {
        item.year?.let { add(it.toString()) }
        item.certification?.let { add(it) }
        if (item.isShow) {
            item.seasonCount?.let { add(if (it == 1) "1 season" else "$it seasons") }
        } else {
            item.runtimeMinutes?.takeIf { it > 0 }?.let { add(com.sentinelcore.android.data.MediaPlaybackMath.formatRuntime(it)) }
                ?: item.durationSeconds?.takeIf { it > 0 }?.let {
                    add(com.sentinelcore.android.data.MediaPlaybackMath.formatRuntime((it / 60).toInt()))
                }
        }
        item.rating?.takeIf { it > 0 }?.let { add("★ %.1f".format(it)) }
    }
    return parts.joinToString(" · ").ifBlank { null }
}

// ---------------- Grids ----------------

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun LibraryGridPane(
    browse: MoviesBrowseModel,
    model: SentinelViewModel,
    kind: String?,
    query: String,
    refreshTick: Int,
    onItem: (MediaItemSummary) -> Unit,
) {
    val grid = browse.grid(kind)
    var filter by remember(kind) { mutableStateOf(browse.filter(kind)) }
    val gridState = rememberLazyGridState()

    // Debounced so typing doesn't fire a request per letter; filters apply at once.
    LaunchedEffect(kind, query, filter, refreshTick) {
        if (query.isNotBlank() && grid.loadedKey != null) delay(300)
        browse.setFilter(kind, filter)
        browse.reload(kind, query, filter)
    }
    // Next page once the last rows come into view - or straight away, when a page doesn't fill
    // a tablet's screen and there is nothing to scroll.
    LaunchedEffect(gridState, kind, query, filter) {
        snapshotFlow {
            Triple(gridState.layoutInfo.visibleItemsInfo.lastOrNull()?.index ?: 0, grid.items.size, grid.loading)
        }
            .distinctUntilChanged()
            .collect { (last, count, loading) ->
                if (!loading && count > 0 && last >= count - 12) browse.loadMore(kind, query, filter)
            }
    }

    Column(Modifier.fillMaxSize()) {
        FilterBar(
            filter = filter,
            genres = grid.genres,
            total = grid.total,
            noun = when (kind) {
                "movie" -> "movies"
                "show" -> "shows"
                else -> "results"
            },
            onChange = { filter = it },
        )
        if (grid.loading) LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
        Box(Modifier.fillMaxSize()) {
            when {
                grid.items.isEmpty() && grid.error != null -> ErrorPanel(
                    grid.error.orEmpty(),
                    onRetry = { browse.reload(kind, query, filter) },
                    modifier = Modifier.align(Alignment.Center),
                )
                grid.items.isEmpty() && !grid.loading && grid.loadedKey != null -> EmptyState(
                    title = if (query.isNotBlank()) "No matches" else "Nothing here",
                    message = when {
                        query.isNotBlank() -> "Nothing matches \"${query.trim()}\"."
                        filter.inList && !filter.unwatched && filter.genre == null -> "Nothing is on your list yet."
                        filter.unwatched || filter.inList || filter.genre != null -> "Nothing matches these filters."
                        else -> "Nothing has been added to this library yet."
                    },
                    modifier = Modifier.align(Alignment.Center),
                )
                else -> LazyVerticalGrid(
                    columns = GridCells.Adaptive(104.dp),
                    state = gridState,
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(16.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    items(grid.items, key = { it.id }) { entry ->
                        PosterCard(model, entry, onClick = { onItem(entry) })
                    }
                    if (grid.error != null && grid.items.isNotEmpty()) {
                        item(span = { GridItemSpan(maxLineSpan) }) {
                            Text(grid.error.orEmpty(), color = SentinelRed, textAlign = TextAlign.Center)
                        }
                    }
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun FilterBar(
    filter: LibraryFilter,
    genres: List<String>,
    total: Int,
    noun: String,
    onChange: (LibraryFilter) -> Unit,
) {
    var sortMenu by remember { mutableStateOf(false) }
    var genreMenu by remember { mutableStateOf(false) }
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .horizontalScroll(rememberScrollState())
            .padding(horizontal = 16.dp, vertical = 6.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box {
            FilterChip(
                selected = false,
                onClick = { sortMenu = true },
                label = { Text("Sort: " + (SORTS.firstOrNull { it.first == filter.sort }?.second ?: filter.sort)) },
            )
            DropdownMenu(sortMenu, onDismissRequest = { sortMenu = false }) {
                SORTS.forEach { (value, label) ->
                    DropdownMenuItem(
                        text = { Text(label) },
                        leadingIcon = { if (value == filter.sort) Icon(Icons.Default.Check, contentDescription = null) },
                        onClick = {
                            sortMenu = false
                            onChange(filter.copy(sort = value, order = MediaApi.defaultOrder(value)))
                        },
                    )
                }
            }
        }
        IconButton(onClick = { onChange(filter.copy(order = if (filter.order == "asc") "desc" else "asc")) }) {
            Icon(
                if (filter.order == "asc") Icons.Default.ArrowUpward else Icons.Default.ArrowDownward,
                contentDescription = if (filter.order == "asc") "Ascending" else "Descending",
            )
        }
        Box {
            FilterChip(
                selected = filter.genre != null,
                onClick = { genreMenu = true },
                label = { Text(filter.genre ?: "All genres") },
            )
            DropdownMenu(genreMenu, onDismissRequest = { genreMenu = false }) {
                DropdownMenuItem(
                    text = { Text("All genres") },
                    onClick = { genreMenu = false; onChange(filter.copy(genre = null)) },
                )
                genres.forEach { genre ->
                    DropdownMenuItem(
                        text = { Text(genre) },
                        leadingIcon = { if (genre == filter.genre) Icon(Icons.Default.Check, contentDescription = null) },
                        onClick = { genreMenu = false; onChange(filter.copy(genre = genre)) },
                    )
                }
            }
        }
        FilterChip(
            selected = filter.unwatched,
            onClick = { onChange(filter.copy(unwatched = !filter.unwatched)) },
            label = { Text("Unwatched") },
        )
        FilterChip(
            selected = filter.inList,
            onClick = { onChange(filter.copy(inList = !filter.inList)) },
            label = { Text("My List") },
        )
        if (total > 0) {
            Text("$total $noun", style = MaterialTheme.typography.labelMedium, color = SentinelTextDim)
        }
    }
}

// ---------------- Cards and artwork ----------------

@Composable
internal fun PosterCard(
    model: SentinelViewModel,
    item: MediaItemSummary,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier.clickable(onClick = onClick),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Box(
            Modifier
                .fillMaxWidth()
                .aspectRatio(2f / 3f)
                .clip(RoundedCornerShape(8.dp)),
        ) {
            PosterImage(model, item.posterUrl, item.title, Modifier.fillMaxSize())
            WatchBadge(item, Modifier.align(Alignment.TopEnd).padding(6.dp))
            item.progressFraction?.let { fraction ->
                LinearProgressIndicator(
                    progress = { fraction },
                    modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(4.dp),
                    color = SentinelGreen,
                    trackColor = Color.Black.copy(alpha = 0.5f),
                )
            }
        }
        Text(item.title, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
        val sub = item.episodeCode ?: item.year?.toString()
        if (sub != null) Text(sub, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim, maxLines = 1)
    }
}

// Landscape card for episodes and Continue Watching: the episode still (or the backdrop), the
// progress bar, and "Show · S1 · E3" underneath.
@Composable
internal fun WideCard(
    model: SentinelViewModel,
    item: MediaItemSummary,
    onClick: () -> Unit,
    modifier: Modifier = Modifier.width(220.dp),
) {
    Column(
        modifier = modifier.clickable(onClick = onClick),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Box(
            Modifier
                .fillMaxWidth()
                .aspectRatio(16f / 9f)
                .clip(RoundedCornerShape(8.dp)),
        ) {
            PosterImage(
                model,
                item.thumbUrl ?: item.backdropUrl ?: item.posterUrl,
                item.showTitle ?: item.title,
                Modifier.fillMaxSize(),
                maxWidthPx = 640,
            )
            WatchBadge(item, Modifier.align(Alignment.TopEnd).padding(6.dp))
            item.progressFraction?.let { fraction ->
                LinearProgressIndicator(
                    progress = { fraction },
                    modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(4.dp),
                    color = SentinelGreen,
                    trackColor = Color.Black.copy(alpha = 0.5f),
                )
            }
        }
        Text(
            item.showTitle ?: item.title,
            style = MaterialTheme.typography.bodyMedium,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        val sub = if (item.isEpisode) listOfNotNull(item.episodeCode, item.title).joinToString(" · ") else item.year?.toString()
        if (!sub.isNullOrBlank()) {
            Text(sub, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

// A check when watched; on a show, how many episodes are left.
@Composable
private fun WatchBadge(item: MediaItemSummary, modifier: Modifier = Modifier) {
    when {
        item.isWatched -> Box(
            modifier.size(22.dp).background(SentinelGreen, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.Default.Check, contentDescription = "Watched", tint = Color.Black, modifier = Modifier.size(16.dp))
        }
        item.isShow && (item.unwatchedEpisodeCount ?: 0) > 0 && (item.watchedEpisodeCount ?: 0) > 0 -> Box(
            modifier.background(SentinelCyan, RoundedCornerShape(11.dp)).padding(horizontal = 7.dp, vertical = 2.dp),
        ) {
            Text(
                "${item.unwatchedEpisodeCount}",
                color = Color.Black,
                style = MaterialTheme.typography.labelSmall,
                fontWeight = FontWeight.Bold,
            )
        }
    }
}

// Artwork without the camera watermark SentinelRemoteImage draws, decoded at about maxWidthPx so
// a grid of TMDB posters stays light. A text card stands in when there is no artwork.
@Composable
internal fun PosterImage(
    model: SentinelViewModel,
    path: String?,
    title: String,
    modifier: Modifier = Modifier,
    maxWidthPx: Int = 360,
    contentScale: ContentScale = ContentScale.Crop,
) {
    var bitmap by remember(path) { mutableStateOf<Bitmap?>(null) }
    var finished by remember(path) { mutableStateOf(path == null) }
    LaunchedEffect(path, maxWidthPx) {
        bitmap = path?.let { model.loadImage(it, maxWidthPx) }
        finished = true
    }
    Box(
        modifier = modifier.background(MaterialTheme.colorScheme.surfaceVariant),
        contentAlignment = Alignment.Center,
    ) {
        val image = bitmap
        when {
            image != null -> Image(
                bitmap = image.asImageBitmap(),
                contentDescription = title,
                modifier = Modifier.fillMaxSize(),
                contentScale = contentScale,
            )
            !finished -> Unit
            else -> Column(
                Modifier.padding(10.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Icon(Icons.Default.Movie, contentDescription = null, tint = SentinelTextDim, modifier = Modifier.size(24.dp))
                Text(
                    title,
                    style = MaterialTheme.typography.labelLarge,
                    textAlign = TextAlign.Center,
                    color = SentinelTextDim,
                    maxLines = 4,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

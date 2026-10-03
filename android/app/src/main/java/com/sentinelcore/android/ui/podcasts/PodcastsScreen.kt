package com.sentinelcore.android.ui.podcasts

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
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
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.QueueMusic
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Bookmarks
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentCut
import androidx.compose.material.icons.filled.DirectionsCar
import androidx.compose.material.icons.filled.DragHandle
import androidx.compose.material.icons.filled.FileDownload
import androidx.compose.material.icons.filled.FileUpload
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Podcasts
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Timer
import androidx.compose.material.icons.filled.TravelExplore
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.ScrollableTabRow
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.Episode
import com.sentinelcore.android.audio.PlayContext
import com.sentinelcore.android.audio.PodcastHome
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.audio.ArtImage
import com.sentinelcore.android.ui.audio.AudioMoreMenu
import com.sentinelcore.android.ui.audio.AudioRoutes
import com.sentinelcore.android.ui.audio.ContinueBanner
import com.sentinelcore.android.ui.audio.DirectoryShowCard
import com.sentinelcore.android.ui.audio.EmptyPanel
import com.sentinelcore.android.ui.audio.EpisodeRow
import com.sentinelcore.android.ui.audio.Item
import com.sentinelcore.android.ui.audio.LoadableContent
import com.sentinelcore.android.ui.audio.LocalAudioUi
import com.sentinelcore.android.ui.audio.SectionTitle
import com.sentinelcore.android.ui.audio.Shelf
import com.sentinelcore.android.ui.audio.ShowCard
import com.sentinelcore.android.ui.audio.combinedClickableCompat
import com.sentinelcore.android.ui.audio.rememberListReorderState
import com.sentinelcore.android.ui.audio.rememberLoadable
import com.sentinelcore.android.ui.audio.reorderHandle
import com.sentinelcore.android.ui.audio.reorderableRow
import com.sentinelcore.android.ui.screens.ScreenHeader
import kotlinx.coroutines.delay

private enum class PodcastPane(val title: String) { LISTEN_NOW("Listen Now"), UP_NEXT("Up Next"), LIBRARY("Library"), DISCOVER("Discover") }

// The Podcasts tab (§6, §11): Listen Now, the Up Next queue, the shows followed, and Discover.
@Composable
fun PodcastsScreen(state: SentinelUiState) {
    val ui = LocalAudioUi.current
    var paneIndex by rememberSaveable { mutableIntStateOf(ui.runtime.prefs.lastPodcastPane.coerceIn(0, PodcastPane.entries.lastIndex)) }
    var showFill by remember { mutableStateOf(false) }
    var showAddUrl by remember { mutableStateOf(false) }
    val opml = rememberOpmlActions()
    Column(Modifier.fillMaxSize()) {
        ScreenHeader(
            "Podcasts",
            state,
            actions = {
                IconButton(onClick = { ui.nav.open(AudioRoutes.TRANSCRIPT_SEARCH) }) { Icon(Icons.Default.Search, contentDescription = "Search transcripts") }
                IconButton(onClick = {
                    ui.launch("Couldn't refresh.") { ui.api.refreshAllPodcasts(); ui.message("Checking every show for new episodes"); delay(4_000); ui.libraryVersion++ }
                }) { Icon(Icons.Default.Refresh, contentDescription = "Check for new episodes") }
                AudioMoreMenu { close ->
                    Item(Icons.Default.DirectionsCar, "Fill my drive…") { close(); showFill = true }
                    Item(Icons.Default.Bookmarks, "Bookmarks") { close(); ui.nav.open(AudioRoutes.BOOKMARKS) }
                    Item(Icons.Default.ContentCut, "Clips") { close(); ui.nav.open(AudioRoutes.CLIPS) }
                    Item(Icons.Default.Link, "Add a show by URL…") { close(); showAddUrl = true }
                    Item(Icons.Default.FileUpload, "Import OPML…") { close(); opml.import() }
                    Item(Icons.Default.FileDownload, "Export OPML") { close(); opml.export() }
                }
            },
        )
        ScrollableTabRow(selectedTabIndex = paneIndex, edgePadding = 8.dp) {
            PodcastPane.entries.forEachIndexed { index, entry ->
                Tab(selected = paneIndex == index, onClick = { paneIndex = index; ui.runtime.prefs.lastPodcastPane = index }, text = { Text(entry.title) })
            }
        }
        when (PodcastPane.entries[paneIndex]) {
            PodcastPane.LISTEN_NOW -> ListenNowPane(onUpNext = { paneIndex = 1 }, onDiscover = { paneIndex = 3 }, onFill = { showFill = true })
            PodcastPane.UP_NEXT -> UpNextPane()
            PodcastPane.LIBRARY -> ShowsLibraryPane(onDiscover = { paneIndex = 3 })
            PodcastPane.DISCOVER -> DiscoverPane(onAddUrl = { showAddUrl = true }, onImport = { opml.import() })
        }
    }
    if (showFill) FillMyDriveDialog(onDismiss = { showFill = false })
    if (showAddUrl) AddByUrlDialog(onDismiss = { showAddUrl = false })
}

// ---------------- Listen Now ----------------

@Composable
private fun ListenNowPane(onUpNext: () -> Unit, onDiscover: () -> Unit, onFill: () -> Unit) {
    val ui = LocalAudioUi.current
    val home = rememberLoadable(Unit) { ui.api.podcastHome() }
    LoadableContent(home) { value ->
        if (value.subscriptions.isEmpty() && value.upNext.isEmpty()) {
            EmptyPanel(Icons.Default.Podcasts, "No shows yet", "Find shows to follow in Discover, or import your list from another app (OPML).") {
                Button(onClick = onDiscover) { Text("Discover shows") }
            }
            return@LoadableContent
        }
        ListenNow(value, onUpNext, onFill)
    }
}

@Composable
private fun ListenNow(home: PodcastHome, onUpNext: () -> Unit, onFill: () -> Unit) {
    val ui = LocalAudioUi.current
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
        item { ContinueBanner("podcast") }
        if (home.continueListening.isNotEmpty()) {
            item { SectionTitle("Continue listening") }
            item { Shelf { items(home.continueListening, key = { "c" + it.id }) { EpisodeCard(it) } } }
        }
        if (home.upNext.isNotEmpty()) {
            item {
                SectionTitle("Up Next", subtitle = "${home.upNext.size} episodes · ${AudioFormat.duration(home.upNext.sumOf { it.remainingSeconds })}", actionLabel = "Play all") {
                    ui.engine.playEpisodes(home.upNext, 0, PlayContext("queue", null, "Up Next"))
                }
            }
            items(home.upNext.take(3), key = { "u" + it.id }) { episode -> EpisodeRow(episode, onMore = { ui.episodeMenu = episode }) { ui.nav.openEpisode(episode.id) } }
            if (home.upNext.size > 3) item { TextButton(onClick = onUpNext, modifier = Modifier.padding(horizontal = 8.dp)) { Text("See all ${home.upNext.size}") } }
        }
        item {
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                ToolCard(Icons.Default.DirectionsCar, "Fill my drive", "Episodes that fit your trip", Modifier.weight(1f), onFill)
                ToolCard(
                    Icons.Default.Timer,
                    if (home.timeSavedSeconds >= 60) AudioFormat.duration(home.timeSavedSeconds) else "Time saved",
                    if (home.timeSavedSeconds >= 60) "saved by speed, trims and skips" else "Speed and trims add up here",
                    Modifier.weight(1f),
                ) { ui.nav.open(AudioRoutes.STATS) }
            }
        }
        if (home.newEpisodes.isNotEmpty()) {
            item { SectionTitle("New episodes", subtitle = "From your shows in the last two weeks") }
            items(home.newEpisodes.take(12), key = { "n" + it.id }) { episode -> EpisodeRow(episode, onMore = { ui.episodeMenu = episode }) { ui.nav.openEpisode(episode.id) } }
        }
        if (home.shortOnes.isNotEmpty()) {
            item { SectionTitle("Short on time", subtitle = "Under 20 minutes") }
            item { Shelf { items(home.shortOnes, key = { "s" + it.id }) { EpisodeCard(it) } } }
        }
        if (home.downloaded.isNotEmpty()) {
            item { SectionTitle("Ready on the server", subtitle = "Downloaded and unplayed") }
            item { Shelf { items(home.downloaded, key = { "d" + it.id }) { EpisodeCard(it) } } }
        }
        if (home.bookmarks.isNotEmpty()) {
            item { SectionTitle("Bookmarks", actionLabel = "All") { ui.nav.open(AudioRoutes.BOOKMARKS) } }
            items(home.bookmarks.take(4), key = { "b" + it.id }) { bookmark -> BookmarkRow(bookmark) }
        }
        if (home.subscriptions.isNotEmpty()) {
            item { SectionTitle("Your shows") }
            item { Shelf { items(home.subscriptions, key = { "p" + it.id }) { podcast -> ShowCard(podcast) { ui.nav.openShow(podcast.id) } } } }
        }
    }
}

@Composable
private fun ToolCard(icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, detail: String, modifier: Modifier, onClick: () -> Unit) {
    Card(modifier.clickable(onClick = onClick), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.secondaryContainer)) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSecondaryContainer)
            Text(title, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.onSecondaryContainer, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(detail, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSecondaryContainer.copy(alpha = 0.8f), maxLines = 2)
        }
    }
}

// An episode as a card on a shelf: tap for details, the play badge plays it.
@Composable
fun EpisodeCard(episode: Episode) {
    val ui = LocalAudioUi.current
    Column(
        Modifier.width(156.dp).clip(RoundedCornerShape(10.dp)).combinedClickableCompat(
            onClick = { ui.nav.openEpisode(episode.id) },
            onLongClick = { ui.episodeMenu = episode },
        ),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Box(Modifier.fillMaxWidth().aspectRatio(1f)) {
            ArtImage(episode.artUrl, Modifier.fillMaxSize(), fallbackIcon = Icons.Default.Podcasts, seed = episode.podcastTitle)
            Box(Modifier.fillMaxSize().clip(RoundedCornerShape(8.dp)).background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.6f)))))
            IconButton(
                onClick = { ui.playEpisode(episode) },
                modifier = Modifier.align(Alignment.BottomEnd).padding(end = 8.dp, bottom = 12.dp).size(36.dp).background(Color.White, RoundedCornerShape(18.dp)),
            ) { Icon(Icons.Default.PlayArrow, contentDescription = "Play ${episode.title}", tint = Color.Black) }
            Text(
                if (episode.inProgress) AudioFormat.minutesLeft(episode.remainingSeconds) else AudioFormat.duration(episode.durationSeconds),
                color = Color.White,
                style = MaterialTheme.typography.labelSmall,
                modifier = Modifier.align(Alignment.BottomStart).padding(start = 8.dp, bottom = 14.dp),
            )
            val progress = episode.progress
            if (episode.inProgress && progress != null) {
                val total = progress.durationSeconds?.takeIf { it > 0 } ?: episode.durationSeconds
                LinearProgressIndicator(
                    progress = { if (total > 0) (progress.positionSeconds / total).toFloat() else 0f },
                    modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(3.dp),
                )
            }
        }
        Text(episode.title, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 2, overflow = TextOverflow.Ellipsis)
        Text(
            listOf(episode.podcastTitle, AudioFormat.day(episode.publishedAt)).filter { it.isNotBlank() }.joinToString(" · "),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

// ---------------- Up Next ----------------

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun UpNextPane() {
    val ui = LocalAudioUi.current
    val queue = rememberLoadable(Unit) { ui.api.queue() }
    val rows = remember(queue.value) { mutableStateListOf<Episode>().apply { addAll(queue.value.orEmpty()) } }
    val listState = rememberLazyListState()
    val reorder = rememberListReorderState(
        listState,
        onMove = { from, to ->
            val a = rows.indexOfFirst { it.id == from }
            val b = rows.indexOfFirst { it.id == to }
            if (a >= 0 && b >= 0) rows.add(b, rows.removeAt(a))
        },
        onDrop = {
            val ids = rows.map { it.id }
            if (ids != queue.value?.map { it.id }) ui.launch("Couldn't save the order.") { ui.api.setQueue(ids) }
        },
    )
    LoadableContent(queue) {
        if (rows.isEmpty()) {
            EmptyPanel(Icons.AutoMirrored.Filled.QueueMusic, "Up Next is empty", "Add episodes from their menu, or turn on \"Add new episodes to Up Next\" in a show's settings.")
            return@LoadableContent
        }
        LazyColumn(Modifier.fillMaxSize(), state = listState, contentPadding = PaddingValues(bottom = 24.dp)) {
            item(contentType = "head") {
                Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "${rows.size} episodes · ${AudioFormat.duration(rows.sumOf { it.remainingSeconds })}",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.weight(1f),
                    )
                    Button(onClick = { ui.engine.playEpisodes(rows.toList(), 0, PlayContext("queue", null, "Up Next")) }) {
                        Icon(Icons.Default.PlayArrow, contentDescription = null)
                        Text("Play all")
                    }
                }
            }
            itemsIndexed(rows, key = { _, episode -> episode.id }, contentType = { _, _ -> "episode" }) { _, episode ->
                val dismiss = rememberSwipeToDismissBoxState(confirmValueChange = { value ->
                    if (value == SwipeToDismissBoxValue.EndToStart) {
                        rows.remove(episode)
                        ui.launch("Couldn't remove it.") { ui.api.dequeue(episode.id) }
                        true
                    } else false
                })
                SwipeToDismissBox(
                    state = dismiss,
                    enableDismissFromStartToEnd = false,
                    modifier = Modifier.reorderableRow(reorder, episode.id),
                    backgroundContent = {
                        Box(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.error).padding(horizontal = 24.dp), contentAlignment = Alignment.CenterEnd) {
                            Text("Remove", color = Color.White)
                        }
                    },
                ) {
                    Row(Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surface), verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.weight(1f)) {
                            EpisodeRow(episode, onMore = { ui.episodeMenu = episode }) { ui.nav.openEpisode(episode.id) }
                        }
                        Icon(Icons.Default.DragHandle, contentDescription = "Drag to reorder", modifier = Modifier.reorderHandle(reorder, episode.id).padding(12.dp))
                    }
                }
            }
        }
    }
}

// ---------------- Library ----------------

@Composable
private fun ShowsLibraryPane(onDiscover: () -> Unit) {
    val ui = LocalAudioUi.current
    var all by rememberSaveable { mutableStateOf(false) }
    var sort by rememberSaveable { mutableStateOf("updated") }
    val shows = rememberLoadable(all) { ui.api.podcasts(all) }
    Column(Modifier.fillMaxSize()) {
        Row(
            Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 6.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            FilterChip(selected = !all, onClick = { all = false }, label = { Text("Following") })
            FilterChip(selected = all, onClick = { all = true }, label = { Text("Everyone's shows") })
            Spacer(Modifier.width(8.dp))
            listOf("updated" to "Recent", "title" to "A-Z", "unplayed" to "Unplayed").forEach { (key, label) ->
                FilterChip(selected = sort == key, onClick = { sort = key }, label = { Text(label) })
            }
        }
        LoadableContent(shows) { list ->
            if (list.isEmpty()) {
                EmptyPanel(Icons.Default.Podcasts, "No shows", "Follow some in Discover.") { Button(onClick = onDiscover) { Text("Discover") } }
                return@LoadableContent
            }
            val sorted = when (sort) {
                "title" -> list.sortedBy { it.title.lowercase().removePrefix("the ") }
                "unplayed" -> list.sortedByDescending { it.unplayedCount }
                else -> list
            }
            LazyVerticalGrid(GridCells.Adaptive(118.dp), Modifier.fillMaxSize(), contentPadding = PaddingValues(16.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                items(sorted, key = { it.id }) { podcast -> ShowCard(podcast, width = 400.dp) { ui.nav.openShow(podcast.id) } }
            }
        }
    }
}

// ---------------- Discover ----------------

@Composable
private fun DiscoverPane(onAddUrl: () -> Unit, onImport: () -> Unit) {
    val ui = LocalAudioUi.current
    var query by rememberSaveable { mutableStateOf("") }
    var genre by rememberSaveable { mutableStateOf<Long?>(null) }
    val results = rememberLoadable(query.trim()) {
        val q = query.trim()
        if (q.length < 2) null else {
            delay(350)
            ui.api.directorySearch(q)
        }
    }
    val genres = rememberLoadable(Unit) { ui.api.directoryGenres() }
    val top = rememberLoadable(genre) { ui.api.directoryTop(genre) }
    val recommended = rememberLoadable(Unit) { ui.api.recommended() }
    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            placeholder = { Text("Search all podcasts") },
            leadingIcon = { Icon(Icons.Default.TravelExplore, contentDescription = null) },
            trailingIcon = { if (query.isNotEmpty()) IconButton(onClick = { query = "" }) { Icon(Icons.Default.Close, contentDescription = "Clear") } },
            singleLine = true,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        )
        if (query.trim().length >= 2) {
            val found = results.value
            when {
                results.error != null -> Text(results.error.orEmpty(), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp))
                found == null -> LinearProgressIndicator(Modifier.fillMaxWidth().padding(horizontal = 16.dp))
                found.isEmpty() -> EmptyPanel(Icons.Default.Search, "No shows found", "Try other words, or add the show by its feed URL.") {
                    OutlinedButton(onClick = onAddUrl) { Text("Add by URL") }
                }
                else -> LazyVerticalGrid(GridCells.Adaptive(132.dp), Modifier.fillMaxSize(), contentPadding = PaddingValues(16.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                    items(found, key = { (it.itunesId ?: 0L).toString() + it.title }) { show ->
                        DirectoryShowCard(show, width = 400.dp) { openDirectoryShow(ui, show) }
                    }
                }
            }
            return@Column
        }
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
            item {
                Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    AssistChip(onClick = onAddUrl, label = { Text("Add by URL") }, leadingIcon = { Icon(Icons.Default.Add, null, Modifier.size(18.dp)) })
                    AssistChip(onClick = onImport, label = { Text("Import OPML") }, leadingIcon = { Icon(Icons.Default.FileUpload, null, Modifier.size(18.dp)) })
                }
            }
            val picks = recommended.value.orEmpty()
            if (picks.isNotEmpty()) {
                item { SectionTitle("Recommended for your household", subtitle = "Top shows in what you listen to most") }
                item { Shelf { items(picks, key = { "rec" + (it.itunesId ?: 0) + it.title }) { show -> DirectoryShowCard(show) { openDirectoryShow(ui, show) } } } }
            }
            item { SectionTitle("Top shows") }
            item {
                Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    FilterChip(selected = genre == null, onClick = { genre = null }, label = { Text("All") })
                    genres.value.orEmpty().forEach { g -> FilterChip(selected = genre == g.id, onClick = { genre = g.id }, label = { Text(g.name) }) }
                }
            }
            val chart = top.value
            when {
                chart == null && top.error == null -> item { LinearProgressIndicator(Modifier.fillMaxWidth().padding(16.dp)) }
                chart == null -> item { Text(top.error.orEmpty(), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
                else -> itemsIndexed(chart, key = { index, show -> "top$index" + show.title }) { index, show ->
                    Row(Modifier.fillMaxWidth().clickable { openDirectoryShow(ui, show) }.padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text("${index + 1}", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.primary, modifier = Modifier.width(34.dp))
                        ArtImage(show.artUrl, Modifier.size(56.dp), sizePx = 168, fallbackIcon = Icons.Default.Podcasts, seed = show.title)
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f)) {
                            Text(show.title, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(listOfNotNull(show.author.ifBlank { null }, show.genre).joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                        if (show.subscribed) Text("Following", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.primary)
                    }
                }
            }
        }
    }
}

// A show the household already has opens its page; otherwise a preview, to look before following.
fun openDirectoryShow(ui: com.sentinelcore.android.ui.audio.AudioUi, show: com.sentinelcore.android.audio.DirectoryShow) {
    val podcastId = show.podcastId
    if (podcastId != null) ui.nav.openShow(podcastId) else ui.nav.openPreview(show.feedUrl, show.itunesId)
}

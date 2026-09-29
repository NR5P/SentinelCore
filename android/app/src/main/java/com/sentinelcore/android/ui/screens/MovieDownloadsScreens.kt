package com.sentinelcore.android.ui.screens

import androidx.activity.compose.BackHandler
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.material.icons.filled.Check
import androidx.compose.ui.draw.alpha
import com.sentinelcore.android.data.MediaDownloadServices
import com.sentinelcore.android.data.MediaVpnTestResult
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
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
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
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
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import com.sentinelcore.android.data.MediaDownload
import com.sentinelcore.android.data.MediaDownloadFormat
import com.sentinelcore.android.data.MediaDownloadList
import com.sentinelcore.android.data.MediaDownloadShow
import com.sentinelcore.android.data.MediaDownloadSite
import com.sentinelcore.android.data.MediaDownloadStatus
import com.sentinelcore.android.data.MediaDownloadsStartingException
import com.sentinelcore.android.data.MediaDownloadApi
import com.sentinelcore.android.data.MediaLookupResult
import com.sentinelcore.android.data.MediaRelease
import com.sentinelcore.android.data.MediaReleaseSearch
import com.sentinelcore.android.data.MediaReleaseSearchInput
import com.sentinelcore.android.data.MediaSiteDefinition
import com.sentinelcore.android.data.MediaSiteField
import com.sentinelcore.android.data.ServerTimestamp
import com.sentinelcore.android.data.VpnTone
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.components.relativeTime
import com.sentinelcore.android.ui.files.ConfirmDialog
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

// Movie downloads - "+ Add" on the Movies tab (core/docs/MOVIE_DOWNLOADS.md). Three screens, in
// the web dashboard's order (movies.js, "downloads (+ add)"): Add finds a title on TMDB and a
// release on the household's torrent sites; Downloads follows the queue; Sites (admin) manages
// which sites are searched. The server does the downloading, through a VPN.

const val MOVIE_ADD_ROUTE = "movies/add"
const val MOVIE_DOWNLOADS_ROUTE = "movies/downloads"
const val MOVIE_DOWNLOAD_SITES_ROUTE = "movies/downloads/sites"

private const val DOWNLOAD_POLL_MS = 2_500L
private const val SEARCH_POLL_MS = 1_000L
private const val SITES_RETRY_MS = 3_000L

// ---------------- Shared bits ----------------

// A small rounded label: quality chips, "in library", privacy.
@Composable
internal fun DownloadTag(text: String, color: Color = SentinelTextDim, filled: Boolean = false) {
    Box(
        Modifier
            .clip(RoundedCornerShape(6.dp))
            .background(if (filled) color else color.copy(alpha = 0.14f))
            .padding(horizontal = 6.dp, vertical = 2.dp),
    ) {
        Text(
            text,
            style = MaterialTheme.typography.labelSmall,
            color = if (filled) Color.Black else color,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
        )
    }
}

@Composable
private fun BusyLine(message: String, modifier: Modifier = Modifier) {
    Row(modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
        Spacer(Modifier.width(10.dp))
        Text(message, color = SentinelTextDim)
    }
}

private fun ageLabel(timestamp: String?): String? =
    timestamp?.let { runCatching { ServerTimestamp.parse(it).relativeTime() }.getOrNull() }

private fun airDateLabel(date: String?): String? = date?.let {
    runCatching { LocalDate.parse(it.take(10)).format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM)) }.getOrDefault(it)
}

// ---------------- Add: find -> (seasons) -> releases ----------------

internal enum class AddStage { FIND, SHOW, RELEASES }

// Kept on the Add screen's back-stack entry, so a rotation keeps the search and its poll.
internal class MovieAddModel(private val app: SentinelViewModel) : ViewModel() {
    var status by mutableStateOf<MediaDownloadStatus?>(null)
    var stage by mutableStateOf(AddStage.FIND)
        private set
    var kind by mutableStateOf("movie")
        private set
    var query by mutableStateOf("")
    // What was last looked up - the "Search sites for" button uses this, not the live text.
    var submitted by mutableStateOf("")
        private set
    var lookingUp by mutableStateOf(false)
        private set
    var results by mutableStateOf<List<MediaLookupResult>?>(null)
        private set
    var lookupError by mutableStateOf<String?>(null)
        private set
    var tmdb by mutableStateOf(true)
        private set
    var seasonText by mutableStateOf("")
    var episodeText by mutableStateOf("")

    var showLoading by mutableStateOf(false)
        private set
    var show by mutableStateOf<MediaDownloadShow?>(null)
        private set
    var showTitle by mutableStateOf<String?>(null)
        private set
    var showError by mutableStateOf<String?>(null)
        private set
    var season by mutableStateOf<Int?>(null)

    var searchInput by mutableStateOf<MediaReleaseSearchInput?>(null)
        private set
    var search by mutableStateOf<MediaReleaseSearch?>(null)
        private set
    var searchError by mutableStateOf<String?>(null)
        private set
    private var backToShow = false
    var othersOpen by mutableStateOf(false)
    var grabbing by mutableStateOf<String?>(null)
        private set
    var grabError by mutableStateOf<String?>(null)
        private set

    private var lookupJob: Job? = null
    private var showJob: Job? = null
    private var searchJob: Job? = null

    init {
        viewModelScope.launch { status = runCatching { app.mediaDownloadStatus() }.getOrNull() }
    }

    val tmdbMissing: Boolean get() = status?.tmdbConfigured == false || !tmdb

    val backLabel: String get() = if (stage == AddStage.RELEASES && backToShow) "Episodes" else "Results"

    fun chooseKind(value: String) {
        if (kind == value) return
        kind = value
        results = null
        lookupError = null
        if (submitted.isNotBlank()) lookup()
    }

    fun lookup() {
        val q = query.trim()
        if (q.isEmpty()) return
        submitted = q
        lookupJob?.cancel()
        lookupJob = viewModelScope.launch {
            lookingUp = true
            lookupError = null
            runCatching { app.mediaDownloadLookup(q, kind) }
                .onSuccess {
                    tmdb = it.tmdbConfigured
                    results = it.results
                }
                .onFailure { lookupError = it.message ?: "Could not search." }
            lookingUp = false
        }
    }

    fun pick(result: MediaLookupResult) {
        if (!result.isShow) {
            startSearch(
                MediaReleaseSearchInput("movie", result.title, result.year, result.tmdbId, result.posterPath),
                fromShow = false,
            )
            return
        }
        stage = AddStage.SHOW
        show = null
        showTitle = result.title
        showError = null
        showJob?.cancel()
        showJob = viewModelScope.launch {
            showLoading = true
            runCatching { app.mediaDownloadShow(result.tmdbId) }
                .onSuccess {
                    show = it
                    season = MediaDownloadFormat.startingSeason(it)
                }
                .onFailure { showError = it.message ?: "Could not load the seasons." }
            showLoading = false
        }
    }

    // Episode null = the whole season.
    fun getFromShow(episode: Int?) {
        val current = show ?: return
        val number = season ?: return
        startSearch(
            MediaReleaseSearchInput("show", current.title, current.year, current.tmdbId, current.posterPath, number, episode),
            fromShow = true,
        )
    }

    // No TMDB: search the sites for exactly what was typed.
    fun searchDirect() {
        val title = submitted.ifBlank { query.trim() }
        if (title.isBlank()) return
        val seasonNumber = if (kind == "show") MediaDownloadFormat.wholeNumber(seasonText) else null
        val episodeNumber = if (seasonNumber != null) MediaDownloadFormat.wholeNumber(episodeText) else null
        startSearch(MediaReleaseSearchInput(kind, title, season = seasonNumber, episode = episodeNumber), fromShow = false)
    }

    fun searchAgain() {
        searchInput?.let { startSearch(it, backToShow) }
    }

    private fun startSearch(input: MediaReleaseSearchInput, fromShow: Boolean) {
        searchJob?.cancel()
        stage = AddStage.RELEASES
        backToShow = fromShow
        othersOpen = false
        grabError = null
        searchInput = input
        searchError = null
        search = MediaReleaseSearch(id = "", phase = "connecting", message = "Connecting to the VPN...")
        searchJob = viewModelScope.launch {
            var current = runCatching { app.startMediaReleaseSearch(input) }
                .getOrElse {
                    searchError = it.message ?: "The search failed."
                    search = null
                    return@launch
                }
            search = current
            // Every second until the search is done or failed.
            while (!current.isFinished) {
                delay(SEARCH_POLL_MS)
                current = runCatching { app.mediaReleaseSearch(current.id) }
                    .getOrElse {
                        searchError = it.message ?: "The search failed."
                        search = null
                        return@launch
                    }
                search = current
            }
        }
    }

    // Back one stage; false when already on the first.
    fun back(): Boolean {
        return when (stage) {
            AddStage.FIND -> false
            AddStage.SHOW -> {
                showJob?.cancel()
                stage = AddStage.FIND
                show = null
                true
            }
            AddStage.RELEASES -> {
                searchJob?.cancel()
                search = null
                searchError = null
                stage = if (backToShow && show != null) AddStage.SHOW else AddStage.FIND
                true
            }
        }
    }

    fun grab(release: MediaRelease, onStarted: () -> Unit) {
        val searchId = search?.id?.takeIf { it.isNotBlank() } ?: return
        if (grabbing != null) return
        viewModelScope.launch {
            grabbing = release.id
            grabError = null
            runCatching { app.startMediaDownload(searchId, release.id) }
                .onSuccess { onStarted() }
                .onFailure { grabError = it.message ?: "Could not start the download." }
            grabbing = null
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MovieAddScreen(
    model: SentinelViewModel,
    onBack: () -> Unit,
    // The download was accepted: close this screen and show the queue.
    onDownloadStarted: () -> Unit,
) {
    val add: MovieAddModel = viewModel { MovieAddModel(model) }
    BackHandler(enabled = add.stage != AddStage.FIND) { add.back() }

    val title = when (add.stage) {
        AddStage.FIND -> "Add a movie or show"
        AddStage.SHOW -> add.show?.title ?: add.showTitle ?: "TV show"
        AddStage.RELEASES -> "Pick a download"
    }
    Column(Modifier.fillMaxSize()) {
        DetailHeader(title, onBack = { if (!add.back()) onBack() })
        MediaDownloadFormat.vpnOffNote(add.status)?.let { note ->
            Row(
                Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 4.dp)
                    .clip(RoundedCornerShape(8.dp))
                    .background(SentinelAmber.copy(alpha = 0.14f))
                    .padding(horizontal = 12.dp, vertical = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Box(Modifier.size(8.dp).background(SentinelAmber, CircleShape))
                Text(note, style = MaterialTheme.typography.bodySmall, color = SentinelAmber, fontWeight = FontWeight.SemiBold)
            }
        }
        when (add.stage) {
            AddStage.FIND -> AddFind(add, model)
            AddStage.SHOW -> AddShow(add)
            AddStage.RELEASES -> AddReleases(add, onDownloadStarted)
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun AddFind(add: MovieAddModel, model: SentinelViewModel) {
    val status = add.status
    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                listOf("movie" to "Movie", "show" to "TV show").forEachIndexed { index, (value, label) ->
                    SegmentedButton(
                        selected = add.kind == value,
                        onClick = { add.chooseKind(value) },
                        shape = SegmentedButtonDefaults.itemShape(index, 2),
                    ) { Text(label) }
                }
            }
        }
        val noLibrary = status != null &&
            ((add.kind == "movie" && !status.movieLibrary) || (add.kind == "show" && !status.showLibrary))
        if (noLibrary) {
            item {
                Text(
                    "There is no ${if (add.kind == "movie") "movie" else "TV"} library on the server's own disks to save into yet. " +
                        "An admin can add one in Settings → Movies & TV libraries" + (if (add.kind == "show") " (kind: TV Shows)" else "") + " first.",
                    color = SentinelAmber,
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        }
        item {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(
                    value = add.query,
                    onValueChange = { add.query = it },
                    modifier = Modifier.weight(1f),
                    singleLine = true,
                    placeholder = { Text(if (add.kind == "movie") "Movie title" else "Show title") },
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                    keyboardActions = KeyboardActions(onSearch = { add.lookup() }),
                )
                Button(onClick = { add.lookup() }, enabled = add.query.isNotBlank()) {
                    Icon(Icons.Default.Search, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(4.dp))
                    Text("Find")
                }
            }
        }
        if (add.tmdbMissing && add.submitted.isNotBlank()) {
            item { DirectSearch(add) }
        }
        when {
            add.lookingUp -> item { BusyLine("Searching…") }
            add.lookupError != null -> item { Text(add.lookupError.orEmpty(), color = SentinelRed) }
            add.results != null && add.results.orEmpty().isEmpty() && !add.tmdbMissing -> item {
                Text("Nothing on TMDB called that. Try fewer words.", color = SentinelTextDim)
            }
        }
        if (!add.lookingUp) {
            itemsIndexed(add.results.orEmpty(), key = { index, r -> "${r.kind}-${r.tmdbId}-$index" }) { _, result ->
                LookupRow(model, result, onClick = { add.pick(result) })
            }
        }
    }
}

@Composable
private fun DirectSearch(add: MovieAddModel) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            if (add.status?.tmdbConfigured == false) {
                Text(
                    "No TMDB key, so there are no posters or episode lists - the sites are searched for exactly what you type.",
                    style = MaterialTheme.typography.bodySmall,
                    color = SentinelTextDim,
                )
            }
            if (add.kind == "show") {
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(
                        value = add.seasonText,
                        onValueChange = { add.seasonText = it.filter(Char::isDigit).take(2) },
                        modifier = Modifier.weight(1f),
                        singleLine = true,
                        label = { Text("Season") },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    )
                    OutlinedTextField(
                        value = add.episodeText,
                        onValueChange = { add.episodeText = it.filter(Char::isDigit).take(3) },
                        modifier = Modifier.weight(1.4f),
                        singleLine = true,
                        label = { Text("Episode") },
                        placeholder = { Text("Blank = whole season") },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    )
                }
            }
            Button(onClick = { add.searchDirect() }, modifier = Modifier.fillMaxWidth()) {
                Icon(Icons.Default.Search, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text("Search sites for “${add.submitted}”", maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

@Composable
private fun LookupRow(model: SentinelViewModel, result: MediaLookupResult, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .clickable(onClick = onClick)
            .padding(vertical = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        PosterImage(
            model,
            result.posterUrl,
            result.title,
            Modifier.width(64.dp).height(96.dp).clip(RoundedCornerShape(6.dp)),
            maxWidthPx = 200,
        )
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(
                result.title + (result.year?.let { " ($it)" } ?: ""),
                style = MaterialTheme.typography.titleSmall,
                fontWeight = FontWeight.SemiBold,
            )
            if (result.inLibrary) DownloadTag("In library", SentinelGreen)
            Text(
                result.overview ?: "No description.",
                style = MaterialTheme.typography.bodySmall,
                color = SentinelTextDim,
                maxLines = 3,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

@Composable
private fun AddShow(add: MovieAddModel) {
    val show = add.show
    when {
        add.showLoading || (show == null && add.showError == null) -> BusyLine("Loading seasons…")
        add.showError != null -> Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(add.showError.orEmpty(), color = SentinelRed)
            OutlinedButton(onClick = { add.back() }) { Text("Back") }
        }
        show != null -> {
            val season = show.seasons.firstOrNull { it.number == add.season }
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                item {
                    TextButton(onClick = { add.back() }, modifier = Modifier.padding(horizontal = 4.dp)) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text("Results")
                    }
                }
                item {
                    Row(
                        Modifier
                            .fillMaxWidth()
                            .horizontalScroll(rememberScrollState())
                            .padding(horizontal = 16.dp),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        show.seasons.forEach { entry ->
                            FilterChip(
                                selected = entry.number == add.season,
                                onClick = { add.season = entry.number },
                                label = { Text(MediaDownloadFormat.seasonTabLabel(entry)) },
                            )
                        }
                    }
                }
                if (season == null) {
                    item { Text("TMDB lists no seasons for this show.", color = SentinelTextDim, modifier = Modifier.padding(16.dp)) }
                } else {
                    item {
                        Column(
                            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp),
                            verticalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            val facts = listOfNotNull(
                                season.name,
                                MediaDownloadFormat.plural(season.episodeCount, "episode"),
                                season.ownedCount.takeIf { it > 0 }?.let { "$it in library" },
                            ).joinToString(" · ")
                            Text(facts, style = MaterialTheme.typography.titleSmall)
                            Button(onClick = { add.getFromShow(null) }, modifier = Modifier.fillMaxWidth()) {
                                Text("Get the whole season")
                            }
                        }
                    }
                    itemsIndexed(season.episodes, key = { _, e -> "e${season.number}-${e.number}" }) { _, episode ->
                        Row(
                            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            Text(
                                "${episode.number}",
                                style = MaterialTheme.typography.titleSmall,
                                color = if (episode.inLibrary) SentinelGreen else SentinelTextDim,
                                modifier = Modifier.width(28.dp),
                                textAlign = TextAlign.End,
                            )
                            Column(Modifier.weight(1f)) {
                                Text(
                                    episode.name ?: "Episode ${episode.number}",
                                    style = MaterialTheme.typography.bodyMedium,
                                    maxLines = 2,
                                    overflow = TextOverflow.Ellipsis,
                                    color = if (episode.inLibrary) SentinelTextDim else Color.Unspecified,
                                )
                                val sub = listOfNotNull(
                                    airDateLabel(episode.airDate),
                                    "in library".takeIf { episode.inLibrary },
                                    "not out yet".takeIf { !episode.aired },
                                ).joinToString(" · ")
                                if (sub.isNotEmpty()) {
                                    Text(sub, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                                }
                            }
                            OutlinedButton(onClick = { add.getFromShow(episode.number) }, enabled = episode.aired) {
                                Text("Get")
                            }
                        }
                    }
                }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun AddReleases(add: MovieAddModel, onDownloadStarted: () -> Unit) {
    val search = add.search
    val input = add.searchInput
    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = PaddingValues(start = 16.dp, end = 16.dp, bottom = 24.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        item {
            TextButton(onClick = { add.back() }) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text(add.backLabel)
            }
        }
        input?.let { item { Text(it.label, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold) } }

        val failedMessage = add.searchError ?: search?.takeIf { it.phase == "failed" }?.let { it.message ?: it.error ?: "The search failed." }
        when {
            failedMessage != null -> {
                item { Text(failedMessage, color = SentinelRed) }
                item { OutlinedButton(onClick = { add.searchAgain() }) { Text("Try again") } }
            }
            search == null || !search.isFinished -> item {
                Column(
                    Modifier.fillMaxWidth().padding(vertical = 24.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    CircularProgressIndicator(color = SentinelGreen)
                    Text(search?.message ?: "Searching…", textAlign = TextAlign.Center)
                    Text(
                        if (search == null || search.phase == "connecting") {
                            "The first search after a while starts the VPN; it takes a few seconds."
                        } else {
                            "Asking each of your sites."
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = SentinelTextDim,
                        textAlign = TextAlign.Center,
                    )
                }
            }
            else -> {
                add.grabError?.let { message -> item { Text(message, color = SentinelRed) } }
                if (search.results.isEmpty()) {
                    item {
                        Text(
                            if (search.others.isNotEmpty()) "None of the releases found are clearly this title with people sharing it."
                            else search.message ?: "Nothing found on your sites.",
                            color = SentinelTextDim,
                        )
                    }
                }
                itemsIndexed(search.results, key = { i, r -> "r-${r.id}-$i" }) { index, release ->
                    ReleaseCard(release, best = index == 0, grabbing = add.grabbing, onGrab = { add.grab(release, onDownloadStarted) })
                }
                if (search.others.isNotEmpty()) {
                    item {
                        TextButton(onClick = { add.othersOpen = !add.othersOpen }) {
                            Icon(if (add.othersOpen) Icons.Default.ExpandLess else Icons.Default.ExpandMore, contentDescription = null)
                            Spacer(Modifier.width(6.dp))
                            Text((if (add.othersOpen) "Hide " else "Show ") + MediaDownloadFormat.plural(search.others.size, "other result"))
                        }
                    }
                    if (add.othersOpen) {
                        itemsIndexed(search.others, key = { i, r -> "o-${r.id}-$i" }) { _, release ->
                            ReleaseCard(release, best = false, grabbing = add.grabbing, onGrab = { add.grab(release, onDownloadStarted) })
                        }
                    }
                }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun ReleaseCard(release: MediaRelease, best: Boolean, grabbing: String?, onGrab: () -> Unit) {
    Card(
        Modifier.fillMaxWidth(),
        border = if (best) BorderStroke(1.5.dp, SentinelGreen) else null,
        colors = if (best) CardDefaults.cardColors(containerColor = SentinelGreen.copy(alpha = 0.08f)) else CardDefaults.cardColors(),
    ) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                if (best) DownloadTag("best match", SentinelGreen, filled = true)
                release.chips.forEach { DownloadTag(it, SentinelCyan) }
            }
            Text(
                release.title,
                style = MaterialTheme.typography.bodySmall,
                fontFamily = FontFamily.Monospace,
                maxLines = 3,
                overflow = TextOverflow.Ellipsis,
            )
            val facts = listOfNotNull(
                MediaDownloadFormat.sizeLabel(release.sizeBytes),
                release.seeders?.let { "$it sharing" },
                release.site,
                ageLabel(release.publishedAt),
            ).joinToString(" · ")
            if (facts.isNotEmpty()) Text(facts, style = MaterialTheme.typography.labelMedium, color = SentinelTextDim)
            release.warning?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = SentinelAmber) }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End, verticalAlignment = Alignment.CenterVertically) {
                if (grabbing == release.id) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                    Spacer(Modifier.width(10.dp))
                }
                if (best) {
                    Button(onClick = onGrab, enabled = grabbing == null) { Text("Download") }
                } else {
                    OutlinedButton(onClick = onGrab, enabled = grabbing == null) { Text("Download") }
                }
            }
        }
    }
}

// ---------------- Downloads ----------------

@Composable
fun MovieDownloadsScreen(
    model: SentinelViewModel,
    onBack: () -> Unit,
    onAdd: () -> Unit,
    onSites: () -> Unit,
    onOpenItem: (String) -> Unit,
) {
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf<MediaDownloadStatus?>(null) }
    var list by remember { mutableStateOf<MediaDownloadList?>(null) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var actionError by remember { mutableStateOf<String?>(null) }
    var busyIds by remember { mutableStateOf(setOf<String>()) }
    var confirmCancel by remember { mutableStateOf<MediaDownload?>(null) }
    // Whatever was fetched last - status, the list, or a VPN test - decides the VPN line.
    var vpnStatus by remember { mutableStateOf<MediaDownloadServices?>(null) }
    var testing by remember { mutableStateOf(false) }
    var testResult by remember { mutableStateOf<MediaVpnTestResult?>(null) }

    suspend fun refresh() {
        runCatching { model.mediaDownloads() }
            .onSuccess { fetched ->
                list = fetched
                fetched.status?.let { vpnStatus = it }
                loadError = null
            }
            .onFailure { loadError = it.message ?: "Could not load the downloads." }
    }

    LaunchedEffect(Unit) {
        status = runCatching { model.mediaDownloadStatus() }.getOrNull()
        status?.status?.let { vpnStatus = it }
        refresh()
    }

    fun testVpn() {
        if (testing) return
        scope.launch {
            testing = true
            testResult = null
            runCatching { model.testMediaDownloadVpn() }
                .onSuccess { result ->
                    testResult = result
                    result.status?.let { vpnStatus = it }
                }
                .onFailure { testResult = MediaVpnTestResult(ok = false, message = it.message ?: "Could not test the VPN.") }
            testing = false
        }
    }
    // Follow the queue while anything is moving; the loop ends once nothing is.
    val hasActive = list?.active?.isNotEmpty() == true
    LaunchedEffect(hasActive) {
        while (hasActive) {
            delay(DOWNLOAD_POLL_MS)
            refresh()
        }
    }

    fun act(id: String, block: suspend () -> Unit) {
        scope.launch {
            busyIds = busyIds + id
            actionError = null
            runCatching { block() }.onFailure { actionError = it.message ?: "That didn't work." }
            refresh()
            busyIds = busyIds - id
        }
    }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Downloads", onBack) {
            IconButton(onClick = onAdd) { Icon(Icons.Default.Add, contentDescription = "Add a movie or show") }
            IconButton(onClick = { scope.launch { refresh() } }) { Icon(Icons.Default.Refresh, contentDescription = "Refresh") }
        }
        val data = list
        LazyColumn(
            Modifier.fillMaxSize(),
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item {
                VpnLine(
                    vpnStatus,
                    canManageSites = status?.canManageSites == true,
                    testing = testing,
                    testResult = testResult,
                    onTest = ::testVpn,
                    onSites = onSites,
                )
            }
            if (data == null) {
                if (loadError != null) {
                    item { ErrorPanel(loadError.orEmpty(), onRetry = { scope.launch { refresh() } }, modifier = Modifier.fillMaxWidth()) }
                } else {
                    item { BusyLine("Loading…") }
                }
                return@LazyColumn
            }
            loadError?.let { message -> item { Text(message, color = SentinelRed) } }
            actionError?.let { message -> item { Text(message, color = SentinelRed) } }
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Downloading", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
                    Button(onClick = onAdd) {
                        Icon(Icons.Default.Add, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(4.dp))
                        Text("Add")
                    }
                }
            }
            if (data.active.isEmpty()) {
                item {
                    Text(
                        "Nothing downloading. Press Add to find a movie or TV episodes.",
                        color = SentinelTextDim,
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
            }
            itemsIndexed(data.active, key = { _, d -> "a-" + d.id }) { _, download ->
                DownloadRow(
                    model,
                    download,
                    busy = download.id in busyIds,
                    onOpen = {},
                    onRetry = {},
                    onCancel = { confirmCancel = download },
                    onRemove = {},
                )
            }
            if (data.recent.isNotEmpty()) {
                item {
                    Text(
                        "Recent",
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.SemiBold,
                        modifier = Modifier.padding(top = 8.dp),
                    )
                }
                itemsIndexed(data.recent, key = { _, d -> "r-" + d.id }) { _, download ->
                    DownloadRow(
                        model,
                        download,
                        busy = download.id in busyIds,
                        onOpen = { download.itemId?.let(onOpenItem) },
                        onRetry = { act(download.id) { model.retryMediaDownload(download.id) } },
                        onCancel = {},
                        onRemove = { act(download.id) { model.deleteMediaDownload(download.id) } },
                    )
                }
            }
        }
    }

    confirmCancel?.let { download ->
        ConfirmDialog(
            title = "Cancel this download?",
            text = "What has downloaded so far of ${download.title}${download.subtitle?.let { " $it" }.orEmpty()} is deleted.",
            confirmLabel = "Cancel download",
            onConfirm = {
                confirmCancel = null
                act(download.id) { model.deleteMediaDownload(download.id) }
            },
            onDismiss = { confirmCancel = null },
        )
    }
}

@Composable
internal fun vpnToneColor(tone: VpnTone): Color = when (tone) {
    VpnTone.ON -> SentinelGreen
    VpnTone.BUSY -> SentinelCyan
    VpnTone.IDLE -> SentinelTextDim
    VpnTone.OFF -> SentinelAmber
    VpnTone.BAD -> SentinelRed
}

// The light's dot; it pulses while the VPN is coming up or being checked.
@Composable
private fun VpnDot(tone: VpnTone, size: androidx.compose.ui.unit.Dp = 10.dp) {
    val color = vpnToneColor(tone)
    val alpha = if (tone == VpnTone.BUSY) {
        val pulse = rememberInfiniteTransition(label = "vpn-pulse")
        pulse.animateFloat(
            initialValue = 1f,
            targetValue = 0.3f,
            animationSpec = infiniteRepeatable(tween(700), RepeatMode.Reverse),
            label = "vpn-pulse-alpha",
        ).value
    } else 1f
    Box(Modifier.size(size).alpha(alpha).background(color, CircleShape))
}

// The VPN light in the Movies top bar: dot + a few words; tapping it opens Downloads.
@Composable
internal fun VpnChip(status: MediaDownloadServices?, onClick: () -> Unit) {
    val look = MediaDownloadFormat.vpnLook(status) ?: return
    val color = vpnToneColor(look.tone)
    Row(
        Modifier
            .padding(horizontal = 2.dp)
            .clip(RoundedCornerShape(14.dp))
            .background(color.copy(alpha = 0.14f))
            .clickable(onClickLabel = "Open Downloads", onClick = onClick)
            .padding(horizontal = 8.dp, vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        VpnDot(look.tone, 8.dp)
        Text(
            look.short,
            style = MaterialTheme.typography.labelMedium,
            fontWeight = FontWeight.SemiBold,
            color = if (look.tone == VpnTone.IDLE) SentinelTextDim else color,
            maxLines = 1,
        )
    }
}

@Composable
private fun VpnLine(
    status: MediaDownloadServices?,
    canManageSites: Boolean,
    testing: Boolean,
    testResult: MediaVpnTestResult?,
    onTest: () -> Unit,
    onSites: () -> Unit,
) {
    val look = MediaDownloadFormat.vpnLook(status)
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            if (look != null) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    VpnDot(look.tone)
                    Text("VPN", style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.Bold)
                    Text(
                        look.text,
                        style = MaterialTheme.typography.bodySmall,
                        color = when (look.tone) {
                            VpnTone.OFF, VpnTone.BAD -> vpnToneColor(look.tone)
                            else -> Color.Unspecified
                        },
                        modifier = Modifier.weight(1f),
                    )
                }
            }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                OutlinedButton(onClick = onTest, enabled = !testing) {
                    if (testing) {
                        CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                    } else {
                        Icon(Icons.Default.Check, contentDescription = null, modifier = Modifier.size(16.dp))
                    }
                    Spacer(Modifier.width(6.dp))
                    Text(if (testing) "Testing…" else "Test")
                }
                Spacer(Modifier.weight(1f))
                if (canManageSites) {
                    TextButton(onClick = onSites) {
                        Icon(Icons.Default.Settings, contentDescription = null, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(4.dp))
                        Text("Sites")
                    }
                }
            }
            if (testing) {
                Text(
                    "Connecting and checking the address the sites see - this can take up to a minute.",
                    style = MaterialTheme.typography.bodySmall,
                    color = SentinelTextDim,
                )
            } else if (testResult != null) {
                Text(
                    testResult.message,
                    style = MaterialTheme.typography.bodySmall,
                    fontWeight = FontWeight.SemiBold,
                    color = if (testResult.ok) SentinelGreen else SentinelRed,
                )
            }
        }
    }
}

@Composable
private fun DownloadRow(
    model: SentinelViewModel,
    download: MediaDownload,
    busy: Boolean,
    onOpen: () -> Unit,
    onRetry: () -> Unit,
    onCancel: () -> Unit,
    onRemove: () -> Unit,
) {
    val active = download.isActive
    val downloading = download.state == "downloading"
    Card(Modifier.fillMaxWidth()) {
        Row(Modifier.padding(12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            PosterImage(
                model,
                download.posterUrl,
                download.title,
                Modifier.width(52.dp).height(78.dp).clip(RoundedCornerShape(6.dp)),
                maxWidthPx = 160,
            )
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(
                    download.title + (download.year?.let { " ($it)" } ?: ""),
                    style = MaterialTheme.typography.titleSmall,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                download.subtitle?.let { DownloadTag(it, SentinelCyan) }
                val release = listOfNotNull(download.releaseTitle, download.site).joinToString(" · ")
                if (release.isNotEmpty()) {
                    Text(
                        release,
                        style = MaterialTheme.typography.labelSmall,
                        color = SentinelTextDim,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                if (active) {
                    when (download.state) {
                        "downloading" -> LinearProgressIndicator(
                            progress = { download.progress.toFloat().coerceIn(0f, 1f) },
                            modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
                            color = SentinelGreen,
                        )
                        "queued" -> LinearProgressIndicator(
                            progress = { 0f },
                            modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp),
                            color = SentinelGreen,
                        )
                        else -> LinearProgressIndicator(Modifier.fillMaxWidth().padding(vertical = 2.dp), color = SentinelGreen)
                    }
                }
                val facts = if (active) {
                    listOfNotNull(
                        if (downloading) MediaDownloadFormat.percentLabel(download.progress) else MediaDownloadFormat.stateWord(download.state),
                        if (downloading) MediaDownloadFormat.speedLabel(download.speedBytesPerSecond) else null,
                        if (downloading) MediaDownloadFormat.etaLabel(download.etaSeconds) else null,
                        MediaDownloadFormat.sizeLabel(download.sizeBytes),
                        if (downloading) download.peers?.takeIf { it > 0 }?.let { MediaDownloadFormat.plural(it, "peer") } else null,
                    )
                } else {
                    listOfNotNull(
                        MediaDownloadFormat.stateWord(download.state),
                        MediaDownloadFormat.sizeLabel(download.sizeBytes),
                        download.requestedBy?.let { "by $it" },
                        ageLabel(download.finishedAt ?: download.createdAt),
                    )
                }
                Text(
                    facts.joinToString(" · "),
                    style = MaterialTheme.typography.labelMedium,
                    color = if (download.isFailed) SentinelRed else SentinelTextDim,
                )
                download.error?.let {
                    Text(it, style = MaterialTheme.typography.bodySmall, color = if (download.isFailed) SentinelRed else SentinelAmber)
                }
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    if (download.isDone && download.itemId != null) {
                        Button(onClick = onOpen, enabled = !busy) {
                            Icon(Icons.Default.PlayArrow, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(4.dp))
                            Text("Open")
                        }
                    }
                    if (download.isFailed) {
                        OutlinedButton(onClick = onRetry, enabled = !busy) {
                            Icon(Icons.Default.Replay, contentDescription = null, modifier = Modifier.size(18.dp))
                            Spacer(Modifier.width(4.dp))
                            Text("Retry")
                        }
                    }
                    Spacer(Modifier.weight(1f))
                    if (busy) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                    if (active) {
                        TextButton(onClick = onCancel, enabled = !busy) { Text("Cancel", color = SentinelRed) }
                    } else {
                        IconButton(onClick = onRemove, enabled = !busy) {
                            Icon(Icons.Default.Close, contentDescription = "Remove from the list", tint = SentinelTextDim)
                        }
                    }
                }
            }
        }
    }
}

// A slim bar at the top of Movies Home while anything is downloading; tapping it opens Downloads.
@Composable
internal fun DownloadBanner(active: List<MediaDownload>, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val text = MediaDownloadFormat.bannerText(active) ?: return
    Row(
        modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp)
            .clip(RoundedCornerShape(10.dp))
            .background(SentinelGreen.copy(alpha = 0.12f))
            .clickable(onClick = onClick)
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(Icons.Default.Download, contentDescription = null, tint = SentinelGreen, modifier = Modifier.size(18.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        Text("Downloads", style = MaterialTheme.typography.labelMedium, color = SentinelGreen)
    }
}

// ---------------- Sites (admin) ----------------

internal enum class SitesView { LIST, CATALOG, FORM }

internal class MovieSitesModel(private val app: SentinelViewModel) : ViewModel() {
    var view by mutableStateOf(SitesView.LIST)
    var sites by mutableStateOf<List<MediaDownloadSite>?>(null)
        private set
    // "Connecting to the VPN..." while the services come up; shown with a spinner.
    var waiting by mutableStateOf<String?>(null)
        private set
    var error by mutableStateOf<String?>(null)
    var catalog by mutableStateOf<List<MediaSiteDefinition>?>(null)
        private set
    var catalogLoading by mutableStateOf(false)
        private set
    var catalogError by mutableStateOf<String?>(null)
        private set
    var catalogQuery by mutableStateOf("")
    var adding by mutableStateOf<MediaSiteDefinition?>(null)
        private set
    val formValues = mutableStateMapOf<String, String>()
    val formChecks = mutableStateMapOf<String, Boolean>()
    var saving by mutableStateOf<String?>(null)
        private set
    var formError by mutableStateOf<String?>(null)
        private set
    var notice by mutableStateOf<String?>(null)
    val toggling = mutableStateMapOf<String, Boolean>()

    init {
        loadSites()
    }

    // Prowlarr lives inside the VPN, so the first request may find it still starting: the server
    // says so with a 503 { starting }, and this simply asks again every few seconds.
    private suspend fun <T> retrying(block: suspend () -> T): T {
        while (true) {
            try {
                val result = block()
                waiting = null
                return result
            } catch (starting: MediaDownloadsStartingException) {
                waiting = starting.message
                delay(SITES_RETRY_MS)
            }
        }
    }

    fun loadSites() {
        viewModelScope.launch {
            waiting = "Connecting to the VPN..."
            runCatching { retrying { app.mediaDownloadSites() } }
                .onSuccess { sites = it; error = null }
                .onFailure { error = it.message ?: "Could not load the sites." }
            waiting = null
        }
    }

    fun openCatalog() {
        view = SitesView.CATALOG
        if (catalog == null) loadCatalog()
    }

    fun loadCatalog() {
        viewModelScope.launch {
            catalogLoading = true
            catalogError = null
            runCatching { retrying { app.mediaDownloadSiteCatalog(catalogQuery) } }
                .onSuccess { catalog = it }
                .onFailure { catalogError = it.message ?: "Could not load the site list." }
            catalogLoading = false
        }
    }

    fun pick(definition: MediaSiteDefinition) {
        if (!definition.needsSetup) {
            addSite(definition, org.json.JSONObject())
            return
        }
        adding = definition
        formValues.clear()
        formChecks.clear()
        definition.fields.forEach { field ->
            formValues[field.name] = field.value
            formChecks[field.name] = field.checked
        }
        formError = null
        view = SitesView.FORM
    }

    fun submitForm() {
        val definition = adding ?: return
        addSite(definition, MediaDownloadApi.siteFieldsJson(definition.fields, formValues.toMap(), formChecks.toMap()))
    }

    private fun addSite(definition: MediaSiteDefinition, fields: org.json.JSONObject) {
        if (saving != null) return
        viewModelScope.launch {
            saving = definition.definition
            formError = null
            catalogError = null
            runCatching { retrying { app.addMediaDownloadSite(definition.definition, fields) } }
                .onSuccess {
                    notice = "${definition.name} added"
                    view = SitesView.LIST
                    adding = null
                    loadSites()
                }
                .onFailure {
                    val message = it.message ?: "Could not add ${definition.name}."
                    if (view == SitesView.FORM) formError = message else catalogError = message
                }
            saving = null
        }
    }

    fun toggle(site: MediaDownloadSite, enabled: Boolean) {
        val before = sites ?: return
        sites = before.map { if (it.id == site.id) it.copy(enabled = enabled) else it }
        toggling[site.id] = true
        viewModelScope.launch {
            runCatching { retrying { app.setMediaDownloadSiteEnabled(site.id, enabled) } }
                .onFailure {
                    sites = sites?.map { s -> if (s.id == site.id) s.copy(enabled = !enabled) else s }
                    error = it.message ?: "Could not change ${site.name}."
                }
            toggling.remove(site.id)
        }
    }

    fun remove(site: MediaDownloadSite) {
        viewModelScope.launch {
            runCatching { retrying { app.deleteMediaDownloadSite(site.id) } }
                .onFailure { error = it.message ?: "Could not remove ${site.name}." }
            loadSites()
        }
    }

    fun back(): Boolean = when (view) {
        SitesView.LIST -> false
        SitesView.CATALOG -> { view = SitesView.LIST; true }
        SitesView.FORM -> { view = SitesView.CATALOG; true }
    }
}

@Composable
fun MovieDownloadSitesScreen(model: SentinelViewModel, onBack: () -> Unit) {
    val sites: MovieSitesModel = viewModel { MovieSitesModel(model) }
    BackHandler(enabled = sites.view != SitesView.LIST) { sites.back() }
    var confirmRemove by remember { mutableStateOf<MediaDownloadSite?>(null) }

    val title = when (sites.view) {
        SitesView.LIST -> "Torrent sites"
        SitesView.CATALOG -> "Add a site"
        SitesView.FORM -> sites.adding?.name ?: "Set up"
    }
    Column(Modifier.fillMaxSize()) {
        DetailHeader(title, onBack = { if (!sites.back()) onBack() })
        LazyColumn(
            Modifier.fillMaxSize(),
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            when (sites.view) {
                SitesView.LIST -> sitesList(sites, onRemove = { confirmRemove = it })
                SitesView.CATALOG -> sitesCatalog(sites)
                SitesView.FORM -> sitesForm(sites)
            }
        }
    }

    confirmRemove?.let { site ->
        ConfirmDialog(
            title = "Remove ${site.name}?",
            text = "It is no longer searched.",
            confirmLabel = "Remove",
            onConfirm = {
                confirmRemove = null
                sites.remove(site)
            },
            onDismiss = { confirmRemove = null },
        )
    }
}

private fun LazyListScope.sitesList(sites: MovieSitesModel, onRemove: (MediaDownloadSite) -> Unit) {
    item {
        Text(
            "The sites searched when anyone adds a movie or show. Prowlarr keeps them working; everything goes through the VPN.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
    sites.notice?.let { message -> item { Text(message, color = SentinelGreen) } }
    sites.waiting?.let { message -> item { BusyLine(message) } }
    sites.error?.let { message -> item { Text(message, color = SentinelRed) } }
    val list = sites.sites
    if (list != null && list.isEmpty()) {
        item { Text("No sites yet. Add one to start searching.", color = SentinelTextDim) }
    }
    itemsIndexed(list.orEmpty(), key = { _, s -> "s-" + s.id }) { _, site ->
        Card(Modifier.fillMaxWidth()) {
            Row(Modifier.padding(start = 14.dp, top = 8.dp, bottom = 8.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(site.name, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
                        MediaDownloadFormat.privacyLabel(site.privacy)?.let { DownloadTag(it, privacyColor(site.privacy)) }
                    }
                    site.description?.let {
                        Text(it, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim, maxLines = 3, overflow = TextOverflow.Ellipsis)
                    }
                }
                Switch(
                    checked = site.enabled,
                    onCheckedChange = { sites.toggle(site, it) },
                    enabled = sites.toggling[site.id] != true,
                )
                IconButton(onClick = { onRemove(site) }) {
                    Icon(Icons.Default.Close, contentDescription = "Remove ${site.name}", tint = SentinelTextDim)
                }
            }
        }
    }
    item {
        Button(onClick = { sites.notice = null; sites.openCatalog() }, enabled = list != null, modifier = Modifier.fillMaxWidth()) {
            Icon(Icons.Default.Add, contentDescription = null, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(4.dp))
            Text("Add a site")
        }
    }
}

@Composable
private fun privacyColor(privacy: String?): Color = when (privacy) {
    "public" -> SentinelGreen
    "private" -> SentinelAmber
    else -> SentinelCyan
}

private fun LazyListScope.sitesCatalog(sites: MovieSitesModel) {
    item {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(
                value = sites.catalogQuery,
                onValueChange = { sites.catalogQuery = it },
                modifier = Modifier.weight(1f),
                singleLine = true,
                placeholder = { Text("Site name") },
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = { sites.loadCatalog() }),
            )
            Button(onClick = { sites.loadCatalog() }) {
                Icon(Icons.Default.Search, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(4.dp))
                Text("Find")
            }
        }
    }
    sites.catalogError?.let { message -> item { Text(message, color = SentinelRed) } }
    if (sites.catalogLoading) {
        item { BusyLine(sites.waiting ?: "Loading the site list…") }
        return
    }
    val catalog = sites.catalog ?: return
    if (catalog.isEmpty()) item { Text("No site by that name.", color = SentinelTextDim) }
    itemsIndexed(catalog, key = { i, d -> "c-${d.definition}-$i" }) { _, definition ->
        Card(Modifier.fillMaxWidth()) {
            Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(definition.name, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
                        MediaDownloadFormat.privacyLabel(definition.privacy)?.let { DownloadTag(it, privacyColor(definition.privacy)) }
                        definition.language?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim) }
                    }
                    definition.description?.let {
                        Text(it, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim, maxLines = 3, overflow = TextOverflow.Ellipsis)
                    }
                }
                if (sites.saving == definition.definition) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                } else {
                    OutlinedButton(onClick = { sites.pick(definition) }, enabled = sites.saving == null) {
                        Text(if (definition.needsSetup) "Set up" else "Add")
                    }
                }
            }
        }
    }
}

private fun LazyListScope.sitesForm(sites: MovieSitesModel) {
    val definition = sites.adding ?: return
    definition.description?.let { description ->
        item { Text(description, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
    }
    itemsIndexed(definition.fields, key = { _, f -> "f-" + f.name }) { _, field ->
        SiteFieldInput(sites, field)
    }
    sites.formError?.let { message -> item { Text(message, color = SentinelRed) } }
    sites.waiting?.takeIf { sites.saving != null }?.let { message -> item { BusyLine(message) } }
    item {
        Button(onClick = { sites.submitForm() }, enabled = sites.saving == null, modifier = Modifier.fillMaxWidth()) {
            if (sites.saving != null) {
                CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                Spacer(Modifier.width(8.dp))
            }
            Text("Add site")
        }
    }
}

@Composable
private fun SiteFieldInput(sites: MovieSitesModel, field: MediaSiteField) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        when (field.type) {
            "checkbox" -> Row(
                Modifier
                    .fillMaxWidth()
                    .clickable { sites.formChecks[field.name] = !(sites.formChecks[field.name] ?: field.checked) },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Checkbox(
                    checked = sites.formChecks[field.name] ?: field.checked,
                    onCheckedChange = { sites.formChecks[field.name] = it },
                )
                Text(field.label)
            }
            "select" -> {
                var open by remember { mutableStateOf(false) }
                val current = sites.formValues[field.name] ?: field.value
                val currentLabel = field.options.firstOrNull { it.value == current }?.label ?: current.ifBlank { "Choose…" }
                Text(field.label, style = MaterialTheme.typography.labelLarge)
                Box {
                    OutlinedButton(onClick = { open = true }, modifier = Modifier.fillMaxWidth()) {
                        Text(currentLabel, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Icon(Icons.Default.ArrowDropDown, contentDescription = null)
                    }
                    DropdownMenu(open, onDismissRequest = { open = false }) {
                        field.options.forEach { option ->
                            DropdownMenuItem(
                                text = { Text(option.label) },
                                onClick = {
                                    sites.formValues[field.name] = option.value
                                    open = false
                                },
                            )
                        }
                    }
                }
            }
            else -> OutlinedTextField(
                value = sites.formValues[field.name] ?: field.value,
                onValueChange = { sites.formValues[field.name] = it },
                label = { Text(field.label) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
                visualTransformation = if (field.type == "password") PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
                keyboardOptions = KeyboardOptions(
                    keyboardType = when (field.type) {
                        "password" -> KeyboardType.Password
                        "number" -> KeyboardType.Number
                        else -> KeyboardType.Text
                    },
                ),
            )
        }
        field.help?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim) }
    }
}

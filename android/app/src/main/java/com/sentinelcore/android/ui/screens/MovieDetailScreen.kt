package com.sentinelcore.android.ui.screens

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
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.RadioButtonUnchecked
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewmodel.compose.viewModel
import com.sentinelcore.android.data.MediaItemDetail
import com.sentinelcore.android.data.MediaItemSummary
import com.sentinelcore.android.data.MediaPlaybackMath
import com.sentinelcore.android.data.MediaSeason
import com.sentinelcore.android.ui.SentinelUiState
import com.sentinelcore.android.ui.SentinelViewModel
import com.sentinelcore.android.ui.theme.SentinelBackground
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.launch

internal class MovieDetailHolder : ViewModel() {
    var detail by mutableStateOf<MediaItemDetail?>(null)
}

// One movie, show or episode: artwork, what it is, Play / Resume / Start over, and for a show its
// seasons and episodes. Reloads each time it comes back into view, so returning from the player
// shows the new resume point and watched marks.
@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun MovieDetailScreen(
    state: SentinelUiState,
    model: SentinelViewModel,
    itemId: String,
    onBack: () -> Unit,
    onItem: (String) -> Unit,
    onPlay: (String, Double?) -> Unit,
) {
    // Held on the back-stack entry, so coming back from the player shows the page at once and
    // refreshes it in place rather than flashing "Loading…".
    val holder: MovieDetailHolder = viewModel(key = "movie-detail-$itemId") { MovieDetailHolder() }
    var detail by holder::detail
    var error by remember(itemId) { mutableStateOf<String?>(null) }
    var loading by remember(itemId) { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    var reloadTick by remember { mutableIntStateOf(0) }
    var selectedSeason by rememberSaveable(itemId) { mutableStateOf<Int?>(null) }
    val scope = rememberCoroutineScope()
    val isAdmin = state.authStatus?.role?.equals("Admin", ignoreCase = true) != false

    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event -> if (event == Lifecycle.Event.ON_RESUME) reloadTick++ }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }
    LaunchedEffect(itemId, reloadTick) {
        if (reloadTick == 0) return@LaunchedEffect
        loading = true
        runCatching { model.mediaItem(itemId) }
            .onSuccess { detail = it; error = null }
            .onFailure { error = it.message ?: "Could not load this title." }
        loading = false
    }
    val act: (suspend () -> Unit) -> Unit = { block ->
        scope.launch {
            busy = true
            runCatching { block() }.onFailure { error = it.message ?: "That didn't work." }
            busy = false
            reloadTick++
        }
    }

    val current = detail
    Column(Modifier.fillMaxSize()) {
        val headerTitle = current?.summary?.let { s -> if (s.isEpisode) s.showTitle ?: s.title else s.title }
        DetailHeader(headerTitle ?: "Loading…", onBack)
        if (loading || busy) LinearProgressIndicator(Modifier.fillMaxWidth(), color = SentinelGreen)
        if (current == null) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                val message = error
                if (message != null) ErrorPanel(message, onRetry = { reloadTick++ }) else CircularProgressIndicator(color = SentinelGreen)
            }
        } else {
            DetailBody(
                model = model,
                current = current,
                isAdmin = isAdmin,
                busy = busy,
                error = error,
                selectedSeason = selectedSeason,
                onSelectSeason = { selectedSeason = it },
                act = act,
                onItem = onItem,
                onPlay = onPlay,
            )
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
private fun DetailBody(
    model: SentinelViewModel,
    current: MediaItemDetail,
    isAdmin: Boolean,
    busy: Boolean,
    error: String?,
    selectedSeason: Int?,
    onSelectSeason: (Int) -> Unit,
    act: (suspend () -> Unit) -> Unit,
    onItem: (String) -> Unit,
    onPlay: (String, Double?) -> Unit,
) {
    val summary = current.summary
    val seasons = current.seasons.orEmpty()
    val season = seasons.firstOrNull { it.number == selectedSeason }
        ?: seasons.firstOrNull { s -> s.episodes.any { it.id == summary.nextEpisodeId } }
        ?: seasons.firstOrNull()

    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(bottom = 32.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        item { Backdrop(model, summary) }
        item { TitleBlock(model, summary, onShow = { summary.showId?.let(onItem) }) }
        error?.let { message -> item { Text(message, color = SentinelRed, modifier = Modifier.padding(horizontal = 16.dp)) } }
        item {
            FlowRow(
                modifier = Modifier.padding(horizontal = 16.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                PlayButtons(summary, seasons, onPlay)
                OutlinedButton(
                    enabled = !busy,
                    onClick = { act { model.setMediaWatched(summary.id, !summary.isWatched) } },
                ) {
                    Icon(
                        if (summary.isWatched) Icons.Default.CheckCircle else Icons.Default.RadioButtonUnchecked,
                        contentDescription = null,
                        modifier = Modifier.size(18.dp),
                    )
                    Text(if (summary.isWatched) "  Watched" else "  Mark watched")
                }
            }
        }
        summary.progressFraction?.let { fraction ->
            item {
                LinearProgressIndicator(
                    progress = { fraction },
                    modifier = Modifier.padding(horizontal = 16.dp).fillMaxWidth().height(4.dp),
                    color = SentinelGreen,
                )
            }
        }
        current.tagline?.let { tagline ->
            item {
                Text(
                    tagline,
                    style = MaterialTheme.typography.bodyMedium,
                    fontStyle = FontStyle.Italic,
                    color = SentinelTextDim,
                    modifier = Modifier.padding(horizontal = 16.dp),
                )
            }
        }
        current.overview?.let { overview ->
            item { Text(overview, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(horizontal = 16.dp)) }
        }

        if (seasons.isNotEmpty() && season != null) {
            item {
                Row(
                    Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    seasons.forEach { s ->
                        FilterChip(
                            selected = s.number == season.number,
                            onClick = { onSelectSeason(s.number) },
                            label = { Text(s.name) },
                        )
                    }
                }
            }
            item { SeasonHeader(season, busy = busy, onToggle = { watched -> act { model.setMediaSeasonWatched(summary.id, season.number, watched) } }) }
            items(season.episodes, key = { it.id }) { episode ->
                EpisodeRow(
                    model = model,
                    episode = episode,
                    isNext = episode.id == summary.nextEpisodeId,
                    onPlay = { onPlay(episode.id, null) },
                    onInfo = { onItem(episode.id) },
                )
            }
        }

        if (current.cast.isNotEmpty()) {
            item { SectionTitle("Cast") }
            item {
                Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    current.cast.take(12).forEach { member ->
                        Row {
                            Text(member.name, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
                            member.character?.let {
                                Text(it, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            }
                        }
                    }
                }
            }
        }

        current.file?.let { file ->
            item { SectionTitle("File") }
            item {
                Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    file.container?.let { MetadataRow("Container", it.uppercase()) }
                    val video = listOfNotNull(
                        file.videoCodec?.uppercase(),
                        file.videoProfile,
                        if (file.width != null && file.height != null) "${file.width}×${file.height}" else null,
                    ).joinToString(" · ")
                    if (video.isNotBlank()) MetadataRow("Video", video)
                    val audio = listOfNotNull(file.audioCodec?.uppercase(), file.audioChannels?.let { channelLabel(it) }).joinToString(" · ")
                    if (audio.isNotBlank()) MetadataRow("Audio", audio)
                    file.sizeBytes?.takeIf { it > 0 }?.let { MetadataRow("Size", MediaPlaybackMath.formatSize(it)) }
                    file.bitrate?.takeIf { it > 0 }?.let { MetadataRow("Bitrate", "%.1f Mbps".format(it / 1_000_000.0)) }
                    summary.durationSeconds?.let { MetadataRow("Length", MediaPlaybackMath.formatClock(it)) }
                    if (current.audioTracks.size > 1) {
                        MetadataRow("Audio tracks", current.audioTracks.joinToString(", ") { it.label ?: it.language ?: "Track ${it.index + 1}" })
                    }
                    if (current.subtitles.isNotEmpty()) {
                        MetadataRow("Subtitles", current.subtitles.joinToString(", ") { it.label ?: it.language ?: "Track ${it.index + 1}" })
                    }
                    if (isAdmin) file.path?.let { MetadataRow("Path", it) }
                }
            }
        }

        current.tmdbUrl?.let { url ->
            item {
                val uriHandler = LocalUriHandler.current
                TextButton(onClick = { runCatching { uriHandler.openUri(url) } }, modifier = Modifier.padding(horizontal = 8.dp)) {
                    Text("View on TMDB")
                }
            }
        }
    }
}

@Composable
private fun Backdrop(model: SentinelViewModel, item: MediaItemSummary) {
    Box(
        Modifier
            .fillMaxWidth()
            .aspectRatio(16f / 9f)
            .heightIn(max = 280.dp),
    ) {
        PosterImage(
            model = model,
            path = item.backdropUrl ?: item.thumbUrl ?: item.posterUrl,
            title = item.title,
            modifier = Modifier.fillMaxSize(),
            maxWidthPx = 1280,
        )
        Box(
            Modifier
                .fillMaxSize()
                .background(Brush.verticalGradient(0.5f to Color.Transparent, 1f to SentinelBackground)),
        )
    }
}

@Composable
private fun TitleBlock(model: SentinelViewModel, item: MediaItemSummary, onShow: () -> Unit) {
    Row(
        Modifier.padding(horizontal = 16.dp),
        horizontalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        if (!item.isEpisode) {
            Box(
                Modifier
                    .width(100.dp)
                    .aspectRatio(2f / 3f)
                    .clip(RoundedCornerShape(8.dp)),
            ) {
                PosterImage(model, item.posterUrl, item.title, Modifier.fillMaxSize())
            }
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            if (item.isEpisode) {
                item.showTitle?.let {
                    Text(
                        it,
                        style = MaterialTheme.typography.titleSmall,
                        color = SentinelCyan,
                        modifier = Modifier.clickable(onClick = onShow),
                    )
                }
                item.episodeCode?.let { Text(it, style = MaterialTheme.typography.labelMedium, color = SentinelTextDim) }
            }
            Text(item.title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
            metaLine(item)?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = SentinelTextDim) }
            if (item.genres.isNotEmpty()) {
                Text(item.genres.joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = SentinelTextDim)
            }
            if (item.isShow && item.episodeCount != null) {
                Text(
                    "${item.watchedEpisodeCount ?: 0} of ${item.episodeCount} episodes watched",
                    style = MaterialTheme.typography.bodySmall,
                    color = SentinelTextDim,
                )
            }
            item.airDate?.let { Text("Aired $it", style = MaterialTheme.typography.bodySmall, color = SentinelTextDim) }
        }
    }
}

// Play / Resume / Start over for a movie or episode; for a show, the episode to play next.
@Composable
private fun PlayButtons(item: MediaItemSummary, seasons: List<MediaSeason>, onPlay: (String, Double?) -> Unit) {
    if (item.isShow) {
        val next = seasons.flatMap { it.episodes }.firstOrNull { it.id == item.nextEpisodeId } ?: return
        val resuming = MediaPlaybackMath.resumeSeconds(next.progress) != null
        Button(onClick = { onPlay(next.id, null) }) {
            Icon(Icons.Default.PlayArrow, contentDescription = null)
            Text((if (resuming) "Resume " else "Play ") + (next.episodeCode ?: next.title))
        }
        return
    }
    val resume = MediaPlaybackMath.resumeSeconds(item.progress)
    if (resume != null) {
        Button(onClick = { onPlay(item.id, null) }) {
            Icon(Icons.Default.PlayArrow, contentDescription = null)
            Text("Resume from ${MediaPlaybackMath.formatClock(resume)}")
        }
        OutlinedButton(onClick = { onPlay(item.id, 0.0) }) {
            Icon(Icons.Default.Replay, contentDescription = null, modifier = Modifier.size(18.dp))
            Text("  Start over")
        }
    } else {
        Button(onClick = { onPlay(item.id, 0.0) }) {
            Icon(Icons.Default.PlayArrow, contentDescription = null)
            Text("Play")
        }
    }
}

@Composable
private fun SectionTitle(title: String) {
    Column(Modifier.padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        HorizontalDivider()
        Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
private fun SeasonHeader(season: MediaSeason, busy: Boolean, onToggle: (Boolean) -> Unit) {
    val allWatched = season.episodeCount > 0 && season.watchedCount >= season.episodeCount
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            "${season.episodeCount} episodes · ${season.watchedCount} watched",
            style = MaterialTheme.typography.labelMedium,
            color = SentinelTextDim,
            modifier = Modifier.weight(1f),
        )
        TextButton(enabled = !busy, onClick = { onToggle(!allWatched) }) {
            Text(if (allWatched) "Mark season unwatched" else "Mark season watched")
        }
    }
}

@Composable
private fun EpisodeRow(
    model: SentinelViewModel,
    episode: MediaItemSummary,
    isNext: Boolean,
    onPlay: () -> Unit,
    onInfo: () -> Unit,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onPlay)
            .padding(horizontal = 16.dp, vertical = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .width(140.dp)
                .aspectRatio(16f / 9f)
                .clip(RoundedCornerShape(6.dp)),
        ) {
            PosterImage(model, episode.thumbUrl, episode.title, Modifier.fillMaxSize(), maxWidthPx = 480)
            episode.progressFraction?.let { fraction ->
                LinearProgressIndicator(
                    progress = { fraction },
                    modifier = Modifier.align(Alignment.BottomCenter).fillMaxWidth().height(3.dp),
                    color = SentinelGreen,
                    trackColor = Color.Black.copy(alpha = 0.5f),
                )
            }
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            val number = episode.episodeNumber?.let { "$it. " }.orEmpty()
            Text(
                number + episode.title,
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = if (isNext) FontWeight.Bold else FontWeight.Normal,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            val meta = listOfNotNull(
                if (isNext) "Up next" else null,
                episode.airDate,
                episode.runtimeMinutes?.takeIf { it > 0 }?.let { MediaPlaybackMath.formatRuntime(it) }
                    ?: episode.durationSeconds?.let { MediaPlaybackMath.formatRuntime((it / 60).toInt()) },
            ).joinToString(" · ")
            if (meta.isNotBlank()) {
                Text(meta, style = MaterialTheme.typography.labelSmall, color = if (isNext) SentinelCyan else SentinelTextDim)
            }
        }
        if (episode.isWatched) {
            Icon(Icons.Default.CheckCircle, contentDescription = "Watched", tint = SentinelGreen, modifier = Modifier.size(20.dp))
        }
        IconButton(onClick = onInfo) {
            Icon(Icons.Default.Info, contentDescription = "Episode details", tint = SentinelTextDim)
        }
    }
}

private fun channelLabel(channels: Int): String = when (channels) {
    1 -> "Mono"
    2 -> "Stereo"
    6 -> "5.1"
    8 -> "7.1"
    else -> "$channels ch"
}

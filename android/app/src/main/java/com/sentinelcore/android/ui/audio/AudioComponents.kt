package com.sentinelcore.android.ui.audio

import android.graphics.Bitmap
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
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
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowForward
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.DownloadDone
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Podcasts
import androidx.compose.material.icons.filled.QueueMusic
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.Album
import com.sentinelcore.android.audio.Artist
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.AudioItem
import com.sentinelcore.android.audio.DirectoryShow
import com.sentinelcore.android.audio.Episode
import com.sentinelcore.android.audio.Mix
import com.sentinelcore.android.audio.Playlist
import com.sentinelcore.android.audio.Podcast
import com.sentinelcore.android.audio.Track
import kotlin.math.abs

// ---------------- loading ----------------

// One screen's worth of server data: loaded when the keys change, kept while it reloads (so a
// refresh never blanks the screen), with the server's own words when it fails.
@Stable
class Loadable<T> {
    var value by mutableStateOf<T?>(null)
    var error by mutableStateOf<String?>(null)
    var loading by mutableStateOf(false)
    var tick by mutableIntStateOf(0)

    fun reload() {
        tick++
    }
}

@Composable
fun <T> rememberLoadable(vararg keys: Any?, load: suspend () -> T): Loadable<T> {
    val ui = LocalAudioUi.current
    val loadable = remember(*keys) { Loadable<T>() }
    LaunchedEffect(loadable, loadable.tick, ui.libraryVersion) {
        loadable.loading = true
        try {
            loadable.value = load()
            loadable.error = null
        } catch (error: Exception) {
            if (error is kotlinx.coroutines.CancellationException) throw error
            loadable.error = error.message ?: "Couldn't load this."
        }
        loadable.loading = false
    }
    return loadable
}

// Spinner first time, the error with Try Again when there's nothing to show, else the content.
@Composable
fun <T> LoadableContent(loadable: Loadable<T>, modifier: Modifier = Modifier, content: @Composable (T) -> Unit) {
    val value = loadable.value
    when {
        value != null -> content(value)
        loadable.error != null -> ErrorPanel(loadable.error.orEmpty(), modifier) { loadable.reload() }
        else -> Box(modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
    }
}

@Composable
fun ErrorPanel(message: String, modifier: Modifier = Modifier, onRetry: (() -> Unit)? = null) {
    Column(
        modifier.fillMaxWidth().padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(message, textAlign = TextAlign.Center, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (onRetry != null) {
            OutlinedButton(onClick = onRetry) {
                Icon(Icons.Default.Refresh, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text("Try again")
            }
        }
    }
}

@Composable
fun EmptyPanel(icon: ImageVector, title: String, detail: String, modifier: Modifier = Modifier, action: (@Composable () -> Unit)? = null) {
    Column(
        modifier.fillMaxWidth().padding(horizontal = 32.dp, vertical = 48.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(icon, contentDescription = null, modifier = Modifier.size(48.dp), tint = MaterialTheme.colorScheme.primary.copy(alpha = 0.7f))
        Text(title, style = MaterialTheme.typography.titleMedium, textAlign = TextAlign.Center)
        Text(detail, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.Center)
        action?.invoke()
    }
}

// ---------------- art ----------------

// A colour pair picked from a name, so placeholder art differs from row to row instead of a wall
// of identical grey squares.
private val placeholderPalette = listOf(
    Color(0xFF3A6EA5) to Color(0xFF1B2F4B),
    Color(0xFF8E44AD) to Color(0xFF3B1A4A),
    Color(0xFF16A085) to Color(0xFF0B3D33),
    Color(0xFFD35400) to Color(0xFF4A1E05),
    Color(0xFFC0392B) to Color(0xFF451410),
    Color(0xFF2C7A7B) to Color(0xFF123536),
    Color(0xFF6D5D3F) to Color(0xFF2B2418),
)

fun placeholderBrush(seed: String): Brush {
    val (a, b) = placeholderPalette[abs(seed.hashCode()) % placeholderPalette.size]
    return Brush.linearGradient(listOf(a, b))
}

@Composable
fun ArtImage(
    path: String?,
    modifier: Modifier = Modifier,
    sizePx: Int = 300,
    shape: Shape = RoundedCornerShape(8.dp),
    fallbackIcon: ImageVector = Icons.Default.MusicNote,
    seed: String = path.orEmpty(),
    onBitmap: ((Bitmap) -> Unit)? = null,
) {
    val ui = LocalAudioUi.current
    var bitmap by remember(path, sizePx) { mutableStateOf(ArtCache.peek(path, sizePx)) }
    LaunchedEffect(path, sizePx) {
        if (bitmap == null && !path.isNullOrBlank()) bitmap = runCatching { ArtCache.load(ui.runtime, path, sizePx) }.getOrNull()
        bitmap?.let { onBitmap?.invoke(it) }
    }
    Box(modifier.clip(shape).background(placeholderBrush(seed)), contentAlignment = Alignment.Center) {
        val image = bitmap
        if (image != null) {
            Image(image.asImageBitmap(), contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        } else {
            Icon(fallbackIcon, contentDescription = null, tint = Color.White.copy(alpha = 0.75f), modifier = Modifier.fillMaxSize(0.4f))
        }
    }
}

// A playlist's 2×2 mosaic of album art (one picture when there's only one).
@Composable
fun MosaicArt(urls: List<String>, modifier: Modifier = Modifier, seed: String, shape: Shape = RoundedCornerShape(8.dp), sizePx: Int = 300) {
    val distinct = urls.distinct()
    if (distinct.size < 4) {
        ArtImage(distinct.firstOrNull(), modifier, sizePx, shape, Icons.Default.QueueMusic, seed)
        return
    }
    Column(modifier.clip(shape)) {
        for (row in 0..1) {
            Row(Modifier.weight(1f)) {
                for (column in 0..1) {
                    ArtImage(distinct[row * 2 + column], Modifier.weight(1f).fillMaxSize(), sizePx / 2, RoundedCornerShape(0.dp), seed = seed)
                }
            }
        }
    }
}

// ---------------- section headings and shelves ----------------

@Composable
fun SectionTitle(title: String, modifier: Modifier = Modifier, subtitle: String? = null, actionLabel: String? = null, onAction: (() -> Unit)? = null) {
    Row(
        modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp, top = 18.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        if (onAction != null) {
            TextButton(onClick = onAction) {
                Text(actionLabel ?: "See all")
                Icon(Icons.Default.ArrowForward, contentDescription = null, modifier = Modifier.size(16.dp).padding(start = 2.dp))
            }
        }
    }
}

@Composable
fun Shelf(content: LazyListScope.() -> Unit) {
    LazyRow(
        contentPadding = PaddingValues(horizontal = 16.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        content = content,
    )
}

// ---------------- cards ----------------

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun AlbumCard(album: Album, width: Dp = 148.dp, onLongClick: (() -> Unit)? = null, onClick: () -> Unit) {
    Column(
        Modifier.width(width).clip(RoundedCornerShape(10.dp)).combinedClickable(onClick = onClick, onLongClick = onLongClick),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        ArtImage(album.artUrl, Modifier.fillMaxWidth().aspectRatio(1f), seed = album.title)
        Text(album.title, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(
            listOfNotNull(album.artist.takeIf { it.isNotBlank() }, album.year?.toString()).joinToString(" · "),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
fun ArtistCircle(artist: Artist, width: Dp = 112.dp, onClick: () -> Unit) {
    Column(
        Modifier.width(width).clip(RoundedCornerShape(10.dp)).combinedClickableCompat(onClick),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        ArtImage(artist.artUrl, Modifier.fillMaxWidth().aspectRatio(1f), shape = CircleShape, fallbackIcon = Icons.Default.Person, seed = artist.name)
        Text(artist.name, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis, textAlign = TextAlign.Center)
    }
}

@Composable
fun PlaylistCard(playlist: Playlist, width: Dp = 148.dp, onClick: () -> Unit) {
    Column(Modifier.width(width).clip(RoundedCornerShape(10.dp)).combinedClickableCompat(onClick), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        MosaicArt(playlist.artUrls, Modifier.fillMaxWidth().aspectRatio(1f), seed = playlist.name)
        Text(playlist.name, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(
            playlistSubtitle(playlist),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

fun playlistSubtitle(playlist: Playlist): String = buildList {
    if (playlist.smart) add("Smart")
    add(if (playlist.trackCount == 1) "1 song" else "${playlist.trackCount} songs")
    if (!playlist.mine) playlist.ownerName?.let { add("by $it") }
}.joinToString(" · ")

@Composable
fun MixCard(mix: Mix, width: Dp = 148.dp, onClick: () -> Unit) {
    Column(Modifier.width(width).clip(RoundedCornerShape(10.dp)).combinedClickableCompat(onClick), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Box(Modifier.fillMaxWidth().aspectRatio(1f)) {
            MosaicArt(mix.artUrls, Modifier.fillMaxSize(), seed = mix.id)
            Box(
                Modifier.fillMaxSize().clip(RoundedCornerShape(8.dp))
                    .background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.7f)))),
            )
            Text(
                mix.name,
                color = Color.White,
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.Bold,
                modifier = Modifier.align(Alignment.BottomStart).padding(10.dp),
                maxLines = 2,
            )
        }
        Text(mix.description, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
fun ShowCard(podcast: Podcast, width: Dp = 132.dp, onClick: () -> Unit) {
    Column(Modifier.width(width).clip(RoundedCornerShape(10.dp)).combinedClickableCompat(onClick), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Box(Modifier.fillMaxWidth().aspectRatio(1f)) {
            ArtImage(podcast.artUrl, Modifier.fillMaxSize(), fallbackIcon = Icons.Default.Podcasts, seed = podcast.title)
            if (podcast.unplayedCount > 0) {
                Badge(podcast.unplayedCount.toString(), Modifier.align(Alignment.TopEnd).padding(6.dp))
            }
        }
        Text(podcast.title, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium, maxLines = 2, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
fun DirectoryShowCard(show: DirectoryShow, width: Dp = 132.dp, rank: Int? = null, onClick: () -> Unit) {
    Column(Modifier.width(width).clip(RoundedCornerShape(10.dp)).combinedClickableCompat(onClick), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Box(Modifier.fillMaxWidth().aspectRatio(1f)) {
            ArtImage(show.artUrl, Modifier.fillMaxSize(), fallbackIcon = Icons.Default.Podcasts, seed = show.title)
            if (show.subscribed) {
                Icon(
                    Icons.Default.CheckCircle,
                    contentDescription = "Following",
                    tint = Color.White,
                    modifier = Modifier.align(Alignment.TopEnd).padding(6.dp).size(22.dp).background(MaterialTheme.colorScheme.primary, CircleShape),
                )
            }
        }
        Text(
            (rank?.let { "$it. " } ?: "") + show.title,
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Medium,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
        Text(show.author, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
fun Badge(text: String, modifier: Modifier = Modifier) {
    Box(
        modifier.background(MaterialTheme.colorScheme.primary, RoundedCornerShape(10.dp)).padding(horizontal = 7.dp, vertical = 2.dp),
    ) {
        Text(text, color = MaterialTheme.colorScheme.onPrimary, style = MaterialTheme.typography.labelSmall, fontWeight = FontWeight.Bold)
    }
}

@OptIn(ExperimentalFoundationApi::class)
fun Modifier.combinedClickableCompat(onClick: () -> Unit, onLongClick: (() -> Unit)? = null): Modifier =
    this.combinedClickable(onClick = onClick, onLongClick = onLongClick)

// ---------------- rows ----------------

// Three bars bouncing, for the row that is playing now.
@Composable
fun PlayingBars(modifier: Modifier = Modifier, playing: Boolean, color: Color = MaterialTheme.colorScheme.primary) {
    val transition = rememberInfiniteTransition(label = "bars")
    val phases = (0..2).map { index ->
        transition.animateFloat(
            initialValue = 0.25f,
            targetValue = 1f,
            animationSpec = infiniteRepeatable(tween(420 + index * 130), RepeatMode.Reverse),
            label = "bar$index",
        )
    }
    Row(modifier.size(16.dp), horizontalArrangement = Arrangement.spacedBy(2.dp), verticalAlignment = Alignment.Bottom) {
        phases.forEach { phase ->
            Box(Modifier.weight(1f).height((15 * (if (playing) phase.value else 0.35f)).dp).background(color, RoundedCornerShape(1.dp)))
        }
    }
}


@OptIn(ExperimentalFoundationApi::class)
@Composable
fun TrackRow(
    track: Track,
    modifier: Modifier = Modifier,
    number: Int? = null,
    showArt: Boolean = true,
    showAlbum: Boolean = false,
    onMore: (() -> Unit)? = null,
    trailing: (@Composable () -> Unit)? = null,
    onClick: () -> Unit,
) {
    val ui = LocalAudioUi.current
    val state by ui.engine.state.collectAsState()
    val downloads by ui.runtime.downloads.entries.collectAsState()
    val isCurrent = state.current?.type == AudioItem.TYPE_TRACK && state.current?.id == track.id
    val downloaded = downloads.any { it.done && it.id == track.id && it.type == AudioItem.TYPE_TRACK }
    Row(
        modifier.fillMaxWidth().combinedClickable(onClick = onClick, onLongClick = onMore).padding(start = 16.dp, end = 4.dp, top = 6.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        when {
            showArt -> Box(Modifier.size(48.dp)) {
                ArtImage(track.artUrl, Modifier.fillMaxSize(), sizePx = 144, shape = RoundedCornerShape(6.dp), seed = track.album)
                if (isCurrent) {
                    Box(Modifier.fillMaxSize().clip(RoundedCornerShape(6.dp)).background(Color.Black.copy(alpha = 0.45f)), contentAlignment = Alignment.Center) {
                        PlayingBars(playing = state.playing, color = Color.White)
                    }
                }
            }
            number != null -> Box(Modifier.width(32.dp), contentAlignment = Alignment.Center) {
                if (isCurrent) PlayingBars(playing = state.playing) else {
                    Text("$number", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(
                track.title,
                style = MaterialTheme.typography.bodyLarge,
                color = if (isCurrent) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (downloaded) {
                    Icon(Icons.Default.DownloadDone, contentDescription = "Downloaded", modifier = Modifier.size(14.dp).padding(end = 3.dp), tint = MaterialTheme.colorScheme.primary)
                }
                if (track.loved) {
                    Icon(Icons.Default.Favorite, contentDescription = "Loved", modifier = Modifier.size(13.dp).padding(end = 3.dp), tint = Color(0xFFE5486B))
                }
                Text(
                    listOfNotNull(track.artist.takeIf { it.isNotBlank() }, track.album.takeIf { showAlbum && it.isNotBlank() }).joinToString(" · ")
                        .ifBlank { AudioFormat.clock(track.durationSeconds) },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
        trailing?.invoke()
        if (!showArt && trailing == null) {
            Text(AudioFormat.clock(track.durationSeconds), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (onMore != null) {
            IconButton(onClick = onMore) { Icon(Icons.Default.MoreVert, contentDescription = "More for ${track.title}") }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
fun EpisodeRow(
    episode: Episode,
    modifier: Modifier = Modifier,
    showShow: Boolean = true,
    onMore: (() -> Unit)? = null,
    onClick: () -> Unit,
) {
    val ui = LocalAudioUi.current
    val state by ui.engine.state.collectAsState()
    val downloads by ui.runtime.downloads.entries.collectAsState()
    val isCurrent = state.current?.isEpisode == true && state.current?.id == episode.id
    val downloaded = downloads.any { it.done && it.id == episode.id && it.type == AudioItem.TYPE_EPISODE }
    // The live progress for the one playing now; the server's for the rest.
    val live = state.queue.firstOrNull { it.isEpisode && it.id == episode.id }?.episode?.progress
    val progress = if (isCurrent) live ?: episode.progress else episode.progress
    Row(
        modifier.fillMaxWidth().combinedClickable(onClick = onClick, onLongClick = onMore).padding(start = 16.dp, end = 4.dp, top = 10.dp, bottom = 10.dp),
        verticalAlignment = Alignment.Top,
    ) {
        ArtImage(episode.artUrl, Modifier.size(64.dp), sizePx = 192, fallbackIcon = Icons.Default.Podcasts, seed = episode.podcastTitle)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(
                listOfNotNull(
                    AudioFormat.day(episode.publishedAt).takeIf { it.isNotBlank() },
                    if (showShow) episode.podcastTitle.takeIf { it.isNotBlank() } else null,
                    episode.season?.let { s -> episode.episode?.let { "S$s E$it" } ?: "Season $s" } ?: episode.episode?.let { "Ep. $it" },
                    episode.episodeType.takeIf { it != "full" }?.replaceFirstChar(Char::uppercase),
                ).joinToString(" · "),
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Text(
                episode.title,
                style = MaterialTheme.typography.bodyLarge,
                fontWeight = if (progress?.played == true) FontWeight.Normal else FontWeight.Medium,
                color = when {
                    isCurrent -> MaterialTheme.colorScheme.primary
                    progress?.played == true -> MaterialTheme.colorScheme.onSurfaceVariant
                    else -> MaterialTheme.colorScheme.onSurface
                },
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
            if (episode.summary.isNotBlank()) {
                Text(episode.summary, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 4.dp)) {
                FilledTonalButton(
                    onClick = {
                        if (isCurrent) ui.engine.togglePlay() else ui.playEpisode(episode)
                    },
                    contentPadding = PaddingValues(horizontal = 10.dp, vertical = 0.dp),
                    modifier = Modifier.height(30.dp),
                ) {
                    if (isCurrent && state.playing) PlayingBars(playing = true, modifier = Modifier.size(14.dp))
                    else Icon(Icons.Default.PlayArrow, contentDescription = null, modifier = Modifier.size(16.dp))
                    Spacer(Modifier.width(4.dp))
                    val remaining = when {
                        progress?.played == true -> "Played"
                        progress != null && progress.positionSeconds > 0 -> {
                            val total = progress.durationSeconds?.takeIf { it > 0 } ?: episode.durationSeconds
                            AudioFormat.minutesLeft(total - progress.positionSeconds)
                        }
                        else -> AudioFormat.duration(episode.durationSeconds)
                    }
                    Text(remaining, style = MaterialTheme.typography.labelMedium)
                }
                if (progress != null && !progress.played && progress.positionSeconds > 0) {
                    val total = progress.durationSeconds?.takeIf { it > 0 } ?: episode.durationSeconds
                    LinearProgressIndicator(
                        progress = { if (total > 0) (progress.positionSeconds / total).toFloat().coerceIn(0f, 1f) else 0f },
                        modifier = Modifier.width(48.dp).height(4.dp).clip(RoundedCornerShape(2.dp)),
                    )
                }
                if (downloaded) Icon(Icons.Default.DownloadDone, contentDescription = "Downloaded to this phone", modifier = Modifier.size(16.dp), tint = MaterialTheme.colorScheme.primary)
                if (episode.inQueue) Icon(Icons.Default.QueueMusic, contentDescription = "In Up Next", modifier = Modifier.size(16.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        if (onMore != null) {
            IconButton(onClick = onMore) { Icon(Icons.Default.MoreVert, contentDescription = "More for ${episode.title}") }
        }
    }
}

// Play and Shuffle side by side, for album, artist and playlist pages.
@Composable
fun PlayShuffleRow(onPlay: () -> Unit, onShuffle: (() -> Unit)?, modifier: Modifier = Modifier, playLabel: String = "Play") {
    Row(modifier.fillMaxWidth().padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Button(onClick = onPlay, modifier = Modifier.weight(1f), colors = ButtonDefaults.buttonColors()) {
            Icon(Icons.Default.PlayArrow, contentDescription = null)
            Spacer(Modifier.width(6.dp))
            Text(playLabel)
        }
        if (onShuffle != null) {
            FilledTonalButton(onClick = onShuffle, modifier = Modifier.weight(1f)) {
                Icon(Icons.Default.Shuffle, contentDescription = null)
                Spacer(Modifier.width(6.dp))
                Text("Shuffle")
            }
        }
    }
}

// A big-art header shared by album, playlist, show and episode pages.
@Composable
fun HeroHeader(
    art: @Composable BoxScope.() -> Unit,
    title: String,
    subtitle: String?,
    detail: String?,
    onSubtitle: (() -> Unit)? = null,
) {
    Column(Modifier.fillMaxWidth().padding(top = 8.dp, bottom = 12.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        Box(Modifier.fillMaxWidth(0.62f).aspectRatio(1f), content = art)
        Spacer(Modifier.height(14.dp))
        Text(title, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center, modifier = Modifier.padding(horizontal = 24.dp))
        if (!subtitle.isNullOrBlank()) {
            Text(
                subtitle,
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.primary,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(horizontal = 24.dp, vertical = 2.dp).then(if (onSubtitle != null) Modifier.combinedClickableCompat(onSubtitle) else Modifier),
            )
        }
        if (!detail.isNullOrBlank()) {
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.Center, modifier = Modifier.padding(horizontal = 24.dp))
        }
    }
}

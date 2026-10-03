package com.sentinelcore.android.ui.audio

import androidx.activity.compose.BackHandler
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
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
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.QueueMusic
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.BookmarkAdd
import androidx.compose.material.icons.filled.Cast
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.DragHandle
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.FavoriteBorder
import androidx.compose.material.icons.filled.Forward30
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.Lyrics
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Podcasts
import androidx.compose.material.icons.filled.Repeat
import androidx.compose.material.icons.filled.RepeatOne
import androidx.compose.material.icons.filled.Replay
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.Speed
import androidx.compose.material.icons.filled.Subject
import androidx.compose.material.icons.filled.Toc
import androidx.compose.material.icons.filled.Replay10
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.IconButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.platform.LocalView
import androidx.compose.runtime.DisposableEffect
import androidx.core.view.WindowCompat
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.AudioItem
import com.sentinelcore.android.audio.Chapter
import com.sentinelcore.android.audio.Lyrics
import com.sentinelcore.android.audio.TimedText
import com.sentinelcore.android.audio.Transcript
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.distinctUntilChanged

// Now Playing is always dark (a wash of the art's colour), so it gets a dark scheme with the
// app's accent lightened: text fields, chips and highlights stay readable in a light-themed app.
@Composable
fun NowPlayingScreen(onClose: () -> Unit) {
    val scheme = androidx.compose.material3.MaterialTheme.colorScheme
    androidx.compose.material3.MaterialTheme(
        colorScheme = androidx.compose.material3.darkColorScheme(
            primary = if (androidx.compose.foundation.isSystemInDarkTheme()) scheme.primary else scheme.inversePrimary,
            onSurface = Color.White,
            onSurfaceVariant = Color.White.copy(alpha = 0.7f),
            surfaceVariant = Color(0xFF2A2F33),
            surface = Color(0xFF15191C),
        ),
    ) { NowPlayingContent(onClose) }
}

private enum class NowPlayingPanel { ART, LYRICS, CHAPTERS, TRANSCRIPT, QUEUE }

// The full Now Playing screen (§11): big art over a wash of its own colour, the scrubber, the
// controls for music or a podcast, and panels for synced lyrics, chapters, a following
// transcript and the queue.
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun NowPlayingContent(onClose: () -> Unit) {
    val ui = LocalAudioUi.current
    val engine = ui.engine
    val state by engine.state.collectAsState()
    val progress by engine.progress.collectAsState()
    val current = state.current
    var panel by remember { mutableStateOf(NowPlayingPanel.ART) }
    var mood by remember { mutableStateOf(Color(0xFF1E2A30)) }
    val background by animateColorAsState(mood, tween(700), label = "mood")
    var showSleep by remember { mutableStateOf(false) }
    var showSpeed by remember { mutableStateOf(false) }
    var showBookmark by remember { mutableStateOf(false) }
    var menu by remember { mutableStateOf(false) }

    // Light status-bar icons over the dark wash, put back when Now Playing closes.
    val view = LocalView.current
    DisposableEffect(view) {
        val window = (view.context as? android.app.Activity)?.window
        val controller = window?.let { WindowCompat.getInsetsController(it, view) }
        val wasLight = controller?.isAppearanceLightStatusBars
        controller?.isAppearanceLightStatusBars = false
        onDispose { if (wasLight != null) controller.isAppearanceLightStatusBars = wasLight }
    }

    BackHandler {
        if (panel != NowPlayingPanel.ART) panel = NowPlayingPanel.ART else onClose()
    }
    // The queue ran out or was cleared: nothing to show.
    LaunchedEffect(current == null) { if (current == null) onClose() }
    current ?: return
    val isEpisode = current.isEpisode
    // Switching between a song and an episode resets a panel that doesn't apply.
    LaunchedEffect(isEpisode) {
        if (isEpisode && panel == NowPlayingPanel.LYRICS) panel = NowPlayingPanel.ART
        if (!isEpisode && (panel == NowPlayingPanel.CHAPTERS || panel == NowPlayingPanel.TRANSCRIPT)) panel = NowPlayingPanel.ART
    }

    Box(
        Modifier.fillMaxSize()
            .background(Brush.verticalGradient(listOf(background, Color(0xFF0B0E10))))
            .clickable(enabled = false) {},
    ) {
        Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
            // ---- top bar ----
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = onClose) { Icon(Icons.Default.KeyboardArrowDown, contentDescription = "Close", tint = Color.White) }
                Column(Modifier.weight(1f), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(
                        if (isEpisode) "PLAYING PODCAST" else "PLAYING FROM",
                        style = MaterialTheme.typography.labelSmall,
                        color = Color.White.copy(alpha = 0.6f),
                        letterSpacing = 1.5.sp,
                    )
                    Text(
                        state.context?.name ?: if (isEpisode) current.subtitle else "Your queue",
                        style = MaterialTheme.typography.labelLarge,
                        color = Color.White,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                Box {
                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = "More", tint = Color.White) }
                    NowPlayingMenu(current, expanded = menu, onDismiss = { menu = false }, onClose = onClose)
                }
            }

            // ---- art or a panel ----
            Box(Modifier.weight(1f).fillMaxWidth().clipToBounds()) {
                when (panel) {
                    NowPlayingPanel.ART -> Box(Modifier.fillMaxSize().padding(horizontal = 28.dp, vertical = 12.dp), contentAlignment = Alignment.Center) {
                        val scale by animateFloatAsState(if (state.playing) 1f else 0.9f, tween(350), label = "artScale")
                        ArtImage(
                            current.artUrl,
                            Modifier.widthIn(max = 420.dp).fillMaxWidth().aspectRatio(1f).scale(scale),
                            sizePx = 900,
                            shape = RoundedCornerShape(14.dp),
                            fallbackIcon = if (isEpisode) Icons.Default.Podcasts else Icons.Default.Lyrics,
                            seed = current.album ?: current.title,
                            onBitmap = { mood = ArtCache.moodColor(it) },
                        )
                    }
                    else -> Column(Modifier.fillMaxSize()) {
                        CompactHeader(current, state.playing) { panel = NowPlayingPanel.ART }
                        when (panel) {
                            NowPlayingPanel.LYRICS -> LyricsPanel(current, progress.positionSeconds)
                            NowPlayingPanel.CHAPTERS -> ChaptersPanel(current, progress.positionSeconds)
                            NowPlayingPanel.TRANSCRIPT -> TranscriptPanel(current, progress.positionSeconds)
                            NowPlayingPanel.QUEUE -> QueuePanel()
                            else -> Unit
                        }
                    }
                }
            }

            // ---- title, scrubber, controls ----
            if (panel == NowPlayingPanel.ART) {
                TitleRow(current, onClose)
            }
            state.error?.let { message ->
                Text(
                    message,
                    color = Color(0xFFFFB4A9),
                    style = MaterialTheme.typography.bodySmall,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 24.dp).clickable { engine.clearError(); engine.resume() },
                )
            }
            if (isEpisode) CurrentChapterLine(current, progress.positionSeconds)
            Scrubber(progress.positionSeconds, progress.durationSeconds.takeIf { it > 0 } ?: current.durationSeconds, progress.bufferedSeconds, state.speed)
            if (isEpisode) PodcastControls(state.playing, state.buffering, state.speed, onSpeed = { showSpeed = true })
            else MusicControls(state.playing, state.buffering, state.shuffle, state.repeat)

            // ---- the bottom row: panels and tools ----
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
                horizontalArrangement = Arrangement.SpaceEvenly,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (isEpisode) {
                    PanelButton(Icons.Default.Toc, "Chapters", panel == NowPlayingPanel.CHAPTERS) {
                        panel = if (panel == NowPlayingPanel.CHAPTERS) NowPlayingPanel.ART else NowPlayingPanel.CHAPTERS
                    }
                    PanelButton(Icons.Default.Subject, "Transcript", panel == NowPlayingPanel.TRANSCRIPT) {
                        panel = if (panel == NowPlayingPanel.TRANSCRIPT) NowPlayingPanel.ART else NowPlayingPanel.TRANSCRIPT
                    }
                    PanelButton(Icons.Default.BookmarkAdd, "Bookmark", false) { showBookmark = true }
                } else {
                    PanelButton(Icons.Default.Lyrics, "Lyrics", panel == NowPlayingPanel.LYRICS) {
                        panel = if (panel == NowPlayingPanel.LYRICS) NowPlayingPanel.ART else NowPlayingPanel.LYRICS
                    }
                    PanelButton(Icons.Default.Speed, AudioFormat.speed(state.speed.toDouble()), state.speed != 1f) { showSpeed = true }
                }
                PanelButton(Icons.Default.Bedtime, sleepLabel(state.sleep), state.sleep != null) { showSleep = true }
                PanelButton(Icons.Default.Cast, "Play on…", false) { ui.speakerPicker = true }
                PanelButton(Icons.AutoMirrored.Filled.QueueMusic, "Queue", panel == NowPlayingPanel.QUEUE) {
                    panel = if (panel == NowPlayingPanel.QUEUE) NowPlayingPanel.ART else NowPlayingPanel.QUEUE
                }
            }
        }
    }

    if (showSleep) SleepTimerSheet(onDismiss = { showSleep = false })
    if (showSpeed) SpeedSheet(isEpisode = isEpisode, showName = current.subtitle, onDismiss = { showSpeed = false })
    if (showBookmark) current.episode?.let { episode ->
        BookmarkDialog(episode.id, engine.currentPositionSeconds(), onDismiss = { showBookmark = false })
    }
}

private fun sleepLabel(timer: com.sentinelcore.android.audio.SleepTimer?): String = when {
    timer == null -> "Sleep"
    timer.endOfItem -> "End of item"
    else -> {
        val seconds = timer.remainingSeconds() ?: 0
        if (seconds >= 60) "${seconds / 60} min" else "${seconds}s"
    }
}

@Composable
private fun PanelButton(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, selected: Boolean, onClick: () -> Unit) {
    Column(
        Modifier.clip(RoundedCornerShape(10.dp)).clickable(onClick = onClick).padding(horizontal = 6.dp, vertical = 4.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(icon, contentDescription = label, tint = if (selected) MaterialTheme.colorScheme.primary else Color.White.copy(alpha = 0.85f))
        Text(label, style = MaterialTheme.typography.labelSmall, color = if (selected) MaterialTheme.colorScheme.primary else Color.White.copy(alpha = 0.7f), maxLines = 1)
    }
}

@Composable
private fun CompactHeader(item: AudioItem, playing: Boolean, onTap: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onTap).padding(horizontal = 20.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        ArtImage(item.artUrl, Modifier.size(52.dp), sizePx = 160, shape = RoundedCornerShape(6.dp), seed = item.album ?: item.title)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(item.title, color = Color.White, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(item.subtitle, color = Color.White.copy(alpha = 0.7f), style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        PlayingBars(playing = playing, color = Color.White)
    }
}

@Composable
private fun TitleRow(item: AudioItem, onClose: () -> Unit) {
    val ui = LocalAudioUi.current
    val track = item.track
    Row(Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(item.title, color = Color.White, style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Text(
                item.subtitle,
                color = Color.White.copy(alpha = 0.75f),
                style = MaterialTheme.typography.titleMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.clickable {
                    val artistId = track?.artistId
                    val podcastId = item.episode?.podcastId
                    when {
                        artistId != null -> { onClose(); ui.nav.openArtist(artistId) }
                        !podcastId.isNullOrBlank() -> { onClose(); ui.nav.openShow(podcastId) }
                    }
                },
            )
            track?.let { AudioFormat.trackQuality(it) }?.let {
                Text(it, color = Color.White.copy(alpha = 0.45f), style = MaterialTheme.typography.labelSmall)
            }
        }
        if (track != null) {
            IconButton(onClick = { ui.loveTrack(track) }) {
                Icon(
                    if (track.loved) Icons.Default.Favorite else Icons.Default.FavoriteBorder,
                    contentDescription = if (track.loved) "Unlove" else "Love",
                    tint = if (track.loved) Color(0xFFFF5C7C) else Color.White,
                )
            }
        }
    }
}

@Composable
private fun CurrentChapterLine(item: AudioItem, position: Double) {
    val ui = LocalAudioUi.current
    val episode = item.episode ?: return
    if (!episode.hasChapters) return
    val chapters = rememberLoadable(episode.id) { ui.api.chapters(episode.id) }
    val list = chapters.value.orEmpty()
    val active = TimedText.activeChapter(list, position)
    if (active < 0) return
    Text(
        "Chapter ${active + 1} of ${list.size}: ${list[active].title}",
        color = Color.White.copy(alpha = 0.7f),
        style = MaterialTheme.typography.labelMedium,
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        modifier = Modifier.fillMaxWidth().padding(horizontal = 24.dp),
        textAlign = TextAlign.Center,
    )
}

@Composable
private fun Scrubber(position: Double, duration: Double, buffered: Double, speed: Float) {
    val ui = LocalAudioUi.current
    var dragging by remember { mutableStateOf<Float?>(null) }
    val shown = dragging?.toDouble() ?: position
    Column(Modifier.fillMaxWidth().padding(horizontal = 20.dp)) {
        Slider(
            value = shown.toFloat().coerceIn(0f, duration.toFloat().coerceAtLeast(0.01f)),
            onValueChange = { dragging = it },
            onValueChangeFinished = {
                dragging?.let { ui.engine.seekTo(it.toDouble()) }
                dragging = null
            },
            valueRange = 0f..duration.toFloat().coerceAtLeast(0.01f),
            colors = SliderDefaults.colors(thumbColor = Color.White, activeTrackColor = Color.White, inactiveTrackColor = Color.White.copy(alpha = 0.25f)),
        )
        Row(Modifier.fillMaxWidth()) {
            Text(AudioFormat.clock(shown), color = Color.White.copy(alpha = 0.7f), style = MaterialTheme.typography.labelSmall)
            Spacer(Modifier.weight(1f))
            // Time left at this speed, the way people read it.
            val left = (duration - shown).coerceAtLeast(0.0) / speed.coerceAtLeast(0.5f)
            Text("-" + AudioFormat.clock(left), color = Color.White.copy(alpha = 0.7f), style = MaterialTheme.typography.labelSmall)
        }
    }
}

@Composable
private fun BigPlayButton(playing: Boolean, buffering: Boolean) {
    val ui = LocalAudioUi.current
    FilledIconButton(
        onClick = { ui.engine.togglePlay() },
        modifier = Modifier.size(72.dp),
        colors = IconButtonDefaults.filledIconButtonColors(containerColor = Color.White, contentColor = Color.Black),
    ) {
        if (buffering && !playing) CircularProgressIndicator(Modifier.size(30.dp), color = Color.Black, strokeWidth = 3.dp)
        else Icon(if (playing) Icons.Default.Pause else Icons.Default.PlayArrow, contentDescription = if (playing) "Pause" else "Play", modifier = Modifier.size(40.dp))
    }
}

@Composable
private fun MusicControls(playing: Boolean, buffering: Boolean, shuffle: Boolean, repeat: String) {
    val ui = LocalAudioUi.current
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = { ui.engine.setShuffle(!shuffle) }) {
            Icon(Icons.Default.Shuffle, contentDescription = if (shuffle) "Shuffle on" else "Shuffle off", tint = if (shuffle) MaterialTheme.colorScheme.primary else Color.White.copy(alpha = 0.7f))
        }
        IconButton(onClick = { ui.engine.previous() }, modifier = Modifier.size(56.dp)) {
            Icon(Icons.Default.SkipPrevious, contentDescription = "Previous", tint = Color.White, modifier = Modifier.size(40.dp))
        }
        BigPlayButton(playing, buffering)
        IconButton(onClick = { ui.engine.next() }, modifier = Modifier.size(56.dp)) {
            Icon(Icons.Default.SkipNext, contentDescription = "Next", tint = Color.White, modifier = Modifier.size(40.dp))
        }
        IconButton(onClick = { ui.engine.cycleRepeat() }) {
            Icon(
                if (repeat == "one") Icons.Default.RepeatOne else Icons.Default.Repeat,
                contentDescription = "Repeat $repeat",
                tint = if (repeat != "off") MaterialTheme.colorScheme.primary else Color.White.copy(alpha = 0.7f),
            )
        }
    }
}

@Composable
private fun PodcastControls(playing: Boolean, buffering: Boolean, speed: Float, onSpeed: () -> Unit) {
    val ui = LocalAudioUi.current
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
        TextButton(onClick = onSpeed) {
            Text(AudioFormat.speed(speed.toDouble()), color = if (speed != 1f) MaterialTheme.colorScheme.primary else Color.White, fontWeight = FontWeight.Bold)
        }
        IconButton(onClick = { ui.engine.seekBy(-15.0) }, modifier = Modifier.size(56.dp)) {
            Back15Icon(Color.White, 40)
        }
        BigPlayButton(playing, buffering)
        IconButton(onClick = { ui.engine.seekBy(30.0) }, modifier = Modifier.size(56.dp)) {
            Icon(Icons.Default.Forward30, contentDescription = "Forward 30 seconds", tint = Color.White, modifier = Modifier.size(40.dp))
        }
        IconButton(onClick = { ui.engine.next() }) {
            Icon(Icons.Default.SkipNext, contentDescription = "Next in queue", tint = Color.White.copy(alpha = 0.8f))
        }
    }
}

@Composable
private fun NowPlayingMenu(item: AudioItem, expanded: Boolean, onDismiss: () -> Unit, onClose: () -> Unit) {
    val ui = LocalAudioUi.current
    val track = item.track
    val episode = item.episode
    DropdownMenu(expanded = expanded, onDismissRequest = onDismiss) {
        if (track != null) {
            track.albumId?.let { id -> DropdownMenuItem(text = { Text("Go to album") }, onClick = { onDismiss(); onClose(); ui.nav.openAlbum(id) }) }
            track.artistId?.let { id -> DropdownMenuItem(text = { Text("Go to artist") }, onClick = { onDismiss(); onClose(); ui.nav.openArtist(id) }) }
            DropdownMenuItem(text = { Text("Add to playlist…") }, onClick = { onDismiss(); ui.addToPlaylist = listOf(track.id) })
            DropdownMenuItem(text = { Text("Start radio") }, onClick = { onDismiss(); ui.engine.startRadio("track", track.id, track.title) })
            DropdownMenuItem(text = { Text("Rate…") }, onClick = { onDismiss(); ui.trackMenu = TrackMenuTarget(track) })
        }
        if (episode != null) {
            DropdownMenuItem(text = { Text("Episode details") }, onClick = { onDismiss(); onClose(); ui.nav.openEpisode(episode.id) })
            if (episode.podcastId.isNotBlank()) DropdownMenuItem(text = { Text("Go to show") }, onClick = { onDismiss(); onClose(); ui.nav.openShow(episode.podcastId) })
            DropdownMenuItem(text = { Text("Make a clip…") }, onClick = { onDismiss(); onClose(); ui.nav.open("podcasts/clip/${episode.id}") })
            DropdownMenuItem(text = { Text("Mark played") }, onClick = {
                onDismiss()
                ui.launch { ui.api.setEpisodePlayed(episode.id, true); ui.libraryVersion++ }
                ui.engine.next()
            })
        }
        val downloaded = ui.runtime.downloads.isDownloaded(item.type, item.id)
        if (!downloaded) {
            DropdownMenuItem(text = { Text("Download") }, onClick = {
                onDismiss()
                if (track != null) ui.download(listOf(track)) else episode?.let { ui.runtime.downloads.downloadEpisode(it); ui.message("Downloading \"${it.title}\"") }
            })
        }
        DropdownMenuItem(text = { Text("Equalizer") }, onClick = { onDismiss(); onClose(); ui.nav.open(AudioRoutes.EQUALIZER) })
        DropdownMenuItem(text = { Text("Stop and clear the queue") }, onClick = { onDismiss(); ui.engine.stop(); onClose() })
    }
}

// ---------------- panels ----------------

@Composable
private fun LyricsPanel(item: AudioItem, position: Double) {
    val ui = LocalAudioUi.current
    val lyrics = rememberLoadable(item.id) { ui.api.lyrics(item.id) ?: Lyrics(false, emptyList(), null, null) }
    val value = lyrics.value
    when {
        value == null && lyrics.error == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Color.White) }
        value == null -> PanelMessage(lyrics.error ?: "Couldn't load the lyrics.")
        value.lines.isEmpty() && value.plain.isNullOrBlank() -> PanelMessage("No lyrics for this song.")
        value.synced -> SyncedLyrics(value, position)
        else -> LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(24.dp)) {
            val lines = value.plain?.lines() ?: value.lines.map { it.text }
            itemsIndexed(lines) { _, line ->
                Text(line, color = Color.White, style = MaterialTheme.typography.titleLarge, modifier = Modifier.padding(vertical = 4.dp))
            }
            item { LyricsSource(value.source) }
        }
    }
}

@Composable
private fun SyncedLyrics(lyrics: Lyrics, position: Double) {
    val ui = LocalAudioUi.current
    val listState = rememberLazyListState()
    val active = TimedText.activeLyric(lyrics.lines, position)
    // Follows the song, unless the person has just scrolled to read ahead: then it waits a few
    // seconds before taking over again.
    var lastUserScroll by remember { mutableStateOf(0L) }
    LaunchedEffect(listState) {
        snapshotFlow { listState.isScrollInProgress }.distinctUntilChanged().collect { scrolling ->
            if (scrolling && listState.interactionSource.let { true }) lastUserScroll = System.currentTimeMillis()
        }
    }
    LaunchedEffect(active) {
        if (active >= 0 && System.currentTimeMillis() - lastUserScroll > 4_000) {
            listState.animateScrollToItem((active - 2).coerceAtLeast(0))
        }
    }
    LazyColumn(Modifier.fillMaxSize(), state = listState, contentPadding = PaddingValues(start = 24.dp, end = 24.dp, top = 16.dp, bottom = 160.dp)) {
        itemsIndexed(lyrics.lines) { index, line ->
            val isActive = index == active
            val alpha by animateFloatAsState(if (isActive) 1f else if (index < active) 0.4f else 0.6f, label = "lyricAlpha")
            Text(
                line.text.ifBlank { "♪" },
                color = Color.White,
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = if (isActive) FontWeight.ExtraBold else FontWeight.SemiBold,
                modifier = Modifier
                    .fillMaxWidth()
                    .alpha(alpha)
                    .clip(RoundedCornerShape(8.dp))
                    .clickable(enabled = line.time != null) {
                        line.time?.let { ui.engine.seekTo(it) }
                        lastUserScroll = 0L
                    }
                    .padding(vertical = 8.dp),
            )
        }
        item { LyricsSource(lyrics.source) }
    }
}

@Composable
private fun LyricsSource(source: String?) {
    val label = when (source) {
        "lrclib" -> "Lyrics from LRCLIB"
        "sidecar" -> "Lyrics from the .lrc file"
        "embedded" -> "Lyrics from the file's tags"
        else -> null
    } ?: return
    Text(label, color = Color.White.copy(alpha = 0.4f), style = MaterialTheme.typography.labelSmall, modifier = Modifier.padding(top = 24.dp))
}

@Composable
private fun PanelMessage(text: String) {
    Box(Modifier.fillMaxSize().padding(32.dp), contentAlignment = Alignment.Center) {
        Text(text, color = Color.White.copy(alpha = 0.7f), textAlign = TextAlign.Center)
    }
}

@Composable
private fun ChaptersPanel(item: AudioItem, position: Double) {
    val ui = LocalAudioUi.current
    val chapters = rememberLoadable(item.id) { ui.api.chapters(item.id) }
    val list = chapters.value
    when {
        list == null && chapters.error == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Color.White) }
        list.isNullOrEmpty() -> PanelMessage(chapters.error ?: "This episode has no chapters.")
        else -> {
            val active = TimedText.activeChapter(list, position)
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(vertical = 8.dp)) {
                itemsIndexed(list) { index, chapter ->
                    ChapterRow(index, chapter, index == active) { ui.engine.seekTo(chapter.startSeconds) }
                }
            }
        }
    }
}

@Composable
private fun ChapterRow(index: Int, chapter: Chapter, active: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick)
            .background(if (active) Color.White.copy(alpha = 0.12f) else Color.Transparent)
            .padding(horizontal = 20.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (chapter.imageUrl != null) {
            ArtImage(chapter.imageUrl, Modifier.size(40.dp), sizePx = 120, shape = RoundedCornerShape(4.dp))
            Spacer(Modifier.width(12.dp))
        } else {
            Text("${index + 1}", color = Color.White.copy(alpha = 0.5f), modifier = Modifier.width(28.dp))
        }
        Text(chapter.title, color = Color.White, fontWeight = if (active) FontWeight.Bold else FontWeight.Normal, modifier = Modifier.weight(1f), maxLines = 2, overflow = TextOverflow.Ellipsis)
        Text(AudioFormat.clock(chapter.startSeconds), color = Color.White.copy(alpha = 0.6f), style = MaterialTheme.typography.labelMedium)
    }
}

@Composable
private fun TranscriptPanel(item: AudioItem, position: Double) {
    val ui = LocalAudioUi.current
    val transcript = rememberLoadable(item.id) { ui.api.transcript(item.id) ?: Transcript(null, null, emptyList()) }
    val value = transcript.value
    when {
        value == null && transcript.error == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Color.White) }
        value == null -> PanelMessage(transcript.error ?: "Couldn't load the transcript.")
        value.segments.isEmpty() -> Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
            Text("No transcript for this episode yet.", color = Color.White.copy(alpha = 0.7f))
            TextButton(onClick = {
                ui.launch("Transcripts aren't set up on the server.") {
                    ui.api.transcribe(item.id)
                    ui.message("Transcribing on the server. It'll be here when it's done.")
                }
            }) { Text("Transcribe it", color = MaterialTheme.colorScheme.primary) }
        }
        else -> TranscriptList(value, position)
    }
}

@Composable
private fun TranscriptList(transcript: Transcript, position: Double) {
    val ui = LocalAudioUi.current
    var query by remember { mutableStateOf("") }
    var follow by remember { mutableStateOf(true) }
    val listState = rememberLazyListState()
    val active = TimedText.activeSegment(transcript.segments, position)
    val matches = remember(query, transcript) { TimedText.search(transcript.segments, query).toSet() }
    LaunchedEffect(active, follow, query) {
        if (follow && query.isBlank() && active >= 0) listState.animateScrollToItem((active - 1).coerceAtLeast(0))
    }
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                value = query,
                onValueChange = { query = it },
                placeholder = { Text("Search this episode") },
                leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
                trailingIcon = { if (query.isNotEmpty()) IconButton(onClick = { query = "" }) { Icon(Icons.Default.Close, contentDescription = "Clear") } },
                singleLine = true,
                keyboardOptions = KeyboardOptions.Default,
                modifier = Modifier.weight(1f),
            )
            TextButton(onClick = { follow = !follow }) { Text(if (follow) "Following" else "Follow", color = if (follow) MaterialTheme.colorScheme.primary else Color.White) }
        }
        if (query.isNotBlank()) {
            Text(
                if (matches.isEmpty()) "No matches" else "${matches.size} matches - tap one to jump there",
                color = Color.White.copy(alpha = 0.6f),
                style = MaterialTheme.typography.labelSmall,
                modifier = Modifier.padding(horizontal = 20.dp, vertical = 4.dp),
            )
        }
        LazyColumn(Modifier.fillMaxSize(), state = listState, contentPadding = PaddingValues(start = 20.dp, end = 20.dp, bottom = 120.dp)) {
            val shown = if (query.isBlank()) transcript.segments.indices.toList() else matches.sorted()
            itemsIndexed(shown, key = { _, index -> index }) { _, index ->
                val segment = transcript.segments[index]
                val isActive = index == active
                val previousSpeaker = transcript.segments.getOrNull(index - 1)?.speaker
                Column(
                    Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp))
                        .background(if (isActive) Color.White.copy(alpha = 0.12f) else Color.Transparent)
                        .clickable { ui.engine.seekTo(segment.start); follow = true; query = "" }
                        .padding(horizontal = 8.dp, vertical = 6.dp),
                ) {
                    if (segment.speaker != null && (segment.speaker != previousSpeaker || query.isNotBlank())) {
                        Text(segment.speaker, color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.labelMedium)
                    }
                    Row {
                        Text(
                            AudioFormat.clock(segment.start),
                            color = Color.White.copy(alpha = 0.45f),
                            style = MaterialTheme.typography.labelSmall,
                            modifier = Modifier.width(48.dp).padding(top = 3.dp),
                        )
                        Text(
                            segment.text,
                            color = Color.White.copy(alpha = if (isActive || query.isNotBlank()) 1f else 0.75f),
                            style = MaterialTheme.typography.bodyLarge,
                            fontWeight = if (isActive) FontWeight.SemiBold else FontWeight.Normal,
                        )
                    }
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun QueuePanel() {
    val ui = LocalAudioUi.current
    val engine = ui.engine
    val state by engine.state.collectAsState()
    val listState = rememberLazyListState()
    // A local copy while a row is being dragged; committed to the player on drop.
    var order by remember(state.queue) { mutableStateOf(state.queue) }
    val reorder = rememberListReorderState(
        listState,
        onMove = { from, to ->
            val fromIndex = order.indexOfFirst { it.uid == from }
            val toIndex = order.indexOfFirst { it.uid == to }
            if (fromIndex >= 0 && toIndex >= 0) order = order.toMutableList().apply { add(toIndex, removeAt(fromIndex)) }
        },
        onDrop = { key ->
            val from = state.queue.indexOfFirst { it.uid == key }
            val to = order.indexOfFirst { it.uid == key }
            if (from >= 0 && to >= 0 && from != to) engine.moveInQueue(from, to)
        },
    )
    val currentIndex = state.index
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Up Next", color = Color.White, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
            if (state.radioSeed != null) Text("Radio keeps going", color = Color.White.copy(alpha = 0.6f), style = MaterialTheme.typography.labelSmall)
            if (state.upNext.isNotEmpty()) TextButton(onClick = { engine.clearUpNext() }) { Text("Clear", color = MaterialTheme.colorScheme.primary) }
        }
        HorizontalDivider(color = Color.White.copy(alpha = 0.1f))
        if (state.upNext.isEmpty()) {
            PanelMessage("Nothing after this. Add songs with Play Next or Add to Queue.")
            return
        }
        LazyColumn(Modifier.fillMaxSize(), state = listState, contentPadding = PaddingValues(bottom = 24.dp)) {
            itemsIndexed(order, key = { _, item -> item.uid }, contentType = { index, _ -> if (index <= currentIndex) "past" else "next" }) { index, item ->
                if (index < currentIndex) return@itemsIndexed
                if (index == currentIndex) {
                    Text(
                        "Now: ${item.title}",
                        color = Color.White.copy(alpha = 0.5f),
                        style = MaterialTheme.typography.labelMedium,
                        modifier = Modifier.padding(horizontal = 20.dp, vertical = 6.dp),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    return@itemsIndexed
                }
                val dismiss = rememberSwipeToDismissBoxState(confirmValueChange = { value ->
                    if (value == SwipeToDismissBoxValue.EndToStart) {
                        val at = state.queue.indexOfFirst { it.uid == item.uid }
                        if (at >= 0) engine.removeFromQueue(at)
                        true
                    } else false
                })
                SwipeToDismissBox(
                    state = dismiss,
                    enableDismissFromStartToEnd = false,
                    modifier = Modifier.reorderableRow(reorder, item.uid),
                    backgroundContent = {
                        // The row on top is transparent (it sits on the blurred art), so the label
                        // only exists mid-swipe - drawn always, it showed through every row.
                        val swiping = dismiss.dismissDirection == SwipeToDismissBoxValue.EndToStart
                        Box(Modifier.fillMaxSize().background(if (swiping) Color(0xFFB3261E) else Color.Transparent).padding(horizontal = 24.dp), contentAlignment = Alignment.CenterEnd) {
                            if (swiping) Text("Remove", color = Color.White)
                        }
                    },
                ) {
                    Row(
                        Modifier.fillMaxWidth().background(if (dismiss.dismissDirection == SwipeToDismissBoxValue.EndToStart) Color(0xFF15191C) else Color.Transparent)
                            .clickable { engine.skipTo(state.queue.indexOfFirst { it.uid == item.uid }) }
                            .padding(start = 20.dp, end = 4.dp, top = 6.dp, bottom = 6.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        ArtImage(item.artUrl, Modifier.size(44.dp), sizePx = 132, shape = RoundedCornerShape(5.dp), seed = item.album ?: item.title)
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f)) {
                            Text(item.title, color = Color.White, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(item.subtitle, color = Color.White.copy(alpha = 0.6f), style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                        Icon(
                            Icons.Default.DragHandle,
                            contentDescription = "Drag to reorder",
                            tint = Color.White.copy(alpha = 0.6f),
                            modifier = Modifier.reorderHandle(reorder, item.uid).padding(14.dp),
                        )
                    }
                }
            }
        }
    }
}

// Material has Replay 5/10/30 but no 15: the round arrow with the number drawn in it.
@Composable
fun Back15Icon(tint: Color, sizeDp: Int) {
    Box(contentAlignment = Alignment.Center) {
        Icon(Icons.Default.Replay, contentDescription = "Back 15 seconds", tint = tint, modifier = Modifier.size(sizeDp.dp))
        Text("15", color = tint, fontSize = (sizeDp / 4).sp, fontWeight = FontWeight.Bold, modifier = Modifier.padding(top = (sizeDp / 12).dp))
    }
}

// ---------------- mini player ----------------

@Composable
fun MiniPlayer(modifier: Modifier = Modifier) {
    val ui = LocalAudioUi.current
    val state by ui.engine.state.collectAsState()
    val progress by ui.engine.progress.collectAsState()
    val current = state.current ?: return
    val fraction = if (progress.durationSeconds > 0) (progress.positionSeconds / progress.durationSeconds).toFloat().coerceIn(0f, 1f) else 0f
    Column(
        modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp)
            .clip(RoundedCornerShape(12.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant)
            .clickable { ui.nowPlayingOpen = true },
    ) {
        Row(Modifier.fillMaxWidth().padding(start = 8.dp, end = 2.dp, top = 6.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            ArtImage(current.artUrl, Modifier.size(42.dp), sizePx = 128, shape = RoundedCornerShape(6.dp), seed = current.album ?: current.title)
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text(current.title, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    state.error ?: current.subtitle,
                    style = MaterialTheme.typography.bodySmall,
                    color = if (state.error != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            if (current.isEpisode) {
                IconButton(onClick = { ui.engine.seekBy(-15.0) }) { Back15Icon(MaterialTheme.colorScheme.onSurface, 24) }
            }
            IconButton(onClick = { ui.engine.togglePlay() }) {
                if (state.buffering && !state.playing) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                else Icon(if (state.playing) Icons.Default.Pause else Icons.Default.PlayArrow, contentDescription = if (state.playing) "Pause" else "Play")
            }
            IconButton(onClick = { if (current.isEpisode) ui.engine.seekBy(30.0) else ui.engine.next() }) {
                Icon(if (current.isEpisode) Icons.Default.Forward30 else Icons.Default.SkipNext, contentDescription = if (current.isEpisode) "Forward 30 seconds" else "Next")
            }
        }
        Box(Modifier.fillMaxWidth().height(2.dp).background(MaterialTheme.colorScheme.onSurface.copy(alpha = 0.08f))) {
            Box(Modifier.fillMaxWidth(fraction).height(2.dp).background(MaterialTheme.colorScheme.primary))
        }
    }
}

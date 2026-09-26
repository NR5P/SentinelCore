package com.sentinelcore.android.ui.screens

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.net.Uri
import android.os.SystemClock
import android.view.View
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView
import com.sentinelcore.android.data.MediaPlayback
import com.sentinelcore.android.data.MediaPlaybackMath
import com.sentinelcore.android.ui.SentinelViewModel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

// One playback of one movie or episode. A ViewModel on the player's back-stack entry, so the
// ExoPlayer survives a rotation (this Activity is recreated on one) and keeps playing instead of
// asking the server for a fresh session.
//
// Two clocks: the player's own, and the movie's. For a direct file they are the same. For HLS the
// playlist starts at playback.startSeconds, so the movie's time is startSeconds + the player's -
// see MediaPlaybackMath.mediaPositionSeconds and MOVIES.md.
@androidx.annotation.OptIn(UnstableApi::class)
internal class MoviePlayerSession(
    private val app: SentinelViewModel,
    private val context: Context,
    val itemId: String,
    initialStart: Double?,
) : ViewModel() {
    var playback by mutableStateOf<MediaPlayback?>(null)
        private set
    var player by mutableStateOf<ExoPlayer?>(null)
        private set
    var loading by mutableStateOf(true)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    var positionSeconds by mutableDoubleStateOf(0.0)
        private set

    // Set when the item ends: the next episode to hand over to, or finished with nothing next.
    var nextToPlay by mutableStateOf<String?>(null)
        private set
    var finished by mutableStateOf(false)
        private set

    private var lastReportAt = 0L
    private var lastRecoveryAt = 0L
    private var endReported = false

    private val listener = object : Player.Listener {
        override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) {
            if (!playWhenReady) report()
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            if (playbackState == Player.STATE_ENDED) onEnded()
        }

        override fun onPlayerError(playbackError: PlaybackException) {
            recover(playbackError)
        }
    }

    init {
        load(initialStart)
        viewModelScope.launch {
            while (isActive) {
                delay(1_000)
                val exo = player ?: continue
                positionSeconds = currentMediaSeconds() ?: continue
                if (exo.isPlaying && SystemClock.elapsedRealtime() - lastReportAt >= PROGRESS_INTERVAL_MS) report()
            }
        }
    }

    // Asks the server for a stream starting at `start` (null = the saved resume point).
    fun load(start: Double?) {
        viewModelScope.launch {
            loading = true
            error = null
            runCatching { app.mediaPlayback(itemId, start) }
                .onSuccess(::begin)
                .onFailure { error = it.message ?: "Could not start playback." }
            loading = false
        }
    }

    // "Start over" in HLS mode, and Retry: a fresh session from a new point.
    fun restart(start: Double?) {
        report()
        player?.stop()
        load(start)
    }

    fun pause() {
        player?.pause()
    }

    private fun begin(value: MediaPlayback) {
        val url = app.absoluteUrl(value.url)
        if (url == null) {
            error = "No server address."
            return
        }
        val exo = player ?: buildPlayer().also { player = it }
        playback = value
        nextToPlay = null
        finished = false
        endReported = false
        positionSeconds = value.startSeconds

        val builder = MediaItem.Builder().setUri(url).setMediaId(value.itemId)
        if (value.isHls) {
            builder.setMimeType(MimeTypes.APPLICATION_M3U8)
            // The playlist is an EVENT playlist that grows while ffmpeg works, which Media3 treats
            // as live - and would otherwise nudge the playback speed to chase the live edge.
            builder.setLiveConfiguration(
                MediaItem.LiveConfiguration.Builder().setMinPlaybackSpeed(1f).setMaxPlaybackSpeed(1f).build(),
            )
        } else {
            value.mimeType?.let(builder::setMimeType)
            // A direct file carries no subtitle renditions of its own; the server's WebVTT does.
            // (HLS sessions list them in the master playlist instead.)
            builder.setSubtitleConfigurations(
                value.subtitles.mapNotNull { subtitle ->
                    val subtitleUrl = app.absoluteUrl(subtitle.url) ?: return@mapNotNull null
                    MediaItem.SubtitleConfiguration.Builder(Uri.parse(subtitleUrl))
                        .setId("sub-${subtitle.index}")
                        .setMimeType(MimeTypes.TEXT_VTT)
                        .setLanguage(subtitle.language)
                        .setLabel(subtitle.label ?: subtitle.language ?: "Subtitles ${subtitle.index + 1}")
                        .setSelectionFlags(if (subtitle.forced) C.SELECTION_FLAG_FORCED else 0)
                        .build()
                },
            )
        }
        // Explicit, even when 0: without a start position an HLS "live" playlist would begin at
        // its live edge rather than at startSeconds.
        exo.setMediaItem(builder.build(), MediaPlaybackMath.initialSeekMillis(value.mode, value.startSeconds))
        exo.prepare()
        exo.playWhenReady = true
        lastReportAt = SystemClock.elapsedRealtime()
    }

    private fun buildPlayer(): ExoPlayer {
        val httpFactory = DefaultHttpDataSource.Factory()
            .setAllowCrossProtocolRedirects(true)
            .setDefaultRequestProperties(app.mediaHeaders())
        return ExoPlayer.Builder(context)
            .setMediaSourceFactory(DefaultMediaSourceFactory(httpFactory))
            .build()
            .also { it.addListener(listener) }
    }

    private fun currentMediaSeconds(): Double? {
        val exo = player ?: return null
        val current = playback ?: return null
        return MediaPlaybackMath.mediaPositionSeconds(current.mode, current.startSeconds, exo.currentPosition)
    }

    private fun durationSeconds(): Double? {
        playback?.durationSeconds?.takeIf { it > 0 }?.let { return it }
        val exo = player ?: return null
        // Only a direct file's player duration is the movie's; an HLS window grows as it's made.
        return if (playback?.isHls == false && exo.duration != C.TIME_UNSET) exo.duration / 1000.0 else null
    }

    private fun report(position: Double? = currentMediaSeconds()) {
        // Once the end has been reported, the pause and close that follow have nothing to add.
        if (endReported) return
        val seconds = position ?: return
        val duration = durationSeconds()
        lastReportAt = SystemClock.elapsedRealtime()
        if (!MediaPlaybackMath.worthReporting(seconds, duration)) return
        app.reportMediaProgressInBackground(itemId, seconds, duration)
    }

    private fun onEnded() {
        report(durationSeconds() ?: currentMediaSeconds())
        endReported = true
        val next = playback?.nextEpisodeId
        if (next != null) nextToPlay = next else finished = true
    }

    // An HLS session dies three minutes after its last segment request - a long pause is enough.
    // The first failure in a while quietly asks for a new session from where the movie was;
    // anything else is shown with Retry.
    private fun recover(playbackError: PlaybackException) {
        val current = playback
        val now = SystemClock.elapsedRealtime()
        if (current?.isHls == true && now - lastRecoveryAt > 30_000) {
            lastRecoveryAt = now
            val resumeAt = positionSeconds
            player?.stop()
            load(resumeAt)
            return
        }
        report()
        error = "Playback stopped: ${playbackError.errorCodeName.removePrefix("ERROR_CODE_").lowercase().replace('_', ' ')}."
    }

    override fun onCleared() {
        report()
        player?.removeListener(listener)
        player?.release()
        player = null
    }

    private companion object {
        const val PROGRESS_INTERVAL_MS = 10_000L
    }
}

@androidx.annotation.OptIn(UnstableApi::class)
@Composable
fun MoviePlayerScreen(
    model: SentinelViewModel,
    itemId: String,
    start: Double?,
    onBack: () -> Unit,
    onPlayNext: (String) -> Unit,
) {
    val appContext = LocalContext.current.applicationContext
    val session: MoviePlayerSession = viewModel(key = "movie-player-$itemId") {
        MoviePlayerSession(model, appContext, itemId, start)
    }
    var controlsVisible by remember { mutableStateOf(true) }

    // The screen stays on, and the status and navigation bars get out of the way, for as long as
    // the player is up.
    val view = LocalView.current
    val context = LocalContext.current
    DisposableEffect(view) {
        view.keepScreenOn = true
        val window = context.findActivity()?.window
        val controller = window?.let { WindowCompat.getInsetsController(it, view) }
        controller?.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        controller?.hide(WindowInsetsCompat.Type.systemBars())
        onDispose {
            view.keepScreenOn = false
            controller?.show(WindowInsetsCompat.Type.systemBars())
        }
    }
    // Leaving the app pauses (and so saves the spot); it doesn't keep playing behind the home screen.
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event -> if (event == Lifecycle.Event.ON_STOP) session.pause() }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }
    LaunchedEffect(session.nextToPlay) { session.nextToPlay?.let(onPlayNext) }
    LaunchedEffect(session.finished) { if (session.finished) onBack() }

    val playback = session.playback
    val player = session.player
    val error = session.error

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        if (player != null) {
            AndroidView(
                factory = { ctx ->
                    PlayerView(ctx).apply {
                        useController = true
                        setShowSubtitleButton(true)
                        setShowNextButton(false)
                        setShowPreviousButton(false)
                        setControllerVisibilityListener(
                            PlayerView.ControllerVisibilityListener { visibility -> controlsVisible = visibility == View.VISIBLE },
                        )
                    }
                },
                update = { it.player = player },
                onRelease = { it.player = null },
                modifier = Modifier.fillMaxSize(),
            )
        }

        if (session.loading && error == null) {
            CircularProgressIndicator(Modifier.align(Alignment.Center), color = Color.White)
        }

        if (error != null) {
            Column(
                Modifier.align(Alignment.Center).padding(32.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                Text(error, color = Color.White, textAlign = TextAlign.Center, style = MaterialTheme.typography.bodyLarge)
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    OutlinedButton(onClick = onBack) { Text("Back", color = Color.White) }
                    // Picks up where it stopped - or from the saved point if it never started.
                    Button(onClick = { session.restart(if (playback != null) session.positionSeconds else start) }) { Text("Retry") }
                }
            }
        }

        if (controlsVisible || player == null || error != null) {
            TopBar(
                playback = playback,
                positionSeconds = session.positionSeconds,
                onBack = onBack,
                onStartOver = { session.restart(0.0) },
                modifier = Modifier.align(Alignment.TopStart),
            )
        }
    }
}

@Composable
private fun TopBar(
    playback: MediaPlayback?,
    positionSeconds: Double,
    onBack: () -> Unit,
    onStartOver: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier
            .fillMaxWidth()
            .background(Brush.verticalGradient(listOf(Color.Black.copy(alpha = 0.7f), Color.Transparent)))
            .safeDrawingPadding()
            .padding(horizontal = 4.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        IconButton(onClick = onBack) {
            Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back", tint = Color.White)
        }
        Column(Modifier.weight(1f)) {
            Text(
                playback?.title.orEmpty(),
                color = Color.White,
                style = MaterialTheme.typography.titleMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            val sub = listOfNotNull(
                playback?.subtitle,
                // In HLS mode the player's own time bar counts from startSeconds, not from the
                // start of the movie - this is the movie's real clock.
                playback?.takeIf { it.isHls }?.let { p ->
                    MediaPlaybackMath.formatClock(positionSeconds) +
                        (p.durationSeconds?.let { " / " + MediaPlaybackMath.formatClock(it) } ?: "")
                },
            ).joinToString(" · ")
            if (sub.isNotBlank()) {
                Text(sub, color = Color.White.copy(alpha = 0.8f), style = MaterialTheme.typography.bodySmall, maxLines = 1)
            }
        }
        // An HLS stream can only go back as far as where it began; earlier needs a new session.
        if (playback?.isHls == true && playback.startSeconds > 0) {
            TextButton(onClick = onStartOver) { Text("Start over", color = Color.White) }
        }
    }
}

private tailrec fun Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}

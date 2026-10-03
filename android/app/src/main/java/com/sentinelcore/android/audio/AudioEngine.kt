package com.sentinelcore.android.audio

import android.media.AudioManager
import android.net.Uri
import android.os.SystemClock
import android.util.Log
import androidx.annotation.OptIn
import androidx.core.content.ContextCompat
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.Timeline
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.datasource.ResolvingDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.time.Instant

data class SleepTimer(
    // elapsedRealtime when it stops; null for "end of this track/episode".
    val endsAtMillis: Long?,
    val endOfItem: Boolean,
) {
    fun remainingSeconds(now: Long = SystemClock.elapsedRealtime()): Long? = endsAtMillis?.let { ((it - now) / 1000).coerceAtLeast(0) }
}

data class PlayerState(
    val queue: List<AudioItem> = emptyList(),
    val index: Int = -1,
    val playing: Boolean = false,
    val buffering: Boolean = false,
    // Whether play was asked for (playing may still be false while buffering).
    val playWhenReady: Boolean = false,
    val shuffle: Boolean = false,
    // off, all or one.
    val repeat: String = "off",
    val speed: Float = 1f,
    val context: PlayContext? = null,
    val error: String? = null,
    val sleep: SleepTimer? = null,
    // The volume another device set while playing here as a speaker (0-1).
    val speakerVolume: Float = 1f,
    // Endless radio: the seed to ask for more of as the queue runs out.
    val radioSeed: Pair<String, String>? = null,
) {
    val current: AudioItem? get() = queue.getOrNull(index)
    val hasQueue: Boolean get() = queue.isNotEmpty() && index >= 0
    val upNext: List<AudioItem> get() = if (index < 0) queue else queue.drop(index + 1)
    val kind: String get() = if (current?.isEpisode == true) "podcast" else "music"
}

data class PlayerProgress(val positionSeconds: Double = 0.0, val durationSeconds: Double = 0.0, val bufferedSeconds: Double = 0.0)

// The shared player behind both tabs (MUSIC_AND_PODCASTS.md §11). Owns the ExoPlayer the media
// session wraps, keeps our queue (with full Track/Episode objects) beside the player's playlist,
// and does the reporting: `played` for tracks, `progress` for episodes, `now-playing` for
// "Continue from…". Everything here runs on the main thread, as ExoPlayer requires.
@OptIn(UnstableApi::class)
class AudioEngine(private val runtime: AudioRuntime) {
    private val context = runtime.context
    private val api = runtime.api
    private val scope = runtime.scope

    // Every queue entry by uid - the player's playlist holds only "q:<uid>" ids.
    private val items = mutableMapOf<Long, AudioItem>()
    private var originalOrder: List<Long> = emptyList()

    private val _state = MutableStateFlow(PlayerState())
    val state: StateFlow<PlayerState> = _state.asStateFlow()

    private val _progress = MutableStateFlow(PlayerProgress())
    val progress: StateFlow<PlayerProgress> = _progress.asStateFlow()

    val dataSourceFactory: DataSource.Factory = run {
        val http = DefaultHttpDataSource.Factory()
            .setAllowCrossProtocolRedirects(true)
            .setConnectTimeoutMs(15_000)
            .setReadTimeoutMs(30_000)
            .setUserAgent("SentinelCore-Android")
        ResolvingDataSource.Factory(DefaultDataSource.Factory(context, http)) { spec ->
            val uri = spec.uri.toString()
            when {
                uri.startsWith(SCHEME) -> {
                    val absolute = runtime.absoluteUrl(uri.removePrefix(SCHEME)) ?: uri
                    spec.withUri(Uri.parse(absolute)).withAdditionalHeaders(runtime.headers())
                }
                (uri.startsWith("http://") || uri.startsWith("https://")) && isServerUrl(uri) ->
                    spec.withAdditionalHeaders(runtime.headers())
                else -> spec
            }
        }
    }

    private val audioManager = ContextCompat.getSystemService(context, AudioManager::class.java)

    val player: ExoPlayer = ExoPlayer.Builder(context)
        .setMediaSourceFactory(DefaultMediaSourceFactory(dataSourceFactory))
        .setAudioAttributes(
            AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_MUSIC).build(),
            /* handleAudioFocus = */ true,
        )
        .setHandleAudioBecomingNoisy(true)
        .setWakeMode(C.WAKE_MODE_NETWORK)
        .setSeekBackIncrementMs(SKIP_BACK_MS)
        .setSeekForwardIncrementMs(SKIP_FORWARD_MS)
        .build()
        .also { exo ->
            // A fixed session id, so the Equalizer attaches once and stays attached.
            audioManager?.generateAudioSessionId()?.takeIf { it > 0 }?.let { exo.audioSessionId = it }
        }

    val effects = AudioEffects(runtime)

    // ---- what the reporting tracks about the entry playing now ----
    private class Tracked(val item: AudioItem, val startedAt: Instant) {
        var listenedSeconds = 0.0
        var lastPositionSeconds = 0.0
        var durationSeconds = item.durationSeconds
    }

    private var tracked: Tracked? = null
    private var lastTick = 0L
    private var lastProgressReport = 0L
    private var lastNowPlayingReport = 0L
    private var nowPlayingJob: Job? = null
    private var tickerJob: Job? = null

    // Volume parts, multiplied together: Sound Check, sleep fade, ducking, the alarm ramp.
    private var gainVolume = 1f
    private var fadeVolume = 1f
    private var duckVolume = 1f
    private var rampVolume = 1f
    private var duckJob: Job? = null
    private var rampJob: Job? = null

    // Per-show settings, fetched when one of its episodes starts.
    private val showSettings = mutableMapOf<String, PodcastMySettings>()
    private var radioLoading = false

    // Listeners outside the engine (the service's button layout, the speaker's state reports).
    val changes = MutableStateFlow(0L)

    init {
        player.addListener(object : Player.Listener {
            override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
                val completed = reason == Player.MEDIA_ITEM_TRANSITION_REASON_AUTO || reason == Player.MEDIA_ITEM_TRANSITION_REASON_REPEAT
                val skipped = reason == Player.MEDIA_ITEM_TRANSITION_REASON_SEEK
                if (reason != Player.MEDIA_ITEM_TRANSITION_REASON_PLAYLIST_CHANGED || tracked?.item?.uid != uidOf(mediaItem)) {
                    finishTracked(completed = completed, skipped = skipped)
                }
                syncQueue()
                startTracked()
                maybeExtendRadio()
                // "End of this episode" sleep timer: the player paused at the boundary.
                if (_state.value.sleep?.endOfItem == true && completed) stopForSleep()
            }

            override fun onTimelineChanged(timeline: Timeline, reason: Int) {
                syncQueue()
            }

            override fun onIsPlayingChanged(isPlaying: Boolean) {
                _state.update { it.copy(playing = isPlaying) }
                if (isPlaying) startTicker() else {
                    tick()
                    reportEpisodeProgress(force = true)
                }
                scheduleNowPlaying()
                bump()
            }

            override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) {
                _state.update { it.copy(playWhenReady = playWhenReady) }
                if (!playWhenReady && reason == Player.PLAY_WHEN_READY_CHANGE_REASON_END_OF_MEDIA_ITEM && _state.value.sleep?.endOfItem == true) {
                    stopForSleep()
                }
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                _state.update { it.copy(buffering = playbackState == Player.STATE_BUFFERING) }
                if (playbackState == Player.STATE_ENDED) {
                    finishTracked(completed = true, skipped = false)
                    updateProgress()
                }
                if (playbackState == Player.STATE_READY) {
                    _state.update { it.copy(error = null) }
                    updateProgress()
                }
            }

            override fun onPlayerError(error: PlaybackException) {
                Log.w(AudioApi.TAG, "Playback error: ${error.errorCodeName} ${error.message}")
                val message = when (error.errorCode) {
                    PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
                    PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT -> "Can't reach the server. Downloads still play offline."
                    PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS -> "The server couldn't send this one."
                    PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED,
                    PlaybackException.ERROR_CODE_DECODER_INIT_FAILED -> "This phone can't play that format."
                    else -> "Playback stopped: ${error.errorCodeName.removePrefix("ERROR_CODE_").lowercase().replace('_', ' ')}"
                }
                _state.update { it.copy(error = message) }
                bump()
            }

            override fun onPositionDiscontinuity(oldPosition: Player.PositionInfo, newPosition: Player.PositionInfo, reason: Int) {
                if (reason == Player.DISCONTINUITY_REASON_SEEK) {
                    updateProgress()
                    scheduleNowPlaying()
                    bump()
                }
            }

            override fun onRepeatModeChanged(repeatMode: Int) {
                _state.update { it.copy(repeat = repeatName(repeatMode)) }
                scheduleNowPlaying()
            }

            override fun onPlaybackParametersChanged(playbackParameters: androidx.media3.common.PlaybackParameters) {
                _state.update { it.copy(speed = playbackParameters.speed) }
            }
        })
        effects.attach(player.audioSessionId)
    }

    // ---------------- starting playback ----------------

    fun playTracks(tracks: List<Track>, startIndex: Int = 0, context: PlayContext? = null, shuffle: Boolean = false) {
        if (tracks.isEmpty()) return
        play(tracks.map(AudioItem::of), startIndex, 0.0, context, shuffle)
    }

    fun playEpisodes(episodes: List<Episode>, startIndex: Int = 0, context: PlayContext? = null, startAt: Double? = null) {
        if (episodes.isEmpty()) return
        val start = episodes.getOrNull(startIndex) ?: episodes.first()
        play(episodes.map(AudioItem::of), startIndex, startAt ?: start.resumeSeconds, context, shuffle = false)
    }

    fun play(
        queue: List<AudioItem>,
        startIndex: Int,
        startSeconds: Double,
        context: PlayContext?,
        shuffle: Boolean,
        radioSeed: Pair<String, String>? = null,
        autoplay: Boolean = true,
    ) {
        if (queue.isEmpty()) return
        runtime.ensureService()
        finishTracked(completed = false, skipped = true)
        items.clear()
        queue.forEach { items[it.uid] = it }
        originalOrder = queue.map { it.uid }
        val start = startIndex.coerceIn(0, queue.lastIndex)
        val ordered = if (shuffle) QueueOps.shuffled(queue, start) else queue
        val index = if (shuffle) 0 else start
        _state.update {
            it.copy(
                queue = ordered,
                index = index,
                shuffle = shuffle,
                context = context,
                error = null,
                radioSeed = radioSeed,
            )
        }
        player.setMediaItems(ordered.map(::mediaItem), index, (startSeconds * 1000).toLong().coerceAtLeast(0))
        player.prepare()
        player.playWhenReady = autoplay
        bump()
    }

    // Play next / Add to queue. With nothing playing, these just start it.
    fun playNext(newItems: List<AudioItem>) {
        if (newItems.isEmpty()) return
        if (!_state.value.hasQueue) return play(newItems, 0, 0.0, PlayContext("queue"), false)
        newItems.forEach { items[it.uid] = it }
        val at = (player.currentMediaItemIndex + 1).coerceAtMost(player.mediaItemCount)
        player.addMediaItems(at, newItems.map(::mediaItem))
        originalOrder = originalOrder + newItems.map { it.uid }
        syncQueue()
    }

    fun addToQueue(newItems: List<AudioItem>) {
        if (newItems.isEmpty()) return
        if (!_state.value.hasQueue) return play(newItems, 0, 0.0, PlayContext("queue"), false)
        newItems.forEach { items[it.uid] = it }
        player.addMediaItems(newItems.map(::mediaItem))
        originalOrder = originalOrder + newItems.map { it.uid }
        syncQueue()
    }

    // Starts a radio from a seed: the seed track first (the server puts it there), then more as
    // the queue runs low.
    fun startRadio(seedType: String, seedId: String, name: String) {
        scope.launch {
            runCatching { api.mix(seedType, seedId) }
                .onSuccess { mix ->
                    if (mix.tracks.isNotEmpty()) {
                        play(mix.tracks.map(AudioItem::of), 0, 0.0, PlayContext("radio", "$seedType:$seedId", mix.name.ifBlank { "$name Radio" }), false, radioSeed = seedType to seedId)
                    } else {
                        _state.update { it.copy(error = "Nothing to play for that radio yet.") }
                    }
                }
                .onFailure { error -> _state.update { it.copy(error = error.message ?: "Couldn't start the radio.") } }
        }
    }

    fun playMix(mix: Mix) {
        scope.launch {
            runCatching { api.namedMix(mix.id) }
                .onSuccess { result -> playTracks(result.tracks, 0, PlayContext("mix", mix.id, result.name.ifBlank { mix.name })) }
                .onFailure { error -> _state.update { it.copy(error = error.message ?: "Couldn't load that mix.") } }
        }
    }

    fun shuffleAll() {
        scope.launch {
            runCatching { api.shuffleAll() }
                .onSuccess { tracks -> playTracks(tracks, 0, PlayContext("queue", null, "Shuffle all")) }
                .onFailure { error -> _state.update { it.copy(error = error.message ?: "Couldn't shuffle the library.") } }
        }
    }

    // "Continue here": another device's queue, position and speed, picked up in one go.
    fun takeOver(resolved: NowPlayingResolved, autoplay: Boolean = true) {
        val np = resolved.nowPlaying
        val queue = buildFromRefs(np.queue, resolved.tracks, resolved.episodes)
        if (queue.isEmpty()) return
        play(queue, np.index.coerceIn(0, queue.lastIndex), np.positionSeconds, np.context, shuffle = false, autoplay = autoplay)
        _state.update { it.copy(shuffle = np.shuffle) }
        setRepeat(np.repeat)
        if (np.speed in 0.5..3.0) player.setPlaybackSpeed(np.speed.toFloat())
    }

    fun buildFromRefs(refs: List<QueueRef>, tracks: List<Track>, episodes: List<Episode>): List<AudioItem> {
        val trackById = tracks.associateBy { it.id }
        val episodeById = episodes.associateBy { it.id }
        return refs.map { ref ->
            when (ref.type) {
                AudioItem.TYPE_EPISODE -> episodeById[ref.id]?.let(AudioItem::of) ?: AudioItem.bare(ref.type, ref.id)
                else -> trackById[ref.id]?.let(AudioItem::of) ?: AudioItem.bare(AudioItem.TYPE_TRACK, ref.id)
            }
        }
    }

    // Items a controller (Android Auto, a Bluetooth resume) asked for, registered so the queue
    // shows them with their full details.
    fun register(newItems: List<AudioItem>, context: PlayContext?): List<MediaItem> {
        runtime.ensureService()
        items.clear()
        newItems.forEach { items[it.uid] = it }
        originalOrder = newItems.map { it.uid }
        _state.update { it.copy(context = context, shuffle = false, radioSeed = null, error = null) }
        return newItems.map(::mediaItem)
    }

    fun registerAdded(newItems: List<AudioItem>): List<MediaItem> {
        newItems.forEach { items[it.uid] = it }
        originalOrder = originalOrder + newItems.map { it.uid }
        return newItems.map(::mediaItem)
    }

    // ---------------- transport ----------------

    fun togglePlay() {
        if (player.isPlaying) player.pause() else resume()
    }

    fun resume() {
        if (!_state.value.hasQueue) return
        runtime.ensureService()
        if (player.playbackState == Player.STATE_IDLE) player.prepare()
        if (player.playbackState == Player.STATE_ENDED) player.seekTo(player.currentMediaItemIndex, 0)
        player.play()
    }

    fun pause() = player.pause()

    fun next() {
        if (player.hasNextMediaItem()) player.seekToNextMediaItem()
        else if (_state.value.radioSeed != null) maybeExtendRadio(force = true)
    }

    fun previous() = player.seekToPrevious()

    fun seekTo(seconds: Double) {
        player.seekTo((seconds * 1000).toLong().coerceAtLeast(0))
        updateProgress()
    }

    fun seekBy(seconds: Double) = seekTo((player.currentPosition / 1000.0 + seconds).coerceAtLeast(0.0))

    fun skipTo(index: Int, seconds: Double = 0.0) {
        if (index !in 0 until player.mediaItemCount) return
        player.seekTo(index, (seconds * 1000).toLong())
        resume()
    }

    fun stop() {
        finishTracked(completed = false, skipped = false)
        reportNowPlayingNow()
        player.stop()
        player.clearMediaItems()
        items.clear()
        originalOrder = emptyList()
        _state.update { PlayerState(speakerVolume = it.speakerVolume) }
        _progress.value = PlayerProgress()
        bump()
    }

    // ---------------- queue editing ----------------

    fun moveInQueue(from: Int, to: Int) {
        if (from !in 0 until player.mediaItemCount || to !in 0 until player.mediaItemCount) return
        player.moveMediaItem(from, to)
        syncQueue()
    }

    fun removeFromQueue(index: Int) {
        if (index !in 0 until player.mediaItemCount) return
        val uid = uidOf(player.getMediaItemAt(index))
        player.removeMediaItem(index)
        originalOrder = originalOrder.filter { it != uid }
        syncQueue()
    }

    fun clearUpNext() {
        val current = player.currentMediaItemIndex
        if (current + 1 < player.mediaItemCount) player.removeMediaItems(current + 1, player.mediaItemCount)
        syncQueue()
    }

    fun setShuffle(on: Boolean) {
        val state = _state.value
        if (on == state.shuffle || !state.hasQueue) {
            _state.update { it.copy(shuffle = on) }
            return
        }
        val queue = state.queue
        val currentIndex = player.currentMediaItemIndex
        val playing = queue.getOrNull(currentIndex)
        val reordered = if (on) {
            originalOrder = queue.map { it.uid }
            QueueOps.shuffled(queue, currentIndex)
        } else {
            val original = originalOrder.mapNotNull { items[it] }
            QueueOps.unshuffled(queue, original, playing).first
        }
        rebuildAround(reordered, playing)
        _state.update { it.copy(shuffle = on) }
        scheduleNowPlaying()
    }

    // Replaces everything around the playing entry without interrupting it.
    private fun rebuildAround(order: List<AudioItem>, playing: AudioItem?) {
        val currentIndex = player.currentMediaItemIndex
        val at = playing?.let { p -> order.indexOfFirst { it.uid == p.uid } } ?: -1
        if (playing == null || at < 0) {
            player.setMediaItems(order.map(::mediaItem), 0, 0)
            syncQueue()
            return
        }
        val count = player.mediaItemCount
        if (currentIndex + 1 < count) player.removeMediaItems(currentIndex + 1, count)
        if (currentIndex > 0) player.removeMediaItems(0, currentIndex)
        val before = order.subList(0, at)
        val after = order.subList(at + 1, order.size)
        if (before.isNotEmpty()) player.addMediaItems(0, before.map(::mediaItem))
        if (after.isNotEmpty()) player.addMediaItems(before.size + 1, after.map(::mediaItem))
        syncQueue()
    }

    fun cycleRepeat() {
        setRepeat(
            when (_state.value.repeat) {
                "off" -> "all"
                "all" -> "one"
                else -> "off"
            },
        )
    }

    fun setRepeat(mode: String) {
        player.repeatMode = when (mode) {
            "all" -> Player.REPEAT_MODE_ALL
            "one" -> Player.REPEAT_MODE_ONE
            else -> Player.REPEAT_MODE_OFF
        }
    }

    // ---------------- speed ----------------

    // For an episode the speed is the show's, remembered on the server (my-settings); for music
    // it's this phone's.
    fun setSpeed(speed: Float) {
        val value = speed.coerceIn(0.5f, 3.0f)
        player.setPlaybackSpeed(value)
        val episode = _state.value.current?.episode
        if (episode == null) {
            runtime.prefs.musicSpeed = value
            return
        }
        scope.launch {
            runCatching {
                val current = showSettings[episode.podcastId] ?: api.podcast(episode.podcastId).mySettings ?: PodcastMySettings()
                val updated = current.copy(playbackSpeed = value.toDouble())
                showSettings[episode.podcastId] = updated
                api.saveMySettings(episode.podcastId, updated)
            }.onFailure { Log.d(AudioApi.TAG, "Couldn't save the show's speed: ${it.message}") }
        }
    }

    fun forgetShowSettings(podcastId: String) {
        showSettings.remove(podcastId)
    }

    // ---------------- sleep timer ----------------

    fun setSleepMinutes(minutes: Int) {
        player.pauseAtEndOfMediaItems = false
        fadeVolume = 1f
        applyVolume()
        _state.update { it.copy(sleep = SleepTimer(SystemClock.elapsedRealtime() + minutes * 60_000L, endOfItem = false)) }
        startTicker()
    }

    fun setSleepEndOfItem() {
        fadeVolume = 1f
        applyVolume()
        player.pauseAtEndOfMediaItems = true
        _state.update { it.copy(sleep = SleepTimer(null, endOfItem = true)) }
        startTicker()
    }

    fun cancelSleep() {
        player.pauseAtEndOfMediaItems = false
        fadeVolume = 1f
        applyVolume()
        _state.update { it.copy(sleep = null) }
    }

    private fun stopForSleep() {
        player.pause()
        cancelSleep()
    }

    // ---------------- volume: ducking, speaker volume, alarm ramp ----------------

    // §8: duck drops to 20% until unduck, or 30 s at most.
    fun duck(seconds: Long = 30) {
        duckJob?.cancel()
        duckVolume = 0.2f
        applyVolume()
        duckJob = scope.launch {
            delay(seconds * 1000)
            unduck()
        }
    }

    fun unduck() {
        duckJob?.cancel()
        duckJob = null
        duckVolume = 1f
        applyVolume()
    }

    fun setSpeakerVolume(volume: Float) {
        rampJob?.cancel()
        rampVolume = 1f
        _state.update { it.copy(speakerVolume = volume.coerceIn(0f, 1f)) }
        applyVolume()
        bump()
    }

    // Alarms fade in from silence to `volume` over `rampSeconds`.
    fun rampTo(volume: Float, rampSeconds: Double) {
        rampJob?.cancel()
        _state.update { it.copy(speakerVolume = volume.coerceIn(0f, 1f)) }
        if (rampSeconds <= 0) {
            rampVolume = 1f
            applyVolume()
            return
        }
        rampVolume = 0f
        applyVolume()
        rampJob = scope.launch {
            val started = SystemClock.elapsedRealtime()
            val total = rampSeconds * 1000
            while (isActive) {
                val fraction = ((SystemClock.elapsedRealtime() - started) / total).toFloat().coerceIn(0f, 1f)
                rampVolume = fraction
                applyVolume()
                if (fraction >= 1f) break
                delay(250)
            }
        }
    }

    fun refreshSoundCheck() {
        gainVolume = soundCheckVolume(_state.value.current)
        applyVolume()
    }

    private fun soundCheckVolume(item: AudioItem?): Float {
        if (!runtime.prefs.soundCheck) return 1f
        val track = item?.track ?: return 1f
        // Album gain when an album plays in order (§11).
        val albumInOrder = _state.value.context?.type == "album" && !_state.value.shuffle
        return SoundCheck.volume(track.replayGain, albumInOrder)
    }

    private fun applyVolume() {
        player.volume = (gainVolume * fadeVolume * duckVolume * rampVolume * _state.value.speakerVolume).coerceIn(0f, 1f)
    }

    // ---------------- the queue mirror ----------------

    private fun syncQueue() {
        val queue = (0 until player.mediaItemCount).mapNotNull { index ->
            val mediaItem = player.getMediaItemAt(index)
            items[uidOf(mediaItem)] ?: fromMediaItem(mediaItem)
        }
        _state.update { it.copy(queue = queue, index = if (queue.isEmpty()) -1 else player.currentMediaItemIndex.coerceIn(0, queue.lastIndex)) }
        updateProgress()
        bump()
    }

    // A playlist entry this engine didn't make (should not happen; kept so the queue never lies).
    private fun fromMediaItem(mediaItem: MediaItem): AudioItem? {
        val metadata = mediaItem.mediaMetadata
        val uid = uidOf(mediaItem) ?: return null
        return AudioItem(uid, AudioItem.TYPE_TRACK, mediaItem.mediaId, metadata.title?.toString() ?: "Track", metadata.artist?.toString().orEmpty())
            .also { items[uid] = it }
    }

    fun mediaItem(item: AudioItem): MediaItem {
        val local = runtime.downloads.localFile(item.type, item.id)
        val uri = when {
            local != null -> Uri.fromFile(local)
            item.isEpisode -> Uri.parse(SCHEME + (item.episode?.audioUrl ?: AudioApi.episodeAudioPath(item.id)))
            else -> Uri.parse(SCHEME + AudioApi.trackStreamPath(item.id, runtime.streamBitrateCap()))
        }
        val art = runtime.downloads.localArt(item.artUrl)?.let(Uri::fromFile)
            ?: AudioApi.sized(item.artUrl, 600)?.let(runtime::absoluteUrl)?.let(Uri::parse)
        val metadata = MediaMetadata.Builder()
            .setTitle(item.title)
            .setArtist(item.subtitle)
            .setAlbumTitle(item.album)
            .setAlbumArtist(item.track?.albumArtist)
            .setDisplayTitle(item.title)
            .setSubtitle(item.subtitle)
            .setArtworkUri(art)
            .setTrackNumber(item.track?.trackNumber)
            .setDiscNumber(item.track?.discNumber)
            .setReleaseYear(item.track?.year)
            .setGenre(item.track?.genres?.firstOrNull())
            .setIsPlayable(true)
            .setIsBrowsable(false)
            .setMediaType(if (item.isEpisode) MediaMetadata.MEDIA_TYPE_PODCAST_EPISODE else MediaMetadata.MEDIA_TYPE_MUSIC)
            .setDurationMs(if (item.durationSeconds > 0) (item.durationSeconds * 1000).toLong() else null)
            .build()
        return MediaItem.Builder()
            .setMediaId("q:${item.uid}")
            .setUri(uri)
            .setMediaMetadata(metadata)
            .build()
    }

    // Re-points queued entries at their downloaded files (or back at the server) without a
    // restart of what is playing.
    fun refreshSources() {
        val current = player.currentMediaItemIndex
        for (index in 0 until player.mediaItemCount) {
            if (index == current) continue
            val item = items[uidOf(player.getMediaItemAt(index))] ?: continue
            player.replaceMediaItem(index, mediaItem(item))
        }
    }

    private fun uidOf(mediaItem: MediaItem?): Long? = mediaItem?.mediaId?.removePrefix("q:")?.toLongOrNull()

    // ---------------- reporting ----------------

    private fun startTracked() {
        val item = _state.value.current ?: run {
            tracked = null
            return
        }
        if (tracked?.item?.uid == item.uid) return
        tracked = Tracked(item, Instant.now())
        lastTick = SystemClock.elapsedRealtime()
        gainVolume = soundCheckVolume(item)
        applyVolume()
        if (item.isEpisode) applyShowSettings(item) else {
            val musicSpeed = runtime.prefs.musicSpeed
            if (player.playbackParameters.speed != musicSpeed) player.setPlaybackSpeed(musicSpeed)
            scope.launch { runCatching { api.nowPlayingTrack(item.id) } }
        }
        scheduleNowPlaying()
    }

    private fun applyShowSettings(item: AudioItem) {
        val episode = item.episode
        val podcastId = episode?.podcastId?.takeIf { it.isNotBlank() }
        val apply: (PodcastMySettings?) -> Unit = { settings ->
            if (_state.value.current?.uid == item.uid) {
                val speed = settings?.playbackSpeed?.toFloat() ?: runtime.prefs.podcastSpeed
                player.setPlaybackSpeed(speed.coerceIn(0.5f, 3.0f))
                val intro = settings?.skipIntroSeconds ?: 0
                if (intro > 0 && player.currentPosition < intro * 1000L) player.seekTo(intro * 1000L)
            }
        }
        if (podcastId == null) return apply(null)
        val known = showSettings[podcastId]
        if (known != null) return apply(known)
        apply(null)
        scope.launch {
            runCatching { api.podcast(podcastId).mySettings }
                .onSuccess { settings -> settings?.let { showSettings[podcastId] = it }; apply(settings) }
        }
    }

    private fun finishTracked(completed: Boolean, skipped: Boolean) {
        val done = tracked ?: return
        tick(done)
        tracked = null
        val item = done.item
        if (item.isEpisode) {
            val duration = done.durationSeconds.takeIf { it > 0 } ?: item.durationSeconds
            val position = if (completed) duration else done.lastPositionSeconds
            sendProgress(item, position, duration)
        } else if (done.listenedSeconds >= 1.0) {
            val body = JSONObject()
                .put("secondsListened", Math.round(done.listenedSeconds * 10) / 10.0)
                .put("completed", completed)
                .put("skipped", PlayReporting.isSkip(done.listenedSeconds, completed, skipped))
                .put("playedAt", done.startedAt.toString())
                .put("deviceName", runtime.deviceName)
            scope.launch { runtime.reports.send("POST", "/api/music/tracks/${AudioApi.seg(item.id)}/played", body) }
        }
    }

    private fun sendProgress(item: AudioItem, position: Double, duration: Double) {
        if (!PlayReporting.worthSavingProgress(position)) return
        val speed = player.playbackParameters.speed.toDouble()
        val body = JSONObject()
            .put("positionSeconds", position)
            .put("durationSeconds", duration)
            .put("speed", speed)
            .put("deviceName", runtime.deviceName)
        scope.launch {
            runtime.reports.send("POST", "/api/podcasts/episodes/${AudioApi.seg(item.id)}/progress", body, replaceKey = "progress:${item.id}")
        }
        // Mirror it on the queue entry so the screens show it without waiting for a reload.
        val episode = item.episode ?: return
        val played = PlayReporting.episodeFinished(position, duration)
        val updated = item.copy(episode = episode.copy(progress = EpisodeProgress(position, duration, position / duration * 100, played)))
        items[item.uid] = updated
        _state.update { state -> state.copy(queue = state.queue.map { if (it.uid == item.uid) updated else it }) }
    }

    private fun reportEpisodeProgress(force: Boolean) {
        val current = tracked ?: return
        if (!current.item.isEpisode) return
        val now = SystemClock.elapsedRealtime()
        if (!force && now - lastProgressReport < 15_000) return
        lastProgressReport = now
        val duration = (player.duration.takeIf { it != C.TIME_UNSET }?.div(1000.0)) ?: current.item.durationSeconds
        sendProgress(current.item, player.currentPosition / 1000.0, duration)
    }

    private fun scheduleNowPlaying() {
        nowPlayingJob?.cancel()
        nowPlayingJob = scope.launch {
            delay(800)
            reportNowPlayingNow()
        }
    }

    private fun reportNowPlayingNow() {
        val state = _state.value
        if (!state.hasQueue) return
        lastNowPlayingReport = SystemClock.elapsedRealtime()
        val (window, index) = QueueOps.window(state.queue.map { it.ref }, state.index)
        val nowPlaying = NowPlaying(
            deviceId = runtime.deviceId,
            deviceName = runtime.deviceName,
            kind = state.kind,
            queue = window,
            index = index,
            positionSeconds = player.currentPosition / 1000.0,
            playing = player.isPlaying,
            shuffle = state.shuffle,
            repeat = state.repeat,
            speed = player.playbackParameters.speed.toDouble(),
            context = state.context,
        )
        scope.launch { runCatching { api.putNowPlaying(nowPlaying) } }
    }

    // ---------------- the ticker ----------------

    private fun startTicker() {
        if (tickerJob?.isActive == true) return
        lastTick = SystemClock.elapsedRealtime()
        tickerJob = scope.launch {
            while (isActive) {
                delay(TICK_MS)
                if (!player.isPlaying && _state.value.sleep == null) {
                    updateProgress()
                    break
                }
                tick()
                updateProgress()
                reportEpisodeProgress(force = false)
                if (SystemClock.elapsedRealtime() - lastNowPlayingReport >= 30_000) reportNowPlayingNow()
                checkSleep()
                checkOutro()
                if (SystemClock.elapsedRealtime() % 5000 < TICK_MS) maybeExtendRadio()
            }
        }
    }

    private fun tick(target: Tracked? = tracked) {
        val now = SystemClock.elapsedRealtime()
        val current = target ?: return
        // Once the player has moved on, its position belongs to the next entry: keep the last one
        // the ticker saw for this one.
        val stillPlayingThis = target === tracked && uidOf(player.currentMediaItem) == current.item.uid
        if (player.isPlaying && target === tracked) {
            current.listenedSeconds += (now - lastTick).coerceIn(0, 5_000) / 1000.0
        }
        if (stillPlayingThis) {
            current.lastPositionSeconds = player.currentPosition / 1000.0
            player.duration.takeIf { it != C.TIME_UNSET && it > 0 }?.let { current.durationSeconds = it / 1000.0 }
        }
        lastTick = now
    }

    private fun updateProgress() {
        val duration = player.duration.takeIf { it != C.TIME_UNSET && it > 0 }?.div(1000.0)
            ?: _state.value.current?.durationSeconds ?: 0.0
        _progress.value = PlayerProgress(player.currentPosition / 1000.0, duration, player.bufferedPosition / 1000.0)
    }

    private fun checkSleep() {
        val sleep = _state.value.sleep ?: return
        val remainingMs = if (sleep.endsAtMillis != null) {
            sleep.endsAtMillis - SystemClock.elapsedRealtime()
        } else {
            val duration = player.duration.takeIf { it != C.TIME_UNSET } ?: return
            duration - player.currentPosition
        }
        // A 10 s fade before it stops (§11).
        fadeVolume = (remainingMs / FADE_MS.toFloat()).coerceIn(0f, 1f)
        applyVolume()
        if (sleep.endsAtMillis != null && remainingMs <= 0) stopForSleep()
    }

    private fun checkOutro() {
        val episode = _state.value.current?.episode ?: return
        val outro = showSettings[episode.podcastId]?.skipOutroSeconds ?: return
        if (outro <= 0) return
        val duration = player.duration.takeIf { it != C.TIME_UNSET } ?: return
        if (duration - player.currentPosition <= outro * 1000L && duration > outro * 2000L) {
            if (player.hasNextMediaItem()) player.seekToNextMediaItem() else {
                player.seekTo(duration)
            }
        }
    }

    private fun maybeExtendRadio(force: Boolean = false) {
        val state = _state.value
        val seed = state.radioSeed ?: return
        if (radioLoading) return
        if (!force && state.queue.size - state.index > 3) return
        radioLoading = true
        scope.launch {
            runCatching { api.mix(seed.first, seed.second, 25, state.queue.map { it.id }.takeLast(200)) }
                .onSuccess { mix ->
                    val known = _state.value.queue.map { it.id }.toSet()
                    val fresh = mix.tracks.filter { it.id !in known }.map(AudioItem::of)
                    if (fresh.isNotEmpty()) {
                        fresh.forEach { items[it.uid] = it }
                        player.addMediaItems(fresh.map(::mediaItem))
                        originalOrder = originalOrder + fresh.map { it.uid }
                        syncQueue()
                        if (force) player.seekToNextMediaItem()
                    }
                }
            radioLoading = false
        }
    }

    // ---------------- helpers ----------------

    fun currentPositionSeconds(): Double = player.currentPosition / 1000.0

    fun clearError() = _state.update { it.copy(error = null) }

    // Updates a track's details in the queue (after a love or a rating) so Now Playing shows it.
    fun updateTrack(track: Track) {
        val changed = items.values.filter { it.track?.id == track.id }
        if (changed.isEmpty()) return
        changed.forEach { items[it.uid] = it.copy(track = track) }
        _state.update { state -> state.copy(queue = state.queue.map { items[it.uid] ?: it }) }
        bump()
    }

    private fun bump() {
        changes.value = changes.value + 1
    }

    private fun isServerUrl(url: String): Boolean {
        val base = runtime.absoluteUrl("/") ?: return false
        return runCatching { Uri.parse(url).host == Uri.parse(base).host }.getOrDefault(false)
    }

    companion object {
        const val SCHEME = "sentinel:"
        const val SKIP_BACK_MS = 15_000L
        const val SKIP_FORWARD_MS = 30_000L
        private const val TICK_MS = 500L
        private const val FADE_MS = 10_000L

        fun repeatName(mode: Int): String = when (mode) {
            Player.REPEAT_MODE_ALL -> "all"
            Player.REPEAT_MODE_ONE -> "one"
            else -> "off"
        }
    }
}

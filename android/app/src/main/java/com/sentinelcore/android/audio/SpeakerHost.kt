package com.sentinelcore.android.audio

import android.util.Log
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

// "Let others play here" (§8): this phone registers as a speaker, so the kiosk, another phone or
// a wake-up alarm can play to it and control it. Heartbeat every 60 s, a long poll for commands,
// and a state report whenever what's playing changes. It lives as long as the app's process -
// the switch says so in Settings - and stops (and unregisters) when switched off.
class SpeakerHost(private val runtime: AudioRuntime) {
    private var heartbeatJob: Job? = null
    private var commandJob: Job? = null
    private var stateJob: Job? = null

    private val _status = MutableStateFlow<String?>(null)
    // A line for Settings: "Listening for other devices" or why not.
    val status: StateFlow<String?> = _status.asStateFlow()

    val running: Boolean get() = commandJob?.isActive == true

    fun refresh() {
        if (runtime.prefs.letOthersPlay && runtime.enabled.value.any) start() else stop(unregister = false)
    }

    fun setEnabled(enabled: Boolean) {
        runtime.prefs.letOthersPlay = enabled
        if (enabled) start() else stop(unregister = true)
    }

    // A new name or the announcements switch: tell the server straight away.
    fun detailsChanged() {
        if (running) runtime.scope.launch { heartbeat() }
    }

    private fun start() {
        if (running) return
        heartbeatJob = runtime.scope.launch {
            while (isActive) {
                heartbeat()
                delay(60_000)
            }
        }
        commandJob = runtime.scope.launch { listen() }
        stateJob = runtime.scope.launch { reportState() }
    }

    private fun stop(unregister: Boolean) {
        heartbeatJob?.cancel()
        commandJob?.cancel()
        stateJob?.cancel()
        heartbeatJob = null
        commandJob = null
        stateJob = null
        _status.value = null
        if (unregister) runtime.scope.launch { runCatching { runtime.api.removeSpeaker(runtime.deviceId) } }
    }

    private suspend fun heartbeat() {
        runCatching {
            runtime.api.speakerHeartbeat(
                runtime.deviceId,
                runtime.deviceName,
                runtime.deviceKind,
                runtime.prefs.allowAnnouncements,
                runtime.engine.state.value.speakerVolume.toDouble(),
            )
        }.onSuccess { _status.value = "Others can play to ${runtime.deviceName}." }
            .onFailure { _status.value = "Can't reach the server - retrying." }
    }

    private suspend fun listen() {
        // -1 the first time: only commands sent from now on (§8).
        var after = -1L
        var failures = 0
        while (true) {
            try {
                val batch = runtime.api.waitForCommands(runtime.deviceId, after, 25)
                failures = 0
                // In seq order, each one finished (an announce spoken) before the next.
                for (command in batch.commands.sortedBy { it.seq }) {
                    if (command.seq <= after && after >= 0) continue
                    after = maxOf(after, command.seq)
                    runCatching { apply(command) }.onFailure { Log.w(AudioApi.TAG, "Speaker command ${command.action} failed: ${it.message}") }
                }
                batch.latestSeq?.let { if (it > after) after = it }
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (error: Exception) {
                failures++
                delay((2_000L * failures).coerceAtMost(30_000))
            }
        }
    }

    suspend fun apply(command: SpeakerCommand) {
        val engine = runtime.engine
        when (command.action) {
            "play" -> {
                val items = resolve(command)
                if (items.isNotEmpty()) {
                    val volume = command.volume?.toFloat()
                    if (command.rampSeconds > 0) engine.rampTo(volume ?: engine.state.value.speakerVolume, command.rampSeconds)
                    else if (volume != null) engine.setSpeakerVolume(volume)
                    engine.play(items, command.index.coerceIn(0, items.lastIndex), command.positionSeconds, command.context, command.shuffle)
                }
            }
            "pause" -> engine.pause()
            "resume" -> engine.resume()
            "next" -> engine.next()
            "previous" -> engine.previous()
            "seek" -> engine.seekTo(command.positionSeconds)
            "volume" -> command.volume?.let {
                if (command.rampSeconds > 0) engine.rampTo(it.toFloat(), command.rampSeconds) else engine.setSpeakerVolume(it.toFloat())
            }
            "stop" -> engine.stop()
            "duck" -> engine.duck()
            "unduck" -> engine.unduck()
            "announce" -> announce(command.text.orEmpty())
        }
        pushState()
    }

    // Ducks whatever is playing, says it, then brings the music back. With nothing playing it
    // just speaks. Announcements the person turned off are skipped (the server checks too).
    suspend fun announce(text: String) {
        if (text.isBlank() || !runtime.prefs.allowAnnouncements) return
        val engine = runtime.engine
        val playing = engine.state.value.playing
        if (playing) engine.duck(seconds = 120)
        try {
            runtime.speech.speak(text)
        } finally {
            if (playing) engine.unduck()
        }
    }

    // The queue as tracks and episodes. The server resolves a queue it knows about, so the
    // queue is stored as this device's now-playing first and resolved from there.
    private suspend fun resolve(command: SpeakerCommand): List<AudioItem> {
        if (command.queue.isEmpty()) return emptyList()
        val (window, index) = QueueOps.window(command.queue, command.index)
        return runCatching {
            runtime.api.putNowPlaying(
                NowPlaying(
                    deviceId = runtime.deviceId,
                    deviceName = runtime.deviceName,
                    kind = if (window.getOrNull(index)?.type == AudioItem.TYPE_EPISODE) "podcast" else "music",
                    queue = window,
                    index = index,
                    positionSeconds = command.positionSeconds,
                    playing = true,
                    shuffle = command.shuffle,
                    context = command.context,
                ),
            )
            val resolved = runtime.api.resolveNowPlaying(runtime.deviceId)
            runtime.engine.buildFromRefs(window, resolved.tracks, resolved.episodes)
        }.getOrElse { runtime.engine.buildFromRefs(window, emptyList(), emptyList()) }
    }

    @OptIn(FlowPreview::class)
    private suspend fun reportState() = kotlinx.coroutines.coroutineScope {
        val engine = runtime.engine
        launch {
            while (isActive) {
                delay(15_000)
                if (engine.state.value.playing) pushState()
            }
        }
        engine.changes.debounce(700).collect { pushState() }
    }

    private suspend fun pushState() {
        if (!running) return
        val engine = runtime.engine
        val state = engine.state.value
        val current = state.current
        val progress = engine.progress.value
        runCatching {
            runtime.api.putSpeakerState(
                runtime.deviceId,
                SpeakerState(
                    playing = state.playing,
                    title = current?.title,
                    subtitle = current?.subtitle,
                    artUrl = current?.artUrl,
                    positionSeconds = progress.positionSeconds,
                    durationSeconds = progress.durationSeconds.takeIf { it > 0 } ?: current?.durationSeconds,
                    volume = state.speakerVolume.toDouble(),
                    kind = if (current == null) null else state.kind,
                    itemId = current?.id,
                ),
            )
        }
    }
}

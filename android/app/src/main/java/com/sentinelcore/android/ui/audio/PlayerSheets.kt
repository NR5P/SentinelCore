package com.sentinelcore.android.ui.audio

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.Cast
import androidx.compose.material.icons.filled.CastConnected
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.SmartDisplay
import androidx.compose.material.icons.filled.Speaker
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Tablet
import androidx.compose.material.icons.filled.Tv
import androidx.compose.material.icons.filled.VolumeUp
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.AudioApi
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.NowPlaying
import com.sentinelcore.android.audio.PlayContext
import com.sentinelcore.android.audio.QueueOps
import com.sentinelcore.android.audio.Speaker
import com.sentinelcore.android.audio.SpeakerCommand
import kotlinx.coroutines.delay
import kotlin.math.roundToInt

// ---------------- sleep timer ----------------

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SleepTimerSheet(onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    val state by ui.engine.state.collectAsState()
    val isEpisode = state.current?.isEpisode == true
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(Modifier.fillMaxWidth().padding(horizontal = 20.dp).padding(bottom = 28.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("Sleep timer", style = MaterialTheme.typography.titleLarge)
            Text(
                "Playback fades out over the last 10 seconds, then pauses.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            state.sleep?.let { timer ->
                // Ticks while the sheet is open.
                var now by remember { mutableStateOf(android.os.SystemClock.elapsedRealtime()) }
                LaunchedEffect(Unit) {
                    while (true) {
                        delay(1000)
                        now = android.os.SystemClock.elapsedRealtime()
                    }
                }
                val remaining = timer.remainingSeconds(now)
                Text(
                    if (timer.endOfItem) "Stops at the end of this ${if (isEpisode) "episode" else "song"}." else "Stops in ${AudioFormat.clock((remaining ?: 0).toDouble())}.",
                    color = MaterialTheme.colorScheme.primary,
                    style = MaterialTheme.typography.titleMedium,
                )
            }
            val choices = listOf(5, 10, 15, 30, 45, 60, 90)
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                choices.forEach { minutes ->
                    FilterChip(selected = false, onClick = { ui.engine.setSleepMinutes(minutes); onDismiss() }, label = { Text("$minutes min") })
                }
            }
            OutlinedButton(onClick = { ui.engine.setSleepEndOfItem(); onDismiss() }, modifier = Modifier.fillMaxWidth()) {
                Text(if (isEpisode) "End of this episode" else "End of this song")
            }
            if (state.sleep != null) {
                TextButton(onClick = { ui.engine.cancelSleep(); onDismiss() }, modifier = Modifier.fillMaxWidth()) { Text("Turn off the timer") }
            }
        }
    }
}

// ---------------- speed ----------------

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SpeedSheet(isEpisode: Boolean, showName: String, onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    val state by ui.engine.state.collectAsState()
    var speed by remember { mutableFloatStateOf(state.speed) }
    ModalBottomSheet(onDismissRequest = onDismiss) {
        Column(Modifier.fillMaxWidth().padding(horizontal = 20.dp).padding(bottom = 28.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("Playback speed", style = MaterialTheme.typography.titleLarge)
            Text(
                if (isEpisode) "Remembered for $showName, on every device." else "For music on this phone.",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(AudioFormat.speed(speed.toDouble()), style = MaterialTheme.typography.displaySmall, fontWeight = FontWeight.Bold, modifier = Modifier.align(Alignment.CenterHorizontally))
            Slider(
                value = speed,
                onValueChange = { speed = (it * 20).roundToInt() / 20f },
                onValueChangeFinished = { ui.engine.setSpeed(speed) },
                valueRange = 0.5f..3.0f,
                steps = 49,
            )
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf(0.8f, 1.0f, 1.2f, 1.5f, 1.8f, 2.0f, 2.5f).forEach { value ->
                    FilterChip(selected = speed == value, onClick = { speed = value; ui.engine.setSpeed(value) }, label = { Text(AudioFormat.speed(value.toDouble())) })
                }
            }
            if (isEpisode) {
                TextButton(onClick = {
                    ui.runtime.prefs.podcastSpeed = speed
                    ui.message("${AudioFormat.speed(speed.toDouble())} is now the speed for shows without their own")
                }) { Text("Use for shows without their own speed") }
            }
        }
    }
}

// ---------------- bookmark ----------------

@Composable
fun BookmarkDialog(episodeId: String, positionSeconds: Double, onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    var note by remember { mutableStateOf("") }
    var saving by remember { mutableStateOf(false) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Bookmark ${AudioFormat.clock(positionSeconds)}") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("The transcript around this moment is saved with it when there is one.", style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(value = note, onValueChange = { note = it }, label = { Text("Note (optional)") }, modifier = Modifier.fillMaxWidth())
            }
        },
        confirmButton = {
            TextButton(enabled = !saving, onClick = {
                saving = true
                ui.launch("Couldn't save the bookmark.") {
                    ui.api.addBookmark(episodeId, positionSeconds, note.trim())
                    ui.libraryVersion++
                    ui.message("Bookmarked ${AudioFormat.clock(positionSeconds)}", action = "All bookmarks") { ui.nav.open(AudioRoutes.BOOKMARKS) }
                }
                onDismiss()
            }) { Text("Save") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

// ---------------- Play on… ----------------

fun speakerIcon(kind: String): ImageVector = when (kind) {
    "tv" -> Icons.Default.Tv
    "kiosk" -> Icons.Default.SmartDisplay
    "phone" -> Icons.Default.PhoneAndroid
    "tablet" -> Icons.Default.Tablet
    "mac", "windows" -> Icons.Default.Computer
    "web" -> Icons.Default.Devices
    else -> Icons.Default.Speaker
}

// The speakers in the house (§8): send what's playing here to one of them, then control it from
// this phone. Also "say something" on a speaker that takes announcements.
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SpeakerPickerSheet(onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    var tick by remember { mutableStateOf(0) }
    val speakers = rememberLoadable(tick) { ui.api.speakers().filter { it.deviceId != ui.runtime.deviceId } }
    var controlling by remember { mutableStateOf<String?>(null) }
    // Keeps the list (and the remote's now-playing) fresh while the sheet is open.
    LaunchedEffect(Unit) {
        while (true) {
            delay(3_000)
            tick++
        }
    }
    val state by ui.engine.state.collectAsState()
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 20.dp).padding(bottom = 28.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("Play on…", style = MaterialTheme.typography.titleLarge)
            val list = speakers.value
            val remote = list?.firstOrNull { it.deviceId == controlling }
            if (remote != null) {
                RemoteControl(remote, onDone = { controlling = null }, onRefresh = { tick++ })
                HorizontalDivider()
            }
            SpeakerRow(
                icon = Icons.Default.PhoneAndroid,
                name = "This phone",
                detail = if (state.playing) "Playing here" else ui.runtime.deviceName,
                selected = controlling == null && state.hasQueue,
                online = true,
            ) {
                controlling = null
                onDismiss()
            }
            when {
                list == null && speakers.error == null -> CircularProgressIndicator(Modifier.align(Alignment.CenterHorizontally))
                list == null -> Text(speakers.error.orEmpty(), color = MaterialTheme.colorScheme.error)
                list.isEmpty() -> Text(
                    "No speakers yet. Turn on \"Let others play here\" on a kiosk, the Apple TV or another phone (Settings → Music & Podcasts).",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                else -> list.sortedByDescending { it.online }.forEach { speaker ->
                    SpeakerRow(
                        icon = speakerIcon(speaker.kind),
                        name = speaker.name,
                        detail = when {
                            !speaker.online -> "Offline"
                            speaker.state?.title != null -> (if (speaker.state.playing) "Playing " else "Paused: ") + speaker.state.title
                            else -> "Ready"
                        },
                        selected = controlling == speaker.deviceId,
                        online = speaker.online,
                    ) {
                        if (state.hasQueue) sendQueueTo(ui, speaker)
                        controlling = speaker.deviceId
                    }
                }
            }
        }
    }
}

// Hands this phone's queue, at this moment, to a speaker, and pauses it here.
private fun sendQueueTo(ui: AudioUi, speaker: Speaker) {
    val state = ui.engine.state.value
    val (queue, index) = QueueOps.window(state.queue.map { it.ref }, state.index)
    val command = SpeakerCommand(
        action = "play",
        queue = queue,
        index = index,
        positionSeconds = ui.engine.currentPositionSeconds(),
        shuffle = state.shuffle,
        context = state.context ?: PlayContext("queue"),
    )
    ui.launch("Couldn't reach ${speaker.name}.") {
        ui.api.sendSpeakerCommand(speaker.deviceId, command)
        ui.engine.pause()
        ui.message("Playing on ${speaker.name}")
    }
}

@Composable
private fun SpeakerRow(icon: ImageVector, name: String, detail: String, selected: Boolean, online: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp))
            .background(if (selected) MaterialTheme.colorScheme.primary.copy(alpha = 0.12f) else MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f))
            .clickable(enabled = online, onClick = onClick)
            .padding(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(if (selected) Icons.Default.CastConnected else icon, contentDescription = null, tint = if (online) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(name, fontWeight = FontWeight.Medium, color = if (online) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant)
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

@Composable
private fun RemoteControl(speaker: Speaker, onDone: () -> Unit, onRefresh: () -> Unit) {
    val ui = LocalAudioUi.current
    val remoteState = speaker.state
    var volume by remember(speaker.deviceId) { mutableFloatStateOf(speaker.volume.toFloat()) }
    var say by remember { mutableStateOf("") }
    fun send(command: SpeakerCommand) = ui.launch("Couldn't reach ${speaker.name}.") {
        ui.api.sendSpeakerCommand(speaker.deviceId, command)
        delay(600)
        onRefresh()
    }
    Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primary.copy(alpha = 0.08f))) {
        Column(Modifier.fillMaxWidth().padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Cast, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(8.dp))
                Text("Controlling ${speaker.name}", style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                IconButton(onClick = onDone) { Icon(Icons.Default.Close, contentDescription = "Stop controlling") }
            }
            if (remoteState?.title != null) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    ArtImage(remoteState.artUrl, Modifier.size(52.dp), sizePx = 160, seed = remoteState.title)
                    Spacer(Modifier.width(10.dp))
                    Column(Modifier.weight(1f)) {
                        Text(remoteState.title, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            listOfNotNull(remoteState.subtitle, remoteState.durationSeconds?.let { "${AudioFormat.clock(remoteState.positionSeconds)} / ${AudioFormat.clock(it)}" }).joinToString(" · "),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            maxLines = 1,
                        )
                    }
                }
            } else {
                Text("Nothing playing there yet.", style = MaterialTheme.typography.bodySmall)
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = { send(SpeakerCommand(action = "previous")) }) { Icon(Icons.Default.SkipPrevious, contentDescription = "Previous") }
                FilledIconButton(onClick = { send(SpeakerCommand(action = if (remoteState?.playing == true) "pause" else "resume")) }, modifier = Modifier.size(52.dp)) {
                    Icon(if (remoteState?.playing == true) Icons.Default.Pause else Icons.Default.PlayArrow, contentDescription = "Play or pause")
                }
                IconButton(onClick = { send(SpeakerCommand(action = "next")) }) { Icon(Icons.Default.SkipNext, contentDescription = "Next") }
                IconButton(onClick = { send(SpeakerCommand(action = "stop")) }) { Icon(Icons.Default.Stop, contentDescription = "Stop") }
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.VolumeUp, contentDescription = "Volume", modifier = Modifier.size(20.dp))
                Slider(
                    value = volume,
                    onValueChange = { volume = it },
                    onValueChangeFinished = { send(SpeakerCommand(action = "volume", volume = volume.toDouble())) },
                    modifier = Modifier.weight(1f).padding(start = 8.dp),
                )
            }
            TextButton(onClick = {
                // Bring it back: pick up where the speaker is, here.
                ui.launch {
                    ui.api.sendSpeakerCommand(speaker.deviceId, SpeakerCommand(action = "pause"))
                    remoteState?.positionSeconds?.let { ui.engine.seekTo(it) }
                    ui.engine.resume()
                    onDone()
                }
            }) { Text("Bring it back to this phone") }
            if (speaker.allowAnnouncements) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        value = say,
                        onValueChange = { say = it },
                        placeholder = { Text("Say something on ${speaker.name}") },
                        singleLine = true,
                        modifier = Modifier.weight(1f),
                    )
                    IconButton(enabled = say.isNotBlank(), onClick = {
                        send(SpeakerCommand(action = "announce", text = say.trim()))
                        ui.message("Announcing on ${speaker.name}")
                        say = ""
                    }) { Icon(Icons.AutoMirrored.Filled.Send, contentDescription = "Announce") }
                }
            }
        }
    }
}

// ---------------- Continue from <device> ----------------

// "Playing on Kitchen iPad - continue here?" (§7). Shows the newest other device of this kind
// that played in the last day, until it's dismissed or this phone takes over.
@Composable
fun ContinueBanner(kind: String, modifier: Modifier = Modifier) {
    val ui = LocalAudioUi.current
    val state by ui.engine.state.collectAsState()
    var candidate by remember { mutableStateOf<NowPlaying?>(null) }
    var dismissed by remember { mutableStateOf(ui.runtime.prefs.dismissedContinue) }
    LaunchedEffect(kind, state.hasQueue) {
        if (state.hasQueue) {
            candidate = null
            return@LaunchedEffect
        }
        val now = System.currentTimeMillis()
        candidate = runCatching { ui.api.nowPlaying() }.getOrNull()
            ?.filter { it.deviceId != ui.runtime.deviceId && it.kind == kind && it.queue.isNotEmpty() }
            ?.filter { np -> AudioFormat.parseInstant(np.updatedAt)?.let { now - it.toEpochMilli() < 24 * 3600_000L } ?: true }
            ?.maxByOrNull { AudioFormat.parseInstant(it.updatedAt)?.toEpochMilli() ?: 0L }
    }
    val np = candidate ?: return
    val key = "${np.deviceId}|${np.updatedAt}"
    if (dismissed == key || state.hasQueue) return
    Card(
        modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.primaryContainer),
    ) {
        Row(Modifier.fillMaxWidth().padding(start = 14.dp, end = 4.dp, top = 8.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(speakerIcon("phone"), contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer)
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text(
                    (if (np.playing) "Playing on " else "Paused on ") + np.deviceName,
                    style = MaterialTheme.typography.titleSmall,
                    color = MaterialTheme.colorScheme.onPrimaryContainer,
                )
                Text(
                    listOfNotNull(np.context?.name, "at ${AudioFormat.clock(np.positionSeconds)}").joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onPrimaryContainer.copy(alpha = 0.8f),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            TextButton(onClick = {
                ui.launch("Couldn't pick that up.") {
                    val resolved = ui.api.resolveNowPlaying(np.deviceId)
                    ui.engine.takeOver(resolved)
                    ui.runtime.prefs.dismissedContinue = key
                    dismissed = key
                }
            }) { Text("Continue here") }
            IconButton(onClick = {
                ui.runtime.prefs.dismissedContinue = key
                dismissed = key
            }) { Icon(Icons.Default.Close, contentDescription = "Dismiss") }
        }
    }
}



package com.sentinelcore.android.ui.audio

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
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
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Alarm
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.DownloadForOffline
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.WbTwilight
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TimePicker
import androidx.compose.material3.rememberTimePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.sentinelcore.android.audio.AlarmSource
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.AudioItem
import com.sentinelcore.android.audio.Briefing
import com.sentinelcore.android.audio.PlayContext
import com.sentinelcore.android.audio.WakeAlarm
import com.sentinelcore.android.ui.music.SwitchLine
import com.sentinelcore.android.ui.screens.DetailHeader
import kotlinx.coroutines.launch

// ---------------- Downloads ----------------

@Composable
fun DownloadsScreen() {
    val ui = LocalAudioUi.current
    val entries by ui.runtime.downloads.entries.collectAsState()
    var pending by remember { mutableIntStateOf(0) }
    LaunchedEffect(entries.size) { pending = ui.runtime.reports.pendingCount() }
    var confirmAll by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Downloads", onBack = ui.nav.back) {
            if (entries.isNotEmpty()) TextButton(onClick = { confirmAll = true }) { Text("Remove all") }
        }
        val total = entries.sumOf { it.bytes }
        val free = ui.runtime.context.filesDir.usableSpace
        Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)) {
            Text("${AudioFormat.bytes(total)} used on this phone · ${AudioFormat.bytes(free)} free", style = MaterialTheme.typography.bodyMedium)
            LinearProgressIndicator(progress = { if (free + total > 0) (total.toFloat() / (free + total)).coerceIn(0.01f, 1f) else 0f }, modifier = Modifier.fillMaxWidth().padding(top = 6.dp))
            if (pending > 0) Text("$pending plays and positions from offline listening wait to be sent.", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp))
            SwitchLine("Download only on Wi-Fi", ui.runtime.prefs.downloadOnWifiOnly.let { remember { mutableStateOf(it) } }.value) {
                ui.runtime.prefs.downloadOnWifiOnly = it
                ui.runtime.downloads.schedule()
            }
        }
        if (entries.isEmpty()) {
            EmptyPanel(Icons.Default.DownloadForOffline, "Nothing downloaded", "Download albums, playlists and episodes from their menus to listen with no connection.")
            return@Column
        }
        val done = entries.filter { it.done }.sortedByDescending { it.addedAt }
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
            val active = entries.filter { !it.done }
            if (active.isNotEmpty()) item { SectionTitle("In progress") }
            items(active, key = { "a" + it.key }) { entry ->
                Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    ArtImage(entry.artUrl, Modifier.size(44.dp), sizePx = 132, seed = entry.title)
                    Spacer(Modifier.width(12.dp))
                    Column(Modifier.weight(1f)) {
                        Text(entry.title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        if (entry.state == "downloading") LinearProgressIndicator(progress = { entry.progress }, modifier = Modifier.fillMaxWidth().padding(top = 4.dp))
                        Text(entry.error ?: entry.state.replaceFirstChar(Char::uppercase), style = MaterialTheme.typography.labelSmall, color = if (entry.state == "failed") MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    if (entry.state == "failed") IconButton(onClick = { ui.runtime.downloads.retry(entry) }) { Icon(Icons.Default.Refresh, contentDescription = "Retry") }
                    IconButton(onClick = { ui.runtime.downloads.remove(entry.type, entry.id) }) { Icon(Icons.Default.Delete, contentDescription = "Remove") }
                }
            }
            for ((label, type) in listOf("Music" to AudioItem.TYPE_TRACK, "Podcasts" to AudioItem.TYPE_EPISODE)) {
                val ofType = done.filter { it.type == type }
                if (ofType.isEmpty()) continue
                item(key = "h$type") {
                    SectionTitle(label, subtitle = "${ofType.size} · ${AudioFormat.bytes(ofType.sumOf { it.bytes })}", actionLabel = "Play all") {
                        ui.engine.play(ofType.map { it.toItem() }, 0, 0.0, PlayContext("queue", null, "Downloads"), false)
                    }
                }
                items(ofType, key = { "d" + it.key }) { entry ->
                    Row(
                        Modifier.fillMaxWidth().clickable {
                            ui.engine.play(ofType.map { it.toItem() }, ofType.indexOf(entry), 0.0, PlayContext("queue", null, "Downloads"), false)
                        }.padding(horizontal = 16.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        ArtImage(entry.artUrl, Modifier.size(44.dp), sizePx = 132, seed = entry.album ?: entry.title)
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f)) {
                            Text(entry.title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(listOfNotNull(entry.subtitle, AudioFormat.bytes(entry.bytes)).joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
                        }
                        IconButton(onClick = { ui.runtime.downloads.remove(entry.type, entry.id) }) { Icon(Icons.Default.Delete, contentDescription = "Remove download") }
                    }
                }
            }
        }
    }
    if (confirmAll) {
        AlertDialog(
            onDismissRequest = { confirmAll = false },
            title = { Text("Remove every download?") },
            text = { Text("They stay on the server; this frees ${AudioFormat.bytes(entries.sumOf { it.bytes })} on this phone.") },
            confirmButton = { TextButton(onClick = { confirmAll = false; ui.runtime.downloads.removeAll() }) { Text("Remove", color = MaterialTheme.colorScheme.error) } },
            dismissButton = { TextButton(onClick = { confirmAll = false }) { Text("Cancel") } },
        )
    }
}

// ---------------- Wake-up alarms ----------------

private val dayNames = mapOf("mon" to "M", "tue" to "T", "wed" to "W", "thu" to "T", "fri" to "F", "sat" to "S", "sun" to "S")

fun describeDays(days: List<String>): String = when {
    days.isEmpty() -> "Once"
    days.size == 7 -> "Every day"
    days.toSet() == setOf("mon", "tue", "wed", "thu", "fri") -> "Weekdays"
    days.toSet() == setOf("sat", "sun") -> "Weekends"
    else -> WakeAlarm.WEEKDAYS.filter { it in days }.joinToString(", ") { it.replaceFirstChar(Char::uppercase) }
}

@Composable
fun AlarmsScreen() {
    val ui = LocalAudioUi.current
    val alarms = rememberLoadable(Unit) { ui.api.alarms() }
    val speakers = rememberLoadable(Unit) { ui.api.speakers() }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Wake-up alarms", onBack = ui.nav.back)
        Box(Modifier.fillMaxSize()) {
            LoadableContent(alarms) { list ->
                if (list.isEmpty()) {
                    EmptyPanel(Icons.Default.Alarm, "No alarms", "Wake up to music, a podcast or your Morning Briefing on a speaker - the kiosk, the Apple TV or a phone that lets others play to it.")
                    return@LoadableContent
                }
                LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 96.dp)) {
                    items(list, key = { it.id ?: it.name }) { alarm ->
                        var enabled by remember(alarm) { mutableStateOf(alarm.enabled) }
                        Row(
                            Modifier.fillMaxWidth().clickable { ui.nav.open("audio/alarm/${alarm.id}") }.padding(horizontal = 16.dp, vertical = 12.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Column(Modifier.weight(1f)) {
                                Text(alarm.time, style = MaterialTheme.typography.displaySmall, fontWeight = FontWeight.Light, color = if (enabled) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant)
                                Text(
                                    listOfNotNull(alarm.name.takeIf { it != describeDays(alarm.days) }, describeDays(alarm.days), alarm.source.name ?: alarm.source.type.replaceFirstChar(Char::uppercase),
                                        speakers.value?.firstOrNull { it.deviceId == alarm.speakerId }?.name?.let { "on $it" }).joinToString(" · "),
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            Switch(checked = enabled, onCheckedChange = { value ->
                                enabled = value
                                ui.launch("Couldn't save it.") { ui.api.saveAlarm(alarm.copy(enabled = value)) }
                            })
                        }
                        HorizontalDivider(Modifier.padding(horizontal = 16.dp))
                    }
                }
            }
            FloatingActionButton(onClick = { ui.nav.open("audio/alarm/new") }, modifier = Modifier.align(Alignment.BottomEnd).padding(20.dp)) {
                Icon(Icons.Default.Alarm, contentDescription = "New alarm")
            }
        }
    }
}

@Composable
private fun Box(modifier: Modifier, content: @Composable androidx.compose.foundation.layout.BoxScope.() -> Unit) =
    androidx.compose.foundation.layout.Box(modifier, content = content)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AlarmEditorScreen(alarmId: String?) {
    val ui = LocalAudioUi.current
    var alarm by remember { mutableStateOf<WakeAlarm?>(if (alarmId == null) WakeAlarm(days = listOf("mon", "tue", "wed", "thu", "fri")) else null) }
    val speakers = rememberLoadable(Unit) { ui.api.speakers() }
    val playlists = rememberLoadable(Unit) { runCatching { ui.api.playlists() }.getOrDefault(emptyList()) }
    val shows = rememberLoadable(Unit) { runCatching { ui.api.podcasts() }.getOrDefault(emptyList()) }
    val mixes = rememberLoadable(Unit) { runCatching { ui.api.musicHome().mixes }.getOrDefault(emptyList()) }
    LaunchedEffect(alarmId) {
        if (alarmId != null) runCatching { ui.api.alarms().first { it.id == alarmId } }.onSuccess { alarm = it }.onFailure { ui.failed(it, "Couldn't open the alarm.") }
    }
    var confirmDelete by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize()) {
        DetailHeader(if (alarmId == null) "New alarm" else "Edit alarm", onBack = ui.nav.back) {
            if (alarmId != null) IconButton(onClick = { confirmDelete = true }) { Icon(Icons.Default.Delete, contentDescription = "Delete alarm") }
        }
        val current = alarm ?: run {
            CircularProgressIndicator(Modifier.padding(32.dp))
            return@Column
        }
        val (hour, minute) = current.time.split(":").let { (it.getOrNull(0)?.toIntOrNull() ?: 7) to (it.getOrNull(1)?.toIntOrNull() ?: 0) }
        val time = rememberTimePickerState(hour, minute, is24Hour = false)
        LaunchedEffect(time.hour, time.minute) { alarm = alarm?.copy(time = "%02d:%02d".format(time.hour, time.minute)) }
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            TimePicker(time, Modifier.align(Alignment.CenterHorizontally))
            Text("Times are the server's time zone.", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            OutlinedTextField(value = current.name, onValueChange = { alarm = current.copy(name = it) }, label = { Text("Name") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            Text("Repeat", style = MaterialTheme.typography.titleSmall)
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                WakeAlarm.WEEKDAYS.forEach { day ->
                    FilterChip(selected = day in current.days, onClick = {
                        alarm = current.copy(days = if (day in current.days) current.days - day else WakeAlarm.WEEKDAYS.filter { it in current.days + day })
                    }, label = { Text(day.replaceFirstChar(Char::uppercase)) })
                }
            }
            Text(describeDays(current.days) + if (current.days.isEmpty()) " - then it turns itself off" else "", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text("Speaker", style = MaterialTheme.typography.titleSmall)
            val speakerList = speakers.value.orEmpty()
            Choice(
                speakerList.firstOrNull { it.deviceId == current.speakerId }?.let { it.name + if (!it.online) " (offline now)" else "" } ?: "Choose a speaker",
                speakerList.map { it.deviceId to it.name },
            ) { alarm = current.copy(speakerId = it) }
            if (speakerList.isEmpty()) Text("No speakers yet. Turn on \"Let others play here\" on the device that should wake you.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            Text("Wake up to", style = MaterialTheme.typography.titleSmall)
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                listOf("shuffle" to "Shuffle all", "briefing" to "Morning Briefing", "playlist" to "Playlist", "mix" to "Mix", "podcast" to "Podcast").forEach { (type, label) ->
                    FilterChip(selected = current.source.type == type, onClick = { alarm = current.copy(source = AlarmSource(type, null, if (type == "shuffle" || type == "briefing") label else null)) }, label = { Text(label) })
                }
            }
            when (current.source.type) {
                "playlist" -> Choice(current.source.name ?: "Choose a playlist", playlists.value.orEmpty().map { it.id to it.name }) { id ->
                    alarm = current.copy(source = AlarmSource("playlist", id, playlists.value?.firstOrNull { it.id == id }?.name))
                }
                "mix" -> Choice(current.source.name ?: "Choose a mix", mixes.value.orEmpty().map { it.id to it.name }) { id ->
                    alarm = current.copy(source = AlarmSource("mix", id, mixes.value?.firstOrNull { it.id == id }?.name))
                }
                "podcast" -> Choice(current.source.name ?: "Choose a show", shows.value.orEmpty().map { it.id to it.title }) { id ->
                    alarm = current.copy(source = AlarmSource("podcast", id, shows.value?.firstOrNull { it.id == id }?.title))
                }
                "briefing" -> Text("Reads the weather, your calendar and reminders, then plays the shows you've put in the briefing.", style = MaterialTheme.typography.bodySmall)
            }
            Text("Volume ${(current.volume * 100).toInt()}%", style = MaterialTheme.typography.titleSmall)
            Slider(value = current.volume.toFloat(), onValueChange = { alarm = current.copy(volume = it.toDouble()) })
            Text(if (current.rampSeconds == 0) "Starts at full volume" else "Fades in over ${current.rampSeconds / 60} min ${current.rampSeconds % 60} s".replace(" 0 s", "").replace("0 min ", ""), style = MaterialTheme.typography.titleSmall)
            Slider(value = current.rampSeconds.toFloat(), onValueChange = { alarm = current.copy(rampSeconds = (it / 15).toInt() * 15) }, valueRange = 0f..600f)
            SwitchLine("On", current.enabled) { alarm = current.copy(enabled = it) }
            current.nextFireAt?.let { Text("Next: ${AudioFormat.day(it)} at ${current.time}", style = MaterialTheme.typography.bodySmall) }
        }
        Row(Modifier.fillMaxWidth().padding(16.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            if (alarmId != null) OutlinedButton(onClick = { ui.launch("Couldn't test it.") { ui.api.testAlarm(alarmId); ui.message("Playing it on the speaker now") } }, modifier = Modifier.weight(1f)) { Text("Test now") }
            Button(
                enabled = current.speakerId != null && (current.source.type in setOf("shuffle", "briefing") || current.source.id != null),
                onClick = { ui.launch("Couldn't save the alarm.") { ui.api.saveAlarm(current); ui.libraryVersion++; ui.nav.back() } },
                modifier = Modifier.weight(1f),
            ) { Text("Save") }
        }
    }
    if (confirmDelete && alarmId != null) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("Delete this alarm?") },
            confirmButton = { TextButton(onClick = { confirmDelete = false; ui.launch { ui.api.deleteAlarm(alarmId); ui.libraryVersion++; ui.nav.back() } }) { Text("Delete", color = MaterialTheme.colorScheme.error) } },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun Choice(label: String, options: List<Pair<String, String>>, onPick: (String) -> Unit) {
    var open by remember { mutableStateOf(false) }
    androidx.compose.foundation.layout.Box {
        OutlinedButton(onClick = { open = true }, modifier = Modifier.fillMaxWidth()) { Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis) }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            if (options.isEmpty()) DropdownMenuItem(text = { Text("Nothing to choose yet") }, onClick = { open = false })
            options.forEach { (id, name) -> DropdownMenuItem(text = { Text(name) }, onClick = { open = false; onPick(id) }) }
        }
    }
}

// ---------------- Morning Briefing ----------------

// §9: the greeting and each speech line said with this phone's voice, the line being said lit up,
// then the briefing's episodes play as a queue.
@Composable
fun BriefingScreen() {
    val ui = LocalAudioUi.current
    val briefing = rememberLoadable(Unit) { ui.api.briefing() }
    var speaking by remember { mutableIntStateOf(-2) }
    var running by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Morning Briefing", onBack = { ui.runtime.speech.stop(); ui.nav.back() })
        LoadableContent(briefing) { value: Briefing ->
            val lines = listOf(value.greeting) + value.speech
            fun start() {
                if (running) return
                running = true
                ui.scope.launch {
                    ui.engine.pause()
                    for ((index, line) in lines.withIndex()) {
                        if (!running) break
                        speaking = index
                        if (!ui.runtime.speech.speak(line)) { ui.message("This phone has no speech voice; skipping to the episodes."); break }
                    }
                    speaking = -1
                    if (running && value.episodes.isNotEmpty()) {
                        ui.engine.playEpisodes(value.episodes, 0, PlayContext("queue", null, "Morning Briefing"))
                    }
                    running = false
                }
            }
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(16.dp)) {
                item {
                    Card(Modifier.fillMaxWidth()) {
                        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(Icons.Default.WbTwilight, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                                Spacer(Modifier.width(8.dp))
                                Text("Today", style = MaterialTheme.typography.titleMedium)
                            }
                            lines.forEachIndexed { index, line ->
                                Text(
                                    line,
                                    style = if (index == 0) MaterialTheme.typography.headlineSmall else MaterialTheme.typography.bodyLarge,
                                    fontWeight = if (index == speaking) FontWeight.Bold else FontWeight.Normal,
                                    color = if (index == speaking) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface,
                                )
                            }
                        }
                    }
                }
                item {
                    Row(Modifier.fillMaxWidth().padding(vertical = 12.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        Button(onClick = { start() }, enabled = !running, modifier = Modifier.weight(1f)) { Text(if (running) "Playing…" else "Play the briefing") }
                        if (running) OutlinedButton(onClick = { running = false; ui.runtime.speech.stop() }) { Text("Stop") }
                    }
                }
                if (value.episodes.isEmpty()) {
                    item { Text("No shows are in your briefing. Turn on \"Include in my Morning Briefing\" in a show's settings.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                } else {
                    item { SectionTitle("Then") }
                    items(value.episodes, key = { it.id }) { episode -> EpisodeRow(episode) { ui.nav.openEpisode(episode.id) } }
                }
            }
        }
    }
}

// ---------------- ListenBrainz ----------------

@Composable
fun ListenBrainzScreen() {
    val ui = LocalAudioUi.current
    val context = LocalContext.current
    val status = rememberLoadable(Unit) { ui.api.listenBrainz() }
    var token by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("ListenBrainz", onBack = ui.nav.back)
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("Every song you finish is sent to your ListenBrainz profile (\"scrobbled\"), from any device. Retried for a day if ListenBrainz is down.", style = MaterialTheme.typography.bodyMedium)
            LoadableContent(status) { value ->
                Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    if (value.connected) {
                        Text("Connected as ${value.username}", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary)
                        OutlinedButton(enabled = !busy, onClick = {
                            busy = true
                            ui.launch("Couldn't disconnect.") { try { status.value = ui.api.setListenBrainz("") } finally { busy = false } }
                        }) { Text("Disconnect") }
                    } else {
                        OutlinedTextField(value = token, onValueChange = { token = it.trim() }, label = { Text("Your user token") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                        TextButton(onClick = { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://listenbrainz.org/settings/"))) }) { Text("Find it at listenbrainz.org/settings") }
                        Button(enabled = token.isNotBlank() && !busy, onClick = {
                            busy = true
                            ui.launch("That token didn't work.") {
                                try {
                                    status.value = ui.api.setListenBrainz(token)
                                    ui.message("Connected to ListenBrainz")
                                } finally { busy = false }
                            }
                        }) { Text(if (busy) "Checking…" else "Connect") }
                    }
                }
            }
        }
    }
}

// ---------------- Equalizer ----------------

@Composable
fun EqualizerScreen() {
    val ui = LocalAudioUi.current
    val effects = ui.engine.effects
    var enabled by remember { mutableStateOf(ui.runtime.prefs.equalizerEnabled) }
    var tick by remember { mutableIntStateOf(0) }
    val info = remember(enabled, tick) { effects.info() }
    var preset by remember { mutableIntStateOf(ui.runtime.prefs.equalizerPreset) }
    var bass by remember { mutableFloatStateOf(ui.runtime.prefs.bassBoost.toFloat()) }
    var soundCheck by remember { mutableStateOf(ui.runtime.prefs.soundCheck) }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Equalizer", onBack = ui.nav.back) {
            TextButton(onClick = { effects.reset(); preset = -1; bass = 0f; tick++ }) { Text("Reset") }
        }
        Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            SwitchLine("Sound Check", soundCheck, "Plays every song at the same loudness, using its ReplayGain (album gain when an album plays in order).") {
                soundCheck = it
                ui.runtime.prefs.soundCheck = it
                ui.engine.refreshSoundCheck()
            }
            HorizontalDivider()
            SwitchLine("Equalizer", enabled, "Applies to everything this app plays.") { enabled = it; effects.setEnabled(it); tick++ }
            if (!info.available) {
                Text("This phone has no equalizer for this app's audio.", color = MaterialTheme.colorScheme.error)
                return@Column
            }
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                FilterChip(selected = preset < 0, enabled = enabled, onClick = { preset = -1; effects.usePreset(-1); tick++ }, label = { Text("Custom") })
                info.presets.forEachIndexed { index, name ->
                    FilterChip(selected = preset == index, enabled = enabled, onClick = { preset = index; effects.usePreset(index); tick++ }, label = { Text(name) })
                }
            }
            info.bands.forEach { band ->
                var level by remember(band.index, tick) { mutableFloatStateOf(band.level.toFloat()) }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(if (band.centerHz >= 1000) "${band.centerHz / 1000} kHz" else "${band.centerHz} Hz", modifier = Modifier.width(64.dp), style = MaterialTheme.typography.labelMedium)
                    Slider(
                        value = level,
                        enabled = enabled,
                        onValueChange = { level = it },
                        onValueChangeFinished = { effects.setBand(band.index, level.toInt(), info.bands.size); preset = -1 },
                        valueRange = info.minLevel.toFloat()..info.maxLevel.toFloat(),
                        modifier = Modifier.weight(1f),
                    )
                    Text("%+.0f dB".format(level / 100f), modifier = Modifier.width(56.dp), style = MaterialTheme.typography.labelSmall)
                }
            }
            if (info.bassSupported) {
                Text("Bass boost", style = MaterialTheme.typography.titleSmall)
                Slider(value = bass, enabled = enabled, onValueChange = { bass = it }, onValueChangeFinished = { effects.setBassBoost(bass.toInt()) }, valueRange = 0f..1000f)
            }
        }
    }
}

// ---------------- this phone's Music & Podcasts settings ----------------

@Composable
fun AudioSettingsScreen() {
    val ui = LocalAudioUi.current
    val context = LocalContext.current
    val prefs = ui.runtime.prefs
    var letOthers by remember { mutableStateOf(prefs.letOthersPlay) }
    var announcements by remember { mutableStateOf(prefs.allowAnnouncements) }
    var name by remember { mutableStateOf(prefs.speakerName.ifBlank { ui.runtime.deviceName }) }
    var dataSaver by remember { mutableStateOf(prefs.dataSaver) }
    var podcastSpeed by remember { mutableFloatStateOf(prefs.podcastSpeed) }
    val speakerStatus by ui.runtime.speaker.status.collectAsState()
    // Android 13+: the media notification needs this permission to show (playback works without).
    val needsPermission = Build.VERSION.SDK_INT >= 33 &&
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
    var asked by remember { mutableStateOf(false) }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { asked = true }
    Column(Modifier.fillMaxSize()) {
        DetailHeader("Music & Podcasts", onBack = ui.nav.back)
        Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            if (needsPermission && !asked) {
                Card(Modifier.fillMaxWidth()) {
                    Column(Modifier.padding(12.dp)) {
                        Text("Allow notifications to get playback controls on the lock screen and in the notification shade.", style = MaterialTheme.typography.bodySmall)
                        TextButton(onClick = { permission.launch(Manifest.permission.POST_NOTIFICATIONS) }) { Text("Allow") }
                    }
                }
            }
            Text("Speaker", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary)
            SwitchLine("Let others play here", letOthers, "Other devices, and wake-up alarms, can play to this phone while the app is open or playing.") {
                letOthers = it
                ui.runtime.speaker.setEnabled(it)
            }
            speakerStatus?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            OutlinedTextField(
                value = name,
                onValueChange = { name = it },
                label = { Text("This phone's name") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
                trailingIcon = {
                    TextButton(onClick = { prefs.speakerName = name.trim(); ui.runtime.speaker.detailsChanged(); ui.message("Saved") }) { Text("Save") }
                },
            )
            SwitchLine("Allow announcements", announcements, "Doorbell and camera alerts are spoken here, the music ducked around them.") {
                announcements = it
                prefs.allowAnnouncements = it
                ui.runtime.speaker.detailsChanged()
            }
            HorizontalDivider()
            Text("Playback", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary)
            SwitchLine("Data saver on mobile data", dataSaver, "Streams music at ${com.sentinelcore.android.audio.AudioRuntime.DATA_SAVER_KBPS} kbps when not on Wi-Fi.") {
                dataSaver = it
                prefs.dataSaver = it
            }
            Text("Podcast speed for shows without their own: ${AudioFormat.speed(podcastSpeed.toDouble())}", style = MaterialTheme.typography.bodyMedium)
            Slider(value = podcastSpeed, onValueChange = { podcastSpeed = (it * 20).toInt() / 20f }, onValueChangeFinished = { prefs.podcastSpeed = podcastSpeed }, valueRange = 0.5f..3f)
            Link("Equalizer and Sound Check") { ui.nav.open(AudioRoutes.EQUALIZER) }
            Link("Downloads") { ui.nav.open(AudioRoutes.DOWNLOADS) }
            HorizontalDivider()
            Text("Your account", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary)
            Link("Wake-up alarms") { ui.nav.open(AudioRoutes.ALARMS) }
            Link("Morning Briefing") { ui.nav.open(AudioRoutes.BRIEFING) }
            Link("ListenBrainz scrobbling") { ui.nav.open(AudioRoutes.LISTENBRAINZ) }
            Link("Listening stats and Wrapped") { ui.nav.open(AudioRoutes.STATS) }
            if (ui.isAdmin) {
                HorizontalDivider()
                Text("Server (admin)", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary)
                Link("Music libraries") { ui.nav.open(AudioRoutes.MUSIC_LIBRARIES) }
                Link("Music and Podcasts server settings") { ui.nav.open("settings/server") }
            }
        }
    }
}

@Composable
private fun Link(label: String, onClick: () -> Unit) {
    TextButton(onClick = onClick, modifier = Modifier.fillMaxWidth()) { Text(label, modifier = Modifier.weight(1f)) }
}


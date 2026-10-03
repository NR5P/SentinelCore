package com.sentinelcore.android.ui.music

import android.content.Intent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.QueueMusic
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.DragHandle
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.outlined.PushPin
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.SwipeToDismissBoxValue
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.audio.AudioFormat
import com.sentinelcore.android.audio.MusicLibrary
import com.sentinelcore.android.audio.PlayContext
import com.sentinelcore.android.audio.PlaylistDetail
import com.sentinelcore.android.audio.SmartFieldKind
import com.sentinelcore.android.audio.SmartFields
import com.sentinelcore.android.audio.SmartRule
import com.sentinelcore.android.audio.SmartRules
import com.sentinelcore.android.audio.Track
import com.sentinelcore.android.ui.audio.ArtImage
import com.sentinelcore.android.ui.audio.HeroHeader
import com.sentinelcore.android.ui.audio.LoadableContent
import com.sentinelcore.android.ui.audio.LocalAudioUi
import com.sentinelcore.android.ui.audio.MosaicArt
import com.sentinelcore.android.ui.audio.PlayShuffleRow
import com.sentinelcore.android.ui.audio.TrackMenuTarget
import com.sentinelcore.android.ui.audio.TrackRow
import com.sentinelcore.android.ui.audio.rememberListReorderState
import com.sentinelcore.android.ui.audio.rememberLoadable
import com.sentinelcore.android.ui.audio.reorderHandle
import com.sentinelcore.android.ui.audio.reorderableRow
import com.sentinelcore.android.ui.screens.DetailHeader

// ---------------- a playlist ----------------

@Composable
fun PlaylistScreen(playlistId: String) {
    val ui = LocalAudioUi.current
    val detail = rememberLoadable(playlistId) { ui.api.playlist(playlistId) }
    var editing by remember { mutableStateOf(false) }
    var menu by remember { mutableStateOf(false) }
    var editDetails by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }
    val context = LocalContext.current
    val value = detail.value
    Column(Modifier.fillMaxSize()) {
        DetailHeader(value?.playlist?.name ?: "Playlist", onBack = ui.nav.back) {
            val playlist = value?.playlist
            if (playlist != null) {
                if (playlist.canEditTracks(ui.isAdmin) && value.tracks.isNotEmpty()) {
                    TextButton(onClick = { editing = !editing }) { Text(if (editing) "Done" else "Edit") }
                }
                IconButton(onClick = {
                    ui.launch { ui.api.pinPlaylist(playlist.id, !playlist.pinned); ui.libraryVersion++; ui.message(if (playlist.pinned) "Unpinned from Home" else "Pinned to Home") }
                }) { Icon(if (playlist.pinned) Icons.Default.PushPin else Icons.Outlined.PushPin, contentDescription = if (playlist.pinned) "Unpin" else "Pin to Home") }
                Box {
                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = "More") }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        if (playlist.canEditDetails(ui.isAdmin)) {
                            DropdownMenuItem(text = { Text("Edit details") }, onClick = { menu = false; editDetails = true })
                            if (playlist.smart) DropdownMenuItem(text = { Text("Edit rules") }, onClick = { menu = false; ui.nav.open("music/smart/${playlist.id}") })
                        }
                        DropdownMenuItem(text = { Text(if (playlist.smart) "Save today's songs as a playlist" else "Duplicate") }, onClick = {
                            menu = false
                            ui.launch("Couldn't duplicate it.") {
                                val copy = ui.api.duplicatePlaylist(playlist.id)
                                ui.libraryVersion++
                                ui.message("Made \"${copy.name}\"", action = "Open") { ui.nav.openPlaylist(copy.id) }
                            }
                        })
                        DropdownMenuItem(text = { Text("Add all to queue") }, onClick = { menu = false; ui.addToQueue(value.tracks) })
                        DropdownMenuItem(text = { Text("Export (.m3u8)") }, onClick = {
                            menu = false
                            ui.launch("Couldn't export it.") {
                                val text = ui.api.playlistM3u(playlist.id)
                                context.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_SUBJECT, "${playlist.name}.m3u8").putExtra(Intent.EXTRA_TEXT, text), "Export playlist"))
                            }
                        })
                        if (playlist.canEditDetails(ui.isAdmin)) {
                            DropdownMenuItem(text = { Text("Delete", color = MaterialTheme.colorScheme.error) }, onClick = { menu = false; confirmDelete = true })
                        }
                    }
                }
            }
        }
        LoadableContent(detail) { PlaylistContent(it, editing) }
    }
    val playlist = value?.playlist
    if (editDetails && playlist != null) {
        PlaylistDetailsDialog(playlist, onDismiss = { editDetails = false })
    }
    if (confirmDelete && playlist != null) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("Delete \"${playlist.name}\"?") },
            text = { Text(if (playlist.shared) "It's shared, so it goes for everyone in the household too. The songs stay in the library." else "The songs stay in the library.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmDelete = false
                    ui.launch("Couldn't delete it.") {
                        ui.api.deletePlaylist(playlist.id)
                        ui.libraryVersion++
                        ui.nav.back()
                    }
                }) { Text("Delete", color = MaterialTheme.colorScheme.error) }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Cancel") } },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PlaylistContent(detail: PlaylistDetail, editing: Boolean) {
    val ui = LocalAudioUi.current
    val playlist = detail.playlist
    val context = PlayContext("playlist", playlist.id, playlist.name)
    val canEdit = playlist.canEditTracks(ui.isAdmin)
    // The rows as shown: reordered locally while dragging, sent to the server on drop.
    val rows = remember(detail) { mutableStateListOf<Track>().apply { addAll(detail.tracks) } }
    val listState = rememberLazyListState()
    val reorder = rememberListReorderState(
        listState,
        onMove = { from, to ->
            val a = rows.indexOfFirst { it.entryId == from }
            val b = rows.indexOfFirst { it.entryId == to }
            if (a >= 0 && b >= 0) rows.add(b, rows.removeAt(a))
        },
        onDrop = {
            val order = rows.mapNotNull { it.entryId }
            if (order != detail.tracks.mapNotNull { it.entryId }) {
                ui.launch("Couldn't save the new order.") { ui.api.reorderPlaylist(playlist.id, order) }
            }
        },
    )
    LazyColumn(Modifier.fillMaxSize(), state = listState, contentPadding = PaddingValues(bottom = 24.dp)) {
        item(contentType = "header") {
            HeroHeader(
                art = { MosaicArt(playlist.artUrls, Modifier.fillMaxSize(), seed = playlist.name, sizePx = 700) },
                title = playlist.name,
                subtitle = when {
                    !playlist.mine -> "by ${playlist.ownerName ?: "someone else"}"
                    playlist.smart -> "Smart playlist"
                    else -> null
                },
                detail = listOfNotNull(
                    "${rows.size} songs, ${AudioFormat.duration(rows.sumOf { it.durationSeconds })}",
                    "Shared".takeIf { playlist.shared },
                    "Collaborative".takeIf { playlist.collaborative },
                ).joinToString(" · "),
            )
        }
        if (playlist.description.isNotBlank()) {
            item(contentType = "header") {
                Text(playlist.description, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 24.dp, vertical = 4.dp))
            }
        }
        playlist.rules?.let { rules ->
            item(contentType = "header") {
                Card(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp).clickable(enabled = playlist.canEditDetails(ui.isAdmin)) { ui.nav.open("music/smart/${playlist.id}") }) {
                    Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.Default.AutoAwesome, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                        Spacer(Modifier.width(10.dp))
                        Column(Modifier.weight(1f)) {
                            Text(rules.summary(), style = MaterialTheme.typography.bodyMedium)
                            Text(
                                "Sorted by ${SmartFields.sorts.firstOrNull { it.first == rules.sort }?.second?.lowercase() ?: rules.sort}" + if (rules.limit > 0) ", up to ${rules.limit} songs" else "",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }
            }
        }
        if (rows.isNotEmpty() && !editing) {
            item(contentType = "header") { PlayShuffleRow(onPlay = { ui.playTracks(rows.toList(), 0, context) }, onShuffle = { ui.playTracks(rows.toList(), 0, context, shuffle = true) }) }
            item(contentType = "header") {
                Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { ui.download(rows.toList()) }) {
                        Icon(Icons.Default.Download, contentDescription = null, modifier = Modifier.size(18.dp))
                        Spacer(Modifier.width(6.dp))
                        Text("Download")
                    }
                    OutlinedButton(onClick = { ui.engine.startRadio("playlist", playlist.id, playlist.name) }) { Text("Playlist radio") }
                }
            }
        }
        if (rows.isEmpty()) {
            item(contentType = "header") {
                com.sentinelcore.android.ui.audio.EmptyPanel(
                    Icons.AutoMirrored.Filled.QueueMusic,
                    if (playlist.smart) "No songs match yet" else "No songs yet",
                    if (playlist.smart) "As songs that match the rules are added or played, they show up here." else "Add songs from any song's menu: Add to playlist.",
                )
            }
        }
        itemsIndexed(rows, key = { index, track -> track.entryId ?: "$index${track.id}" }, contentType = { _, _ -> "track" }) { index, track ->
            val key = track.entryId ?: "$index${track.id}"
            if (editing && canEdit) {
                val dismiss = rememberSwipeToDismissBoxState(confirmValueChange = { valueChange ->
                    if (valueChange == SwipeToDismissBoxValue.EndToStart && track.entryId != null) {
                        rows.remove(track)
                        ui.launch("Couldn't remove it.") { ui.api.removeFromPlaylist(playlist.id, track.entryId) }
                        true
                    } else false
                })
                SwipeToDismissBox(
                    state = dismiss,
                    enableDismissFromStartToEnd = false,
                    modifier = Modifier.reorderableRow(reorder, key),
                    backgroundContent = {
                        Box(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.error).padding(horizontal = 24.dp), contentAlignment = Alignment.CenterEnd) {
                            Icon(Icons.Default.Delete, contentDescription = "Remove", tint = Color.White)
                        }
                    },
                ) {
                    Row(Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surface), verticalAlignment = Alignment.CenterVertically) {
                        IconButton(onClick = {
                            rows.remove(track)
                            track.entryId?.let { entry -> ui.launch("Couldn't remove it.") { ui.api.removeFromPlaylist(playlist.id, entry) } }
                        }) { Icon(Icons.Default.Close, contentDescription = "Remove ${track.title}", tint = MaterialTheme.colorScheme.error) }
                        Box(Modifier.weight(1f)) { TrackRow(track, onClick = {}) }
                        Icon(Icons.Default.DragHandle, contentDescription = "Drag to reorder", modifier = Modifier.reorderHandle(reorder, key).padding(14.dp))
                    }
                }
            } else {
                TrackRow(
                    track,
                    showAlbum = true,
                    onMore = { ui.trackMenu = TrackMenuTarget(track, playlist.id, track.entryId, canRemove = canEdit) },
                ) { ui.playTracks(rows.toList(), index, context) }
            }
        }
    }
}

@Composable
private fun PlaylistDetailsDialog(playlist: com.sentinelcore.android.audio.Playlist, onDismiss: () -> Unit) {
    val ui = LocalAudioUi.current
    var name by remember { mutableStateOf(playlist.name) }
    var description by remember { mutableStateOf(playlist.description) }
    var shared by remember { mutableStateOf(playlist.shared) }
    var collaborative by remember { mutableStateOf(playlist.collaborative) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Edit playlist") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(value = name, onValueChange = { name = it }, label = { Text("Name") }, singleLine = true, modifier = Modifier.fillMaxWidth())
                OutlinedTextField(value = description, onValueChange = { description = it }, label = { Text("Description") }, modifier = Modifier.fillMaxWidth())
                SwitchLine("Shared with the household", shared, "Everyone can see and play it.") { shared = it; if (!it) collaborative = false }
                if (!playlist.smart) SwitchLine("Collaborative", collaborative, "Everyone can also add, remove and reorder.") { collaborative = it; if (it) shared = true }
            }
        },
        confirmButton = {
            TextButton(enabled = name.isNotBlank(), onClick = {
                ui.launch("Couldn't save it.") {
                    ui.api.updatePlaylist(playlist.id, name.trim(), description.trim(), shared, collaborative, playlist.rules)
                    ui.libraryVersion++
                }
                onDismiss()
            }) { Text("Save") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

// ---------------- the smart playlist builder ----------------

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SmartPlaylistEditor(playlistId: String?) {
    val ui = LocalAudioUi.current
    var name by remember { mutableStateOf("") }
    var description by remember { mutableStateOf("") }
    var shared by remember { mutableStateOf(false) }
    var rules by remember { mutableStateOf(SmartRules(rules = listOf(SmartFields.defaultRule("genre")))) }
    var loaded by remember { mutableStateOf(playlistId == null) }
    var saving by remember { mutableStateOf(false) }
    var libraries by remember { mutableStateOf<List<MusicLibrary>>(emptyList()) }
    LaunchedEffect(playlistId) {
        runCatching { ui.api.musicLibraries() }.onSuccess { libraries = it }
        if (playlistId != null) {
            runCatching { ui.api.playlist(playlistId).playlist }.onSuccess { playlist ->
                name = playlist.name
                description = playlist.description
                shared = playlist.shared
                playlist.rules?.let { rules = it }
                loaded = true
            }.onFailure { ui.failed(it, "Couldn't open the playlist.") }
        }
    }
    Column(Modifier.fillMaxSize()) {
        DetailHeader(if (playlistId == null) "New smart playlist" else "Edit rules", onBack = ui.nav.back)
        if (!loaded) return@Column
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedTextField(value = name, onValueChange = { name = it }, label = { Text("Name") }, placeholder = { Text("Top rated rock") }, singleLine = true, modifier = Modifier.fillMaxWidth())
            OutlinedTextField(value = description, onValueChange = { description = it }, label = { Text("Description (optional)") }, modifier = Modifier.fillMaxWidth())
            Text("Songs that match", style = MaterialTheme.typography.titleMedium)
            SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                SegmentedButton(selected = rules.match == "all", onClick = { rules = rules.copy(match = "all") }, shape = SegmentedButtonDefaults.itemShape(0, 2)) { Text("All of these") }
                SegmentedButton(selected = rules.match == "any", onClick = { rules = rules.copy(match = "any") }, shape = SegmentedButtonDefaults.itemShape(1, 2)) { Text("Any of these") }
            }
            rules.rules.forEachIndexed { index, rule ->
                RuleEditor(
                    rule = rule,
                    libraries = libraries,
                    onChange = { changed -> rules = rules.copy(rules = rules.rules.toMutableList().also { it[index] = changed }) },
                    onRemove = { rules = rules.copy(rules = rules.rules.toMutableList().also { it.removeAt(index) }) },
                )
            }
            OutlinedButton(onClick = { rules = rules.copy(rules = rules.rules + SmartFields.defaultRule("rating")) }) {
                Icon(Icons.Default.Add, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text("Add a rule")
            }
            Text("Order and size", style = MaterialTheme.typography.titleMedium)
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                SmartFields.sorts.forEach { (key, label) ->
                    FilterChip(selected = rules.sort == key, onClick = { rules = rules.copy(sort = key) }, label = { Text(label) })
                }
            }
            if (rules.sort != "random") {
                SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                    SegmentedButton(selected = rules.order == "asc", onClick = { rules = rules.copy(order = "asc") }, shape = SegmentedButtonDefaults.itemShape(0, 2)) { Text("Ascending") }
                    SegmentedButton(selected = rules.order == "desc", onClick = { rules = rules.copy(order = "desc") }, shape = SegmentedButtonDefaults.itemShape(1, 2)) { Text("Descending") }
                }
            }
            OutlinedTextField(
                value = if (rules.limit == 0) "" else rules.limit.toString(),
                onValueChange = { text -> rules = rules.copy(limit = text.filter(Char::isDigit).take(4).toIntOrNull()?.coerceAtMost(SmartFields.MAX_LIMIT) ?: 0) },
                label = { Text("Most songs (blank for no limit)") },
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )
            SwitchLine("Shared with the household", shared) { shared = it }
            Text(rules.withoutBlankRules().summary(), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Button(
            enabled = name.isNotBlank() && !saving,
            onClick = {
                saving = true
                ui.launch("Couldn't save the smart playlist.") {
                    try {
                        val clean = rules.withoutBlankRules()
                        val saved = if (playlistId == null) ui.api.createPlaylist(name.trim(), description.trim(), shared, false, clean)
                        else ui.api.updatePlaylist(playlistId, name.trim(), description.trim(), shared, false, clean)
                        ui.libraryVersion++
                        ui.nav.back()
                        if (playlistId == null) ui.nav.openPlaylist(saved.id)
                    } finally {
                        saving = false
                    }
                }
            },
            modifier = Modifier.fillMaxWidth().padding(16.dp),
        ) { Text(if (playlistId == null) "Create" else "Save") }
    }
}

@Composable
private fun RuleEditor(rule: SmartRule, libraries: List<MusicLibrary>, onChange: (SmartRule) -> Unit, onRemove: () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Picker(SmartFields.label(rule.field), SmartFields.fields, Modifier.weight(1f)) { field -> onChange(SmartFields.defaultRule(field)) }
                Spacer(Modifier.width(8.dp))
                Picker(SmartFields.opLabel(rule.op), SmartFields.ops(rule.field).map { it to SmartFields.opLabel(it) }, Modifier.weight(1f)) { op -> onChange(rule.copy(op = op)) }
                IconButton(onClick = onRemove) { Icon(Icons.Default.Close, contentDescription = "Remove rule") }
            }
            when (SmartFields.kind(rule.field)) {
                SmartFieldKind.BOOL -> SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                    SegmentedButton(selected = rule.value == "true", onClick = { onChange(rule.copy(value = "true")) }, shape = SegmentedButtonDefaults.itemShape(0, 2)) { Text("Yes") }
                    SegmentedButton(selected = rule.value != "true", onClick = { onChange(rule.copy(value = "false")) }, shape = SegmentedButtonDefaults.itemShape(1, 2)) { Text("No") }
                }
                SmartFieldKind.CODEC -> Picker(rule.value.uppercase(), SmartFields.codecs.map { it to it.uppercase() }, Modifier.fillMaxWidth()) { onChange(rule.copy(value = it)) }
                SmartFieldKind.LIBRARY -> Picker(
                    libraries.firstOrNull { it.id == rule.value }?.name ?: "Choose a library",
                    libraries.map { it.id to it.name },
                    Modifier.fillMaxWidth(),
                ) { onChange(rule.copy(value = it)) }
                SmartFieldKind.NUMBER, SmartFieldKind.DAYS -> OutlinedTextField(
                    value = rule.value,
                    onValueChange = { onChange(rule.copy(value = it.filter { c -> c.isDigit() || c == '.' })) },
                    label = { Text(if (SmartFields.kind(rule.field) == SmartFieldKind.DAYS) "Days" else if (rule.field == "rating") "Stars (0-5)" else "Number") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
                SmartFieldKind.TEXT -> OutlinedTextField(
                    value = rule.value,
                    onValueChange = { onChange(rule.copy(value = it)) },
                    label = { Text(SmartFields.label(rule.field)) },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        }
    }
}

@Composable
private fun Picker(label: String, options: List<Pair<String, String>>, modifier: Modifier = Modifier, onPick: (String) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box(modifier) {
        OutlinedButton(onClick = { open = true }, modifier = Modifier.fillMaxWidth(), shape = RoundedCornerShape(8.dp)) {
            Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            options.forEach { (key, text) -> DropdownMenuItem(text = { Text(text) }, onClick = { open = false; onPick(key) }) }
        }
    }
}


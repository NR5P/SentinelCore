package com.sentinelcore.android.ui.organizer

import android.content.Intent
import android.os.Build
import android.provider.Settings
import android.text.format.Formatter
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sentinelcore.android.organizer.OfflinePolicy
import com.sentinelcore.android.organizer.OrganizerDevice
import com.sentinelcore.android.organizer.OrganizerEngine
import com.sentinelcore.android.organizer.OrganizerPerson
import com.sentinelcore.android.organizer.OrganizerSpace
import com.sentinelcore.android.organizer.OrganizerSpaceKind
import com.sentinelcore.android.ui.screens.DetailHeader
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import kotlinx.coroutines.launch

// "Organizer on this phone" in Settings: how much this phone keeps for use with no signal.
// Phones default light; computers (the Mac and Windows apps) keep everything, which is what lets
// them rebuild a replacement server.
@Composable
fun OrganizerOfflineSettings(engine: OrganizerEngine) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val policy by engine.policy.collectAsStateWithLifecycle()
    val sync by engine.syncState.collectAsStateWithLifecycle()
    rememberOrganizerRevision(engine)
    val summary = engine.storageSummary()
    // The restore line compares against the server's own count, read only when connecting.
    LaunchedEffect(Unit) { engine.refreshStatus() }

    fun set(newPolicy: OfflinePolicy) {
        OrganizerDevice.savePolicy(context, newPolicy)
        engine.setPolicy(newPolicy)
    }

    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        ChoiceMenu("Keep offline", when (policy.mode) { OfflinePolicy.Mode.OFF -> "Off"; OfflinePolicy.Mode.LIGHT -> "Light"; OfflinePolicy.Mode.FULL -> "Everything" },
            listOf(OfflinePolicy.Mode.OFF to "Off", OfflinePolicy.Mode.LIGHT to "Light", OfflinePolicy.Mode.FULL to "Everything")) { set(policy.copy(mode = it)) }
        if (policy.mode == OfflinePolicy.Mode.LIGHT) {
            ChoiceMenu("How far back", when (policy.windowDays) { 30 -> "30 days"; 90 -> "90 days"; 365 -> "1 year"; null -> "Everything"; else -> "${policy.windowDays} days" },
                listOf(30 to "30 days", 90 to "90 days", 365 to "1 year", null to "Everything")) { set(policy.copy(windowDays = it)) }
        }
        if (policy.mode != OfflinePolicy.Mode.OFF) {
            ChoiceMenu("Pictures & files", when (policy.attachments) { OfflinePolicy.Attachments.NONE -> "None"; OfflinePolicy.Attachments.THUMBNAILS -> "Thumbnails"; OfflinePolicy.Attachments.FULL -> "Full size" },
                listOf(OfflinePolicy.Attachments.NONE to "None", OfflinePolicy.Attachments.THUMBNAILS to "Thumbnails", OfflinePolicy.Attachments.FULL to "Full size")) { set(policy.copy(attachments = it)) }
            val gb = 1024L * 1024 * 1024
            ChoiceMenu("Storage limit", when (policy.storageCapBytes) { null -> "No limit"; 500L * 1024 * 1024 -> "500 MB"; 2 * gb -> "2 GB"; 10 * gb -> "10 GB"; else -> Formatter.formatShortFileSize(context, policy.storageCapBytes!!) },
                listOf(500L * 1024 * 1024 to "500 MB", 2 * gb to "2 GB", 10 * gb to "10 GB", null to "No limit")) { set(policy.copy(storageCapBytes = it)) }
        }
        Row {
            Text("On this phone", modifier = Modifier.weight(1f))
            Text("${summary.itemCount} items · ${Formatter.formatShortFileSize(context, summary.bytesOnDevice)}", color = SentinelTextDim)
        }
        summary.serverItemCount?.let { serverCount ->
            val complete = summary.itemCount >= serverCount && summary.attachmentsWithOriginal >= summary.attachmentCount
            Row {
                Text("Could restore", modifier = Modifier.weight(1f))
                Text(
                    if (complete) "Everything" else "${minOf(summary.itemCount, serverCount)} of $serverCount items · " +
                        "${summary.attachmentsWithOriginal} of ${summary.attachmentCount} files",
                    color = if (complete) SentinelGreen else SentinelTextDim,
                )
            }
        }
        if (sync.waitingChanges > 0 || summary.waitingUploads > 0) {
            Text("Waiting to sync: ${sync.waitingChanges} changes · ${summary.waitingUploads} files", color = SentinelAmber)
        }
        Text(
            when (policy.mode) {
                OfflinePolicy.Mode.OFF -> "Nothing is kept after the app closes; everything is read from the server. Anything you change with no signal is still kept until it's sent."
                OfflinePolicy.Mode.LIGHT -> "Keeps upcoming events, open reminders, boards, pinned notes and anything from the chosen window. Older things are still on the server and appear when you look while connected."
                OfflinePolicy.Mode.FULL -> "Keeps everything you can see, so this phone works fully offline - and if the server is ever lost, a new one can be filled back up from it."
            },
            style = MaterialTheme.typography.labelSmall, color = SentinelTextDim,
        )
        Row {
            TextButton(onClick = { scope.launch { engine.sync() } }) { Text("Sync now") }
            // Exact alarm timing needs the person's permission on Android 12+; without it alerts
            // arrive within a few minutes of their time.
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                val alarms = context.getSystemService(android.app.AlarmManager::class.java)
                if (!alarms.canScheduleExactAlarms()) {
                    TextButton(onClick = {
                        context.startActivity(Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, android.net.Uri.parse("package:${context.packageName}")))
                    }) { Text("Allow exact alert times") }
                }
            }
        }
    }
}

// ---- groups -----------------------------------------------------------------------------------

// Personal, Family and groups - who sees what. Groups need login on, since the server has to know
// who each person is.
@Composable
fun OrganizerGroupsScreen(engine: OrganizerEngine, onBack: () -> Unit) {
    val status by engine.status.collectAsStateWithLifecycle()
    val spaces by engine.spaces.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    var people by remember { mutableStateOf<List<OrganizerPerson>>(emptyList()) }
    var editing by remember { mutableStateOf<OrganizerSpace?>(null) }
    var creating by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) { people = runCatching { engine.people() }.getOrDefault(emptyList()) }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Spaces & groups", onBack)
        Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            if (status?.personalSpaces != true) {
                Text("Groups let you share notes, events, reminders and boards with only the people you pick.")
                Text("They need login turned on, with an account for each person, so the server knows who is who. An admin can turn it on under Settings → Dashboard login. Until then everything is shared with everyone as Family.",
                    color = SentinelTextDim)
                return@Column
            }
            spaces.ifEmpty { listOf(OrganizerSpace.FAMILY) }.forEach { space ->
                Card(Modifier.fillMaxWidth()) {
                    Row(Modifier.padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(10.dp).clip(CircleShape).background(spaceColor(engine, space.id)))
                        Column(Modifier.weight(1f).padding(start = 10.dp)) {
                            Text(space.name)
                            Text(when (space.kind) {
                                OrganizerSpaceKind.PERSONAL -> "Only you"
                                OrganizerSpaceKind.FAMILY -> "Everyone"
                                OrganizerSpaceKind.GROUP -> space.members.joinToString { member -> people.firstOrNull { it.username == member }?.label ?: member }
                            }, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                        }
                        if (space.kind == OrganizerSpaceKind.GROUP) TextButton(onClick = { editing = space }) { Text("Edit") }
                    }
                }
            }
            OutlinedButton(onClick = { creating = true }) { Text("New group") }
            Text("Personal is only you. Family is everyone with an account. Anyone in a group can change who's in it; deleting a group deletes everything in it.",
                style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
            error?.let { Text(it, color = SentinelRed) }
        }
    }

    if (creating || editing != null) {
        val group = editing
        var name by remember(group) { mutableStateOf(group?.name.orEmpty()) }
        var members by remember(group) { mutableStateOf(group?.members?.toSet() ?: setOfNotNull(status?.username)) }
        var confirmDelete by remember(group) { mutableStateOf(false) }
        AlertDialog(
            onDismissRequest = { creating = false; editing = null },
            title = { Text(if (group == null) "New group" else "Edit group") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    OutlinedTextField(name, { name = it }, label = { Text("Name, e.g. Parents") }, singleLine = true)
                    people.forEach { person ->
                        Row(Modifier.fillMaxWidth().clickable { members = if (person.username in members) members - person.username else members + person.username },
                            verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(checked = person.username in members, onCheckedChange = null)
                            Text(person.label + if (person.username == status?.username) " (you)" else "")
                        }
                    }
                    if (group != null) TextButton(onClick = { confirmDelete = true }) { Text("Delete group", color = SentinelRed) }
                }
            },
            confirmButton = {
                TextButton(enabled = name.isNotBlank(), onClick = {
                    scope.launch {
                        error = runCatching { engine.saveGroup(group?.id, name.trim(), members.toList(), group?.color) }.exceptionOrNull()?.message
                        creating = false
                        editing = null
                    }
                }) { Text("Save") }
            },
            dismissButton = { TextButton(onClick = { creating = false; editing = null }) { Text("Cancel") } },
        )
        if (confirmDelete && group != null) {
            AlertDialog(
                onDismissRequest = { confirmDelete = false },
                title = { Text("Delete \"${group.name}\" and everything in it, for everyone in the group?") },
                confirmButton = {
                    TextButton(onClick = {
                        scope.launch {
                            error = runCatching { engine.deleteGroup(group.id) }.exceptionOrNull()?.message
                            confirmDelete = false
                            editing = null
                        }
                    }) { Text("Delete", color = SentinelRed) }
                },
                dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Cancel") } },
            )
        }
    }
}

// ---- calendar subscription --------------------------------------------------------------------

@Composable
fun OrganizerSubscribeScreen(engine: OrganizerEngine, serverUrl: String, onBack: () -> Unit) {
    val context = LocalContext.current
    val clipboard = LocalClipboardManager.current
    val scope = rememberCoroutineScope()
    var path by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var confirmReset by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        runCatching { engine.calendarFeedPath() }.onSuccess { path = it }.onFailure { error = "Couldn't get a link - is the server reachable?" }
    }
    val url = path?.let { serverUrl.trimEnd('/') + it }

    Column(Modifier.fillMaxSize()) {
        DetailHeader("Subscribe in another calendar", onBack)
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("Add this link as a subscribed calendar in Google Calendar, Apple Calendar or Outlook to see your events there. It's read-only on their side - edits still happen here.")
            when {
                url != null -> {
                    Text(url, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall, color = SentinelCyan)
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedButton(onClick = { clipboard.setText(AnnotatedString(url)) }) { Text("Copy") }
                        OutlinedButton(onClick = {
                            context.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, url), "Share calendar link"))
                        }) { Text("Share") }
                    }
                }
                error != null -> Text(error!!, color = SentinelRed)
                else -> Text("Getting a link…", color = SentinelTextDim)
            }
            Text("Anyone with this link can see your events, so treat it like a password. The other calendar has to be able to reach this server - from outside your home, that means the remote address. Resetting makes the old link stop working.",
                style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
            TextButton(onClick = { confirmReset = true }) { Text("Reset link", color = SentinelRed) }
        }
    }
    if (confirmReset) AlertDialog(
        onDismissRequest = { confirmReset = false },
        title = { Text("Make a new link? Calendars subscribed to the old one stop updating.") },
        confirmButton = {
            TextButton(onClick = {
                confirmReset = false
                scope.launch { runCatching { engine.calendarFeedPath(reset = true) }.onSuccess { path = it } }
            }) { Text("Reset", color = SentinelRed) }
        },
        dismissButton = { TextButton(onClick = { confirmReset = false }) { Text("Cancel") } },
    )
}

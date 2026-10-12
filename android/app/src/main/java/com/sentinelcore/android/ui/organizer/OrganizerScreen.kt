package com.sentinelcore.android.ui.organizer

import androidx.compose.material.icons.filled.Star
import androidx.compose.material.icons.outlined.StarOutline
import androidx.compose.material3.Switch
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.text.font.FontStyle
import com.sentinelcore.android.organizer.Holiday
import com.sentinelcore.android.organizer.HolidayColors
import com.sentinelcore.android.organizer.HolidayState
import android.content.ClipData
import android.content.ClipDescription
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.draganddrop.dragAndDropSource
import androidx.compose.foundation.draganddrop.dragAndDropTarget
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.ui.draganddrop.DragAndDropEvent
import androidx.compose.ui.draganddrop.DragAndDropTarget
import androidx.compose.ui.draganddrop.DragAndDropTransferData
import androidx.compose.ui.draganddrop.mimeTypes
import androidx.compose.ui.draganddrop.toAndroidDragEvent
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Checklist
import androidx.compose.material.icons.filled.ChevronLeft
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.CloudOff
import androidx.compose.material.icons.filled.Flag
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.RadioButtonUnchecked
import androidx.compose.material.icons.filled.Repeat
import androidx.compose.material.icons.outlined.ChatBubbleOutline
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.SmallFloatingActionButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.sentinelcore.android.organizer.NoteChecklist
import com.sentinelcore.android.organizer.OrganizerDates
import com.sentinelcore.android.organizer.OrganizerEngine
import com.sentinelcore.android.organizer.OrganizerItem
import com.sentinelcore.android.organizer.OrganizerKinds
import com.sentinelcore.android.organizer.VoiceCapture
import com.sentinelcore.android.organizer.OrganizerNavigation
import com.sentinelcore.android.organizer.Recurrence
import com.sentinelcore.android.ui.theme.SentinelAmber
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelGreen
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import java.time.DayOfWeek
import java.time.LocalDate
import java.time.YearMonth
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.time.format.TextStyle
import java.util.Locale
import kotlinx.coroutines.launch

// The Organizer tab: calendar, reminders, notes and boards over this phone's copy, with a filter
// for which space to show. Every view reads the engine, so an edit from another device shows up as
// soon as it syncs in.

class OrganizerRoutes(
    val openEvent: (itemId: String?, day: LocalDate?, originalStart: String?) -> Unit,
    val openReminder: (itemId: String?) -> Unit,
    val openNote: (itemId: String?) -> Unit,
    // A new note that opens straight on its check-off view, ready for a title and items.
    val openChecklist: () -> Unit,
    val openCard: (itemId: String) -> Unit,
    val openGroups: () -> Unit,
    val openSubscribe: () -> Unit,
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun OrganizerScreen(engine: OrganizerEngine, routes: OrganizerRoutes) {
    var pane by rememberSaveable { mutableStateOf(0) }
    // Opened from a home-screen widget: show the pane it was for.
    val requestedPane by OrganizerNavigation.pane.collectAsStateWithLifecycle()
    LaunchedEffect(requestedPane) {
        when (requestedPane) {
            "calendar" -> pane = 0
            "reminders" -> pane = 1
            "notes" -> pane = 2
        }
        OrganizerNavigation.pane.value = null
    }
    var spaceFilter by rememberSaveable { mutableStateOf<String?>(null) }
    var menu by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    rememberOrganizerRevision(engine)
    val filter = spaceFilter?.takeIf { engine.space(it) != null }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Organizer") },
                actions = {
                    Box {
                        TextButton(onClick = { menu = true }) {
                            Icon(Icons.Default.FilterList, contentDescription = null)
                            Spacer(Modifier.width(6.dp))
                            Text(filter?.let { engine.space(it)?.name } ?: "Everything")
                        }
                        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                            DropdownMenuItem(text = { Text("Everything") }, onClick = { spaceFilter = null; menu = false })
                            engine.visibleSpaces.forEach { space ->
                                DropdownMenuItem(text = { Text(space.name) }, onClick = { spaceFilter = space.id; menu = false })
                            }
                            HorizontalDivider()
                            DropdownMenuItem(text = { Text("Groups…") }, onClick = { menu = false; routes.openGroups() })
                            DropdownMenuItem(text = { Text("Subscribe in another calendar…") }, onClick = { menu = false; routes.openSubscribe() })
                            DropdownMenuItem(text = { Text("Sync now") }, onClick = { menu = false; scope.launch { engine.sync() } })
                        }
                    }
                },
            )
        },
        floatingActionButton = {
            // Boards make their own boards, columns and cards inline.
            if (pane != 3) {
                Column(horizontalAlignment = Alignment.End, verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    // Notes get a second, smaller button for a checklist - a shopping or to-do list
                    // without typing any "- [ ]".
                    if (pane == 2) {
                        SmallFloatingActionButton(onClick = routes.openChecklist) {
                            Icon(Icons.Default.Checklist, contentDescription = "New checklist")
                        }
                    }
                    FloatingActionButton(onClick = {
                        when (pane) {
                            0 -> routes.openEvent(null, LocalDate.now(), null)
                            1 -> routes.openReminder(null)
                            else -> routes.openNote(null)
                        }
                    // Explicit dark content: with dynamic colour on, SentinelGreen isn't the scheme's
                    // primary, so the default content colour fell back to a light grey on the mint.
                    }, containerColor = SentinelGreen, contentColor = Color.Black) {
                        Icon(Icons.Default.Add, contentDescription = listOf("New event", "New reminder", "New note")[pane])
                    }
                }
            }
        },
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {
            TabRow(selectedTabIndex = pane) {
                listOf("Calendar", "Reminders", "Notes", "Boards").forEachIndexed { index, title ->
                    Tab(selected = pane == index, onClick = { pane = index }, text = { Text(title, maxLines = 1) })
                }
            }
            SyncBanner(engine)
            when (pane) {
                0 -> CalendarPane(engine, filter, routes)
                1 -> RemindersPane(engine, filter, routes)
                2 -> NotesPane(engine, filter, routes)
                else -> BoardsPane(engine, filter, routes)
            }
        }
    }
    ResetPromptDialog(engine)
}

// One line under the tabs, only when there's something to say.
@Composable
fun SyncBanner(engine: OrganizerEngine) {
    val state by engine.syncState.collectAsStateWithLifecycle()
    val message = when {
        state.needsSignIn -> "Sign in again to sync the Organizer."
        !state.online && (engine.isConnected || state.waitingChanges > 0) ->
            if (state.waitingChanges > 0) "Offline - ${state.waitingChanges} change${if (state.waitingChanges == 1) "" else "s"} saved on this phone, will sync when the server is back"
            else "Offline - showing this phone's copy"
        else -> state.lastError
    } ?: return
    val color = if (state.online) SentinelRed else SentinelAmber
    Row(
        Modifier.fillMaxWidth().background(color.copy(alpha = 0.12f)).padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Icons.Default.CloudOff, contentDescription = null, tint = color, modifier = Modifier.size(16.dp))
        Spacer(Modifier.width(8.dp))
        Text(message, color = color, style = MaterialTheme.typography.labelMedium, modifier = Modifier.weight(1f))
        if (state.lastError != null && state.online) TextButton(onClick = engine::clearError) { Text("OK") }
    }
}

// The server answering isn't the one this phone last synced with. Nothing syncs until answered.
// "Not now" (or Back) is remembered for this server; Settings > Organizer brings the question back.
@Composable
fun ResetPromptDialog(engine: OrganizerEngine) {
    val prompt by engine.resetPrompt.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    val current = prompt ?: return
    AlertDialog(
        onDismissRequest = { engine.declineReset() },
        title = { Text("This server doesn't have your Organizer data") },
        text = {
            Text("This phone has ${current.localCount} Organizer items from a server that isn't the one answering now" +
                (current.serverCount?.let { " (it has $it)" } ?: "") +
                ". If the old server was lost or reinstalled, upload this copy to fill the new one back up - anything already on " +
                "the new server is kept, and newer edits win. Using the server's copy replaces what's on this phone. " +
                "Not now keeps this phone's copy and pauses Organizer sync until you decide in Settings.")
        },
        confirmButton = { TextButton(onClick = { scope.launch { engine.resolveReset(upload = true) } }) { Text("Upload this phone's copy") } },
        dismissButton = {
            Row {
                TextButton(onClick = { engine.declineReset() }) { Text("Not now") }
                TextButton(onClick = { scope.launch { engine.resolveReset(upload = false) } }) { Text("Use the server's copy", color = SentinelRed) }
            }
        },
    )
}

// ---- calendar ---------------------------------------------------------------------------------

// An event occurrence, a dated reminder or a holiday. Holidays aren't Organizer items.
private data class CalendarEntry(
    val item: OrganizerItem?,
    val start: java.time.ZonedDateTime,
    val end: java.time.ZonedDateTime,
    val originalStart: String?,
    val isReminder: Boolean,
    val holiday: Holiday? = null,
) {
    val title: String get() = holiday?.name ?: ((if (isReminder) "☐ " else "") + (item?.title?.ifEmpty { null } ?: "Untitled"))

    @Composable
    fun color(engine: OrganizerEngine): Color = when {
        holiday != null -> Color(HolidayColors.argb(holiday.setId))
        isReminder -> SentinelAmber
        else -> spaceColor(engine, item!!.spaceId)
    }
}

private fun entriesBetween(engine: OrganizerEngine, filter: String?, from: java.time.ZonedDateTime, to: java.time.ZonedDateTime): List<CalendarEntry> {
    val out = mutableListOf<CalendarEntry>()
    // Holidays first on each day, and whatever space is picked - they belong to none.
    engine.holidaysBetween(from, to).forEach { (holiday, start, end) -> out += CalendarEntry(null, start, end, null, false, holiday) }
    engine.items(OrganizerKinds.EVENT).filter { filter == null || it.spaceId == filter }.forEach { event ->
        Recurrence.occurrences(event, from, to).forEach { out += CalendarEntry(event, it.start, it.end, it.originalStart, false) }
    }
    engine.items(OrganizerKinds.REMINDER).filter { it.parentId == null && !it.bool("completed") && (filter == null || it.spaceId == filter) }.forEach { reminder ->
        val due = OrganizerDates.parse(reminder.string("due"), reminder.bool("allDay")) ?: return@forEach
        if (!due.isBefore(from) && due.isBefore(to)) out += CalendarEntry(reminder, due, due, null, true)
    }
    return out.sortedWith(compareBy({ it.start }, { if (it.holiday != null) 0 else 1 }))
}

@Composable
private fun CalendarPane(engine: OrganizerEngine, filter: String?, routes: OrganizerRoutes) {
    var month by rememberSaveable { mutableStateOf(YearMonth.now().toString()) }
    var selected by rememberSaveable { mutableStateOf(LocalDate.now().toString()) }
    val holidayState by engine.holidays.collectAsStateWithLifecycle()
    var showingHolidaySettings by remember { mutableStateOf(false) }
    var viewingHoliday by remember { mutableStateOf<Holiday?>(null) }
    val shown = YearMonth.parse(month)
    val selectedDay = LocalDate.parse(selected)
    val zone = ZoneId.systemDefault()
    val gridStart = shown.atDay(1).let { it.minusDays((it.dayOfWeek.value % 7).toLong()) }
    val entries = entriesBetween(engine, filter, gridStart.atStartOfDay(zone), gridStart.plusDays(42).atStartOfDay(zone))
    val byDay = HashMap<LocalDate, MutableList<CalendarEntry>>()
    entries.forEach { entry ->
        var day = entry.start.toLocalDate()
        val last = if (entry.end.isAfter(entry.start)) entry.end.minusSeconds(1).toLocalDate() else day
        var guard = 0
        while (!day.isAfter(last) && guard++ < 60) {
            byDay.getOrPut(day) { mutableListOf() } += entry
            day = day.plusDays(1)
        }
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(12.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { month = shown.minusMonths(1).toString() }) { Icon(Icons.Default.ChevronLeft, contentDescription = "Previous month") }
            Text(shown.month.getDisplayName(TextStyle.FULL, Locale.getDefault()) + " " + shown.year,
                style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
            TextButton(onClick = { month = YearMonth.now().toString(); selected = LocalDate.now().toString() }) { Text("Today") }
            IconButton(onClick = { month = shown.plusMonths(1).toString() }) { Icon(Icons.Default.ChevronRight, contentDescription = "Next month") }
            IconButton(onClick = { showingHolidaySettings = true }) {
                Icon(if (holidayState.sets.isEmpty()) Icons.Outlined.StarOutline else Icons.Filled.Star,
                    contentDescription = "Holiday calendars", tint = SentinelGreen)
            }
        }
        Row {
            (0..6).forEach { i ->
                Text(DayOfWeek.of(if (i == 0) 7 else i).getDisplayName(TextStyle.NARROW, Locale.getDefault()),
                    modifier = Modifier.weight(1f), style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
            }
        }
        Column(Modifier.clip(RoundedCornerShape(8.dp)).border(0.5.dp, SentinelTextDim.copy(alpha = 0.3f), RoundedCornerShape(8.dp))) {
            (0 until 6).forEach { week ->
                // Intrinsic height so a busy day stretches its whole week, not just its own cell.
                Row(Modifier.height(IntrinsicSize.Min)) {
                    (0 until 7).forEach { weekday ->
                        val day = gridStart.plusDays((week * 7 + weekday).toLong())
                        val dayEntries = byDay[day].orEmpty()
                        val isToday = day == LocalDate.now()
                        Column(
                            Modifier.weight(1f).fillMaxHeight().heightIn(min = 64.dp)
                                .background(if (day == selectedDay) SentinelGreen.copy(alpha = 0.12f) else Color.Transparent)
                                .border(0.25.dp, SentinelTextDim.copy(alpha = 0.2f))
                                .clickable { selected = day.toString() }
                                .padding(2.dp),
                        ) {
                            Text(
                                day.dayOfMonth.toString(),
                                style = MaterialTheme.typography.labelSmall,
                                fontWeight = if (isToday) FontWeight.Bold else null,
                                color = when {
                                    isToday -> SentinelGreen
                                    day.month != shown.month -> SentinelTextDim
                                    else -> Color.Unspecified
                                },
                            )
                            dayEntries.take(3).forEach { entry ->
                                val color = entry.color(engine)
                                Text(
                                    entry.title,
                                    fontSize = 9.sp, maxLines = 1, overflow = TextOverflow.Ellipsis,
                                    fontStyle = if (entry.holiday != null) FontStyle.Italic else null,
                                    modifier = Modifier.fillMaxWidth().padding(top = 1.dp).background(color.copy(alpha = 0.18f), RoundedCornerShape(3.dp)).padding(horizontal = 2.dp),
                                )
                            }
                            if (dayEntries.size > 3) Text("+${dayEntries.size - 3}", fontSize = 9.sp, color = SentinelTextDim)
                        }
                    }
                }
            }
        }

        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(selectedDay.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.FULL)), color = SentinelCyan, modifier = Modifier.weight(1f))
                    TextButton(onClick = { routes.openEvent(null, selectedDay, null) }) { Text("Add event") }
                }
                val dayEntries = byDay[selectedDay].orEmpty()
                if (dayEntries.isEmpty()) Text("Nothing on this day", color = SentinelTextDim)
                dayEntries.forEach { entry ->
                    Row(
                        Modifier.fillMaxWidth().clickable {
                            when {
                                entry.holiday != null -> viewingHoliday = entry.holiday
                                entry.isReminder -> routes.openReminder(entry.item!!.id)
                                else -> routes.openEvent(entry.item!!.id, null, entry.originalStart)
                            }
                        }.padding(vertical = 6.dp),
                        horizontalArrangement = Arrangement.spacedBy(10.dp),
                    ) {
                        Box(Modifier.width(4.dp).height(36.dp).background(entry.color(engine)))
                        val item = entry.item
                        if (entry.holiday != null || item == null) {
                            val holiday = entry.holiday
                            Column(Modifier.weight(1f)) {
                                Text(entry.title, fontStyle = FontStyle.Italic)
                                Text(holiday?.note ?: if ((holiday?.dayCount ?: 1) > 1) "${holiday?.dayCount} days" else "All day",
                                    style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                            }
                            if (holiday != null) Text(engine.holidaySetName(holiday.setId), style = MaterialTheme.typography.labelSmall,
                                color = Color(HolidayColors.argb(holiday.setId)))
                            return@Row
                        }
                        Column(Modifier.weight(1f)) {
                            Text(entry.title)
                            val time = when {
                                item.bool("allDay") -> "All day"
                                entry.isReminder -> "Due " + entry.start.toLocalTime().format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT))
                                else -> entry.start.toLocalTime().format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)) + " – " +
                                    entry.end.toLocalTime().format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT))
                            }
                            Text(time + (item.string("location")?.takeIf { it.isNotBlank() }?.let { " · $it" } ?: ""),
                                style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                        }
                        SpaceTag(engine, item.spaceId)
                    }
                    HorizontalDivider()
                }
            }
        }
        Spacer(Modifier.height(72.dp))
    }

    if (showingHolidaySettings) HolidaySettingsDialog(engine, holidayState) { showingHolidaySettings = false }
    viewingHoliday?.let { holiday ->
        AlertDialog(
            onDismissRequest = { viewingHoliday = null },
            title = { Text(holiday.name) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    val start = runCatching { LocalDate.parse(holiday.date) }.getOrNull()
                    val formatter = DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM)
                    if (start != null) {
                        Text(if (holiday.dayCount > 1) "${start.format(formatter)} – ${start.plusDays(holiday.dayCount - 1L).format(formatter)}" else start.format(formatter))
                    }
                    holiday.note?.let { Text(it) }
                    Text("From ${engine.holidaySetName(holiday.setId)}. Holidays can't be edited - turn the whole calendar off in holiday settings if you don't want it.",
                        style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                }
            },
            confirmButton = { TextButton(onClick = { viewingHoliday = null }) { Text("Done") } },
            dismissButton = { TextButton(onClick = { viewingHoliday = null; showingHolidaySettings = true }) { Text("Holiday settings") } },
        )
    }
}

// Which holiday calendars to show. All off until someone turns one on; the choice follows the
// account, so it applies on every device.
@Composable
private fun HolidaySettingsDialog(engine: OrganizerEngine, state: HolidayState, onDismiss: () -> Unit) {
    LaunchedEffect(Unit) { if (state.catalog == null) engine.refreshHolidays(force = true) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Holidays") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text("Holidays show on the calendar here and on your other devices, and in the calendar subscription link.",
                    style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                val catalog = state.catalog
                if (catalog == null) {
                    Text("Connect to the server once to see the holiday calendars.", color = SentinelTextDim)
                } else {
                    catalog.forEach { set ->
                        val on = set.id in state.sets
                        Row(
                            Modifier.fillMaxWidth().clickable {
                                engine.setHolidaySets(if (on) state.sets - set.id else state.sets + set.id)
                            }.padding(vertical = 6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            Box(Modifier.size(10.dp).background(Color(HolidayColors.argb(set.id)), CircleShape))
                            Column(Modifier.weight(1f)) {
                                Text(set.name)
                                Text(set.description, style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                            }
                            Switch(checked = on, onCheckedChange = { checked ->
                                engine.setHolidaySets(if (checked) state.sets + set.id else state.sets - set.id)
                            })
                        }
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Done") } },
    )
}

// ---- reminders --------------------------------------------------------------------------------

fun completeReminder(engine: OrganizerEngine, reminder: OrganizerItem, done: Boolean) {
    // A repeating reminder moves on to its next date instead - same as the Apple apps and the
    // dashboard, and Apple Reminders.
    val next = if (done) Recurrence.nextDue(reminder) else null
    if (next != null) {
        engine.update(reminder.id, mapOf("due" to next, "completed" to false, "lastCompletedAt" to OrganizerDates.timestamp()))
    } else {
        engine.update(reminder.id, mapOf("completed" to done, "completedAt" to if (done) OrganizerDates.timestamp() else null))
    }
}

@Composable
private fun RemindersPane(engine: OrganizerEngine, filter: String?, routes: OrganizerRoutes) {
    var newTitle by remember { mutableStateOf("") }
    var showDone by rememberSaveable { mutableStateOf(false) }
    val now = java.time.ZonedDateTime.now()
    val reminders = engine.items(OrganizerKinds.REMINDER).filter { it.parentId == null && (filter == null || it.spaceId == filter) }
    fun due(item: OrganizerItem) = OrganizerDates.parse(item.string("due"), item.bool("allDay"))
    fun overdue(item: OrganizerItem): Boolean {
        val due = due(item) ?: return false
        return if (item.bool("allDay")) due.toLocalDate().isBefore(LocalDate.now()) else due.isBefore(now)
    }
    val open = reminders.filterNot { it.bool("completed") }
    val groups = listOf(
        "Overdue" to open.filter(::overdue),
        "Today" to open.filter { !overdue(it) && due(it)?.toLocalDate() == LocalDate.now() },
        "Upcoming" to open.filter { !overdue(it) && due(it)?.toLocalDate()?.isAfter(LocalDate.now()) == true },
        "No date" to open.filter { due(it) == null }.sortedBy { it.sortPosition },
    ).map { (title, list) -> title to if (title == "No date") list else list.sortedWith(compareBy({ due(it)?.toInstant() }, { -(it.number("priority") ?: 0.0) })) }
    val done = reminders.filter { it.bool("completed") }.sortedByDescending { it.string("completedAt") }

    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        item {
            OutlinedTextField(
                value = newTitle,
                onValueChange = { newTitle = it },
                label = { Text("New reminder") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
                // Or say it: "add a reminder to walk the dog tomorrow at 1". The phone's speech
                // recognizer listens (the same path as the launcher shortcut, see the app shell),
                // VoicePhrase works out the date, and the reminder is added.
                leadingIcon = {
                    IconButton(onClick = { VoiceCapture.pending.value = VoiceCapture.Request(OrganizerKinds.REMINDER, null) }) {
                        Icon(Icons.Default.Mic, contentDescription = "Speak a reminder", tint = SentinelCyan)
                    }
                },
                trailingIcon = {
                    TextButton(enabled = newTitle.isNotBlank(), onClick = {
                        engine.create(OrganizerKinds.REMINDER, mapOf("title" to newTitle.trim(), "completed" to false), spaceId = filter ?: engine.defaultSpaceId)
                        newTitle = ""
                    }) { Text("Add") }
                },
            )
        }
        groups.forEach { (title, list) ->
            if (list.isNotEmpty()) {
                item { Text(title, color = if (title == "Overdue") SentinelRed else SentinelCyan, style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(top = 8.dp)) }
                items(list, key = { it.id }) { ReminderRow(engine, it, overdue(it), routes) }
            }
        }
        if (open.isEmpty()) item { Text("Nothing to do", color = SentinelTextDim, modifier = Modifier.padding(24.dp)) }
        if (done.isNotEmpty()) {
            item { TextButton(onClick = { showDone = !showDone }) { Text((if (showDone) "Hide" else "Show") + " completed (${done.size})") } }
            if (showDone) items(done.take(200), key = { it.id }) { ReminderRow(engine, it, false, routes) }
        }
        item { Spacer(Modifier.height(72.dp)) }
    }
}

@Composable
private fun ReminderRow(engine: OrganizerEngine, reminder: OrganizerItem, overdue: Boolean, routes: OrganizerRoutes) {
    val done = reminder.bool("completed")
    val steps = engine.children(reminder.id, OrganizerKinds.REMINDER)
    Row(Modifier.fillMaxWidth().clickable { routes.openReminder(reminder.id) }.padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = { completeReminder(engine, reminder, !done) }) {
            Icon(if (done) Icons.Default.CheckCircle else Icons.Default.RadioButtonUnchecked,
                contentDescription = if (done) "Mark not done" else "Mark done", tint = if (done) SentinelGreen else SentinelTextDim)
        }
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                val priority = reminder.number("priority")?.toInt() ?: 0
                if (priority > 0) Text("!".repeat(priority) + " ", color = SentinelAmber, fontWeight = FontWeight.Bold)
                Text(reminder.title.ifEmpty { "Reminder" }, textDecoration = if (done) TextDecoration.LineThrough else null,
                    color = if (done) SentinelTextDim else Color.Unspecified)
                if (reminder.bool("flagged")) Icon(Icons.Default.Flag, contentDescription = "Flagged", tint = SentinelAmber, modifier = Modifier.size(14.dp))
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                OrganizerDates.parse(reminder.string("due"), reminder.bool("allDay"))?.let { due ->
                    val day = when (due.toLocalDate()) {
                        LocalDate.now() -> "Today"
                        LocalDate.now().plusDays(1) -> "Tomorrow"
                        else -> due.toLocalDate().format(DateTimeFormatter.ofPattern("EEE, MMM d"))
                    }
                    Text(if (reminder.bool("allDay")) day else day + " " + due.toLocalTime().format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)),
                        style = MaterialTheme.typography.labelSmall, color = if (overdue) SentinelRed else SentinelTextDim)
                }
                if (reminder.string("recurrence") != null) Icon(Icons.Default.Repeat, contentDescription = "Repeats", tint = SentinelTextDim, modifier = Modifier.size(12.dp))
                if (steps.isNotEmpty()) Text("${steps.count { it.bool("completed") }}/${steps.size} steps", style = MaterialTheme.typography.labelSmall, color = SentinelTextDim)
                if (engine.children(reminder.id, OrganizerKinds.ATTACHMENT).isNotEmpty()) Icon(Icons.Default.AttachFile, contentDescription = null, tint = SentinelTextDim, modifier = Modifier.size(12.dp))
                SpaceTag(engine, reminder.spaceId)
            }
        }
    }
}

// ---- notes ------------------------------------------------------------------------------------

@Composable
private fun NotesPane(engine: OrganizerEngine, filter: String?, routes: OrganizerRoutes) {
    var search by rememberSaveable { mutableStateOf("") }
    val scope = rememberCoroutineScope()
    val policy by engine.policy.collectAsStateWithLifecycle()
    val notes = engine.items(OrganizerKinds.NOTE)
        .filter { (filter == null || it.spaceId == filter) && !it.bool("archived") }
        .filter { search.isBlank() || "${it.title}\n${it.string("body").orEmpty()}".contains(search, ignoreCase = true) }
        .sortedWith(compareByDescending<OrganizerItem> { it.bool("pinned") }.thenByDescending { it.updatedAt })
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        item {
            OutlinedTextField(value = search, onValueChange = { search = it }, label = { Text("Search notes") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        }
        items(notes, key = { it.id }) { note ->
            Card(Modifier.fillMaxWidth().clickable { routes.openNote(note.id) }) {
                Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        if (note.bool("pinned")) Icon(Icons.Default.PushPin, contentDescription = "Pinned", tint = SentinelAmber, modifier = Modifier.size(14.dp))
                        Text(note.title.ifEmpty { "Untitled" }, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                        val progress = NoteChecklist.progress(note.string("body").orEmpty())
                        if (progress.total > 0) Text("☑ ${progress.done}/${progress.total}", style = MaterialTheme.typography.labelSmall,
                            color = if (progress.done == progress.total) SentinelGreen else SentinelTextDim, modifier = Modifier.padding(horizontal = 4.dp))
                        if (engine.children(note.id, OrganizerKinds.ATTACHMENT).isNotEmpty()) Icon(Icons.Default.AttachFile, contentDescription = null, tint = SentinelTextDim, modifier = Modifier.size(14.dp))
                    }
                    Text(note.string("body").orEmpty().replace("- [ ] ", "☐ ").replace("- [x] ", "☑ ").replace("#", "").trim(),
                        style = MaterialTheme.typography.bodySmall, color = SentinelTextDim, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    SpaceTag(engine, note.spaceId)
                }
            }
        }
        if (notes.isEmpty()) item { Text(if (search.isBlank()) "No notes yet" else "No matches", color = SentinelTextDim, modifier = Modifier.padding(24.dp)) }
        if (policy.mode != com.sentinelcore.android.organizer.OfflinePolicy.Mode.FULL) {
            item { TextButton(onClick = { scope.launch { runCatching { engine.loadArchive(OrganizerKinds.NOTE) } } }) { Text("Show notes not kept on this phone") } }
        }
        item { Spacer(Modifier.height(72.dp)) }
    }
}

// ---- boards -----------------------------------------------------------------------------------

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun BoardsPane(engine: OrganizerEngine, filter: String?, routes: OrganizerRoutes) {
    var selectedId by rememberSaveable { mutableStateOf<String?>(null) }
    var naming by remember { mutableStateOf<Pair<String, String?>?>(null) } // (kind of naming, id)
    var nameText by remember { mutableStateOf("") }
    var confirmDelete by remember { mutableStateOf(false) }
    var automationsFor by remember { mutableStateOf<OrganizerItem?>(null) }
    // The card whose comments-only sheet is open. Held here rather than on the tile so the sheet
    // stays open when a sync moves the card to another column (which redraws its tile).
    var commentsFor by rememberSaveable { mutableStateOf<String?>(null) }
    val boards = engine.items(OrganizerKinds.BOARD).filter { (filter == null || it.spaceId == filter) && !it.bool("archived") }.sortedBy { it.sortPosition }
    val board = boards.firstOrNull { it.id == selectedId } ?: boards.firstOrNull()

    val scope = rememberCoroutineScope()
    val zones = remember { DropZones() }
    // One drop target for the whole screen, working out what's under the finger from where each
    // chip, column and card was last laid out. Compose only offers a drag to targets that existed
    // when it started, so per-column targets would miss the columns of a board opened mid-drag.
    val paneTarget = remember {
        object : DragAndDropTarget {
            var opener: Job? = null
            var openerFor: String? = null
            fun stop() { opener?.cancel(); opener = null; openerFor = null; zones.hovered = null }
            override fun onMoved(event: DragAndDropEvent) {
                val hit = zones.hit(event.position())
                zones.hovered = hit
                // Held over a board's chip a moment, the board opens so the card can go in any column.
                val chip = (hit as? DropHit.Chip)?.boardId
                if (chip != openerFor) {
                    opener?.cancel()
                    openerFor = chip
                    opener = chip?.let { id -> scope.launch { delay(700); selectedId = id } }
                }
            }
            override fun onExited(event: DragAndDropEvent) = stop()
            override fun onEnded(event: DragAndDropEvent) = stop()
            override fun onDrop(event: DragAndDropEvent): Boolean {
                val hit = zones.hit(event.position())
                stop()
                val cardId = draggedCardId(event) ?: return false
                when (hit) {
                    // Let go on a board's chip: the bottom of its first column.
                    is DropHit.Chip -> {
                        val first = engine.children(hit.boardId, OrganizerKinds.COLUMN).minByOrNull { it.sortPosition } ?: return false
                        moveCardToEnd(engine, cardId, first)
                    }
                    // On a card: just above it.
                    is DropHit.Card -> {
                        if (hit.cardId == cardId) return true
                        val column = engine.item(hit.columnId) ?: return false
                        val target = engine.item(hit.cardId) ?: return false
                        val others = engine.children(column.id, OrganizerKinds.CARD).filter { it.id != cardId }.sortedBy { it.sortPosition }
                        val index = others.indexOfFirst { it.id == target.id }
                        val previous = if (index > 0) others[index - 1].sortPosition else target.sortPosition - 1
                        moveCard(engine, cardId, column, (previous + target.sortPosition) / 2)
                    }
                    // Anywhere else in a column: its bottom.
                    is DropHit.Column -> moveCardToEnd(engine, cardId, engine.item(hit.columnId) ?: return false)
                    null -> return false
                }
                return true
            }
        }
    }
    Column(Modifier.fillMaxSize().dragAndDropTarget(shouldStartDragAndDrop = ::isCardDrag, target = paneTarget)) {
        LazyRow(contentPadding = PaddingValues(12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            items(boards, key = { it.id }) { item ->
                val chipFill = when {
                    (zones.hovered as? DropHit.Chip)?.boardId == item.id -> SentinelGreen.copy(alpha = 0.35f)
                    item.id == board?.id -> SentinelGreen.copy(alpha = 0.18f)
                    else -> MaterialTheme.colorScheme.surfaceVariant
                }
                Row(
                    Modifier.clip(RoundedCornerShape(50)).background(chipFill)
                        .dropZone(zones.chips, item.id)
                        .clickable { selectedId = item.id }.padding(horizontal = 12.dp, vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Box(Modifier.size(8.dp).clip(CircleShape).background(spaceColor(engine, item.spaceId)))
                    Spacer(Modifier.width(6.dp))
                    Text(item.title.ifEmpty { "Board" })
                }
            }
            item { TextButton(onClick = { nameText = ""; naming = "newBoard" to null }) { Text("+ New board") } }
        }
        if (board == null) {
            Text("No boards yet - make one to track anything in columns: chores, projects, a move.", color = SentinelTextDim, modifier = Modifier.padding(24.dp))
            return@Column
        }
        Row(Modifier.padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(board.title, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
            SpaceSelector(engine, board.spaceId) { engine.update(board.id, mapOf("spaceId" to it)) }
        }
        Row(Modifier.padding(horizontal = 4.dp).horizontalScroll(rememberScrollState())) {
            TextButton(onClick = { nameText = board.title; naming = "renameBoard" to board.id }) { Text("Rename") }
            TextButton(onClick = { nameText = ""; naming = "newColumn" to board.id }) { Text("Add column") }
            // Rules that run on the server: "every morning, add a card", "moved to Done → done".
            TextButton(onClick = { automationsFor = board }) { Text("⚡ Automations") }
            TextButton(onClick = { confirmDelete = true }) { Text("Delete board", color = SentinelRed) }
        }
        val columns = engine.children(board.id, OrganizerKinds.COLUMN).sortedBy { it.sortPosition }
        LazyRow(contentPadding = PaddingValues(12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), modifier = Modifier.fillMaxSize()) {
            items(columns, key = { it.id }) { column ->
                BoardColumn(engine, column, columns, boards, zones, routes, onComments = { commentsFor = it }, onRename = { nameText = column.title; naming = "renameColumn" to column.id })
            }
        }
    }

    naming?.let { (kind, id) ->
        AlertDialog(
            onDismissRequest = { naming = null },
            title = { Text(when (kind) { "newBoard" -> "New board"; "renameBoard" -> "Rename board"; "newColumn" -> "New column"; else -> "Rename column" }) },
            text = { OutlinedTextField(value = nameText, onValueChange = { nameText = it }, singleLine = true) },
            confirmButton = {
                TextButton(onClick = {
                    val name = nameText.trim()
                    if (name.isNotEmpty()) when (kind) {
                        "newBoard" -> {
                            val created = engine.create(OrganizerKinds.BOARD, mapOf("title" to name, "position" to System.currentTimeMillis().toDouble()),
                                spaceId = filter ?: engine.defaultSpaceId)
                            listOf("To do", "Doing", "Done").forEachIndexed { i, title ->
                                engine.create(OrganizerKinds.COLUMN, mapOf("title" to title, "position" to (i + 1).toDouble()), parentId = created.id)
                            }
                            selectedId = created.id
                        }
                        "newColumn" -> {
                            val last = engine.children(id!!, OrganizerKinds.COLUMN).map { it.sortPosition }.filter { it < 1e9 }.maxOrNull() ?: 0.0
                            engine.create(OrganizerKinds.COLUMN, mapOf("title" to name, "position" to last + 1), parentId = id)
                        }
                        else -> engine.update(id!!, mapOf("title" to name))
                    }
                    naming = null
                }) { Text("Save") }
            },
            dismissButton = { TextButton(onClick = { naming = null }) { Text("Cancel") } },
        )
    }
    automationsFor?.let { BoardAutomationsDialog(engine, it) { automationsFor = null } }
    commentsFor?.let { CardCommentsSheet(engine, it) { commentsFor = null } }
    if (confirmDelete && board != null) {
        AlertDialog(
            onDismissRequest = { confirmDelete = false },
            title = { Text("Delete \"${board.title}\" and every card on it?") },
            confirmButton = { TextButton(onClick = { engine.deleteTree(board.id); confirmDelete = false }) { Text("Delete", color = SentinelRed) } },
            dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text("Cancel") } },
        )
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun BoardColumn(engine: OrganizerEngine, column: OrganizerItem, columns: List<OrganizerItem>, boards: List<OrganizerItem>, zones: DropZones, routes: OrganizerRoutes, onComments: (String) -> Unit, onRename: () -> Unit) {
    var newCard by remember { mutableStateOf("") }
    var menu by remember { mutableStateOf(false) }
    val cards = engine.children(column.id, OrganizerKinds.CARD).sortedBy { it.sortPosition }
    val targeted = zones.hovered.let { it is DropHit.Column && it.columnId == column.id || it is DropHit.Card && it.columnId == column.id }
    Column(
        Modifier.width(270.dp).fillMaxHeight().clip(RoundedCornerShape(10.dp))
            .background(if (targeted) SentinelGreen.copy(alpha = 0.12f) else MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f))
            .dropZone(zones.columns, column.id)
            .padding(10.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(column.title, color = SentinelCyan, style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
            Text(cards.size.toString(), color = SentinelTextDim, style = MaterialTheme.typography.labelSmall)
            Box {
                TextButton(onClick = { menu = true }) { Text("⋯") }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    DropdownMenuItem(text = { Text("Rename") }, onClick = { menu = false; onRename() })
                    val index = columns.indexOf(column)
                    if (index > 0) DropdownMenuItem(text = { Text("Move left") }, onClick = { menu = false; moveColumn(engine, columns, index, -1) })
                    if (index < columns.size - 1) DropdownMenuItem(text = { Text("Move right") }, onClick = { menu = false; moveColumn(engine, columns, index, 1) })
                    DropdownMenuItem(text = { Text("Delete column", color = SentinelRed) }, onClick = { menu = false; engine.deleteTree(column.id) })
                }
            }
        }
        LazyColumn(Modifier.weight(1f, fill = false), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(cards, key = { it.id }) { card -> CardTile(engine, card, cards, column, columns, boards, zones, routes, onComments) }
        }
        OutlinedTextField(
            value = newCard, onValueChange = { newCard = it }, singleLine = true, label = { Text("Add a card") },
            trailingIcon = {
                TextButton(enabled = newCard.isNotBlank(), onClick = {
                    val last = cards.map { it.sortPosition }.filter { it < 1e9 }.maxOrNull() ?: 0.0
                    engine.create(OrganizerKinds.CARD, mapOf("title" to newCard.trim(), "position" to last + 1), parentId = column.id)
                    newCard = ""
                }) { Text("Add") }
            },
        )
    }
}

private fun moveColumn(engine: OrganizerEngine, columns: List<OrganizerItem>, index: Int, by: Int) {
    val ordered = columns.toMutableList()
    val target = index + by
    if (target !in ordered.indices) return
    java.util.Collections.swap(ordered, index, target)
    ordered.forEachIndexed { position, item ->
        if (item.number("position") != (position + 1).toDouble()) engine.update(item.id, mapOf("position" to (position + 1).toDouble()))
    }
}

// Tap to open, long-press and drag to move - onto another column, another card, or a board's chip
// at the top. The ⋮ menu does the same moves for people who find dragging on a phone fiddly.
// The comment bubble is a tap target of its own: it opens just the comments, not the editor.
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun CardTile(engine: OrganizerEngine, card: OrganizerItem, cards: List<OrganizerItem>, column: OrganizerItem, columns: List<OrganizerItem>, boards: List<OrganizerItem>, zones: DropZones, routes: OrganizerRoutes, onComments: (String) -> Unit) {
    var menu by remember { mutableStateOf(false) }
    var pickingBoard by remember { mutableStateOf(false) }
    val attachments = engine.children(card.id, OrganizerKinds.ATTACHMENT)
    val cover = attachments.firstOrNull { it.string("contentType")?.startsWith("image/") == true }
    val checklist = card.list("checklist")
    val comments = engine.children(card.id, OrganizerKinds.COMMENT).size
    Box {
        Card(
            Modifier.fillMaxWidth()
                .dragAndDropSource {
                    detectTapGestures(
                        onTap = { routes.openCard(card.id) },
                        onLongPress = {
                            startTransfer(DragAndDropTransferData(ClipData(ClipDescription(card.title, arrayOf(CARD_MIME)), ClipData.Item(card.id))))
                        },
                    )
                }
                .dropZone(zones.cards, card.id, column.id)
                .semantics(mergeDescendants = true) {
                    role = Role.Button
                    onClick(label = "Open") { routes.openCard(card.id); true }
                },
        ) {
            Column(Modifier.padding(10.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                cover?.string("hash")?.let { OrganizerImage(engine, it, Modifier.fillMaxWidth().height(110.dp).clip(RoundedCornerShape(6.dp))) }
                val labels = card.list("labels").mapNotNull { (it as? Map<*, *>)?.get("name") as? String }
                if (labels.isNotEmpty()) {
                    Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                        labels.forEach { Text(it, fontSize = 10.sp, modifier = Modifier.background(SentinelCyan.copy(alpha = 0.25f), RoundedCornerShape(4.dp)).padding(horizontal = 5.dp)) }
                    }
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(card.title.ifEmpty { "Card" }, textDecoration = if (card.bool("completed")) TextDecoration.LineThrough else null, modifier = Modifier.weight(1f))
                    // Beside the menu rather than with the badges below, so a card with no
                    // comments (just the faint bubble) is no taller than it was.
                    CommentBadge(comments) { onComments(card.id) }
                    IconButton(onClick = { menu = true }, modifier = Modifier.size(24.dp)) {
                        Icon(Icons.Filled.MoreVert, contentDescription = "Card options", tint = SentinelTextDim, modifier = Modifier.size(18.dp))
                    }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                    card.string("due")?.let { dueText ->
                        OrganizerDates.parse(dueText, dueText.length == 10)?.let { due ->
                            val late = !card.bool("completed") && due.toLocalDate().isBefore(LocalDate.now())
                            Text("⏰ " + due.toLocalDate().format(DateTimeFormatter.ofPattern("MMM d")), fontSize = 11.sp, color = if (late) SentinelRed else SentinelTextDim)
                        }
                    }
                    if (checklist.isNotEmpty()) Text("☑ ${checklist.count { (it as? Map<*, *>)?.get("done") == true }}/${checklist.size}", fontSize = 11.sp, color = SentinelTextDim)
                    if (attachments.isNotEmpty()) Text("📎 ${attachments.size}", fontSize = 11.sp, color = SentinelTextDim)
                }
            }
        }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
            columns.filter { it.id != column.id }.forEach { other ->
                DropdownMenuItem(text = { Text("Move to ${other.title}") }, onClick = {
                    menu = false
                    moveCardToEnd(engine, card.id, other)
                })
            }
            if (boards.any { it.id != column.parentId }) DropdownMenuItem(text = { Text("Move to another board…") }, onClick = {
                menu = false
                pickingBoard = true
            })
            val index = cards.indexOf(card)
            if (index > 0) DropdownMenuItem(text = { Text("Move up") }, onClick = {
                menu = false
                val before = if (index > 1) cards[index - 2].sortPosition else cards[index - 1].sortPosition - 1
                engine.update(card.id, mapOf("position" to (before + cards[index - 1].sortPosition) / 2))
            })
            if (index < cards.size - 1) DropdownMenuItem(text = { Text("Move down") }, onClick = {
                menu = false
                val after = if (index < cards.size - 2) cards[index + 2].sortPosition else cards[index + 1].sortPosition + 1
                engine.update(card.id, mapOf("position" to (cards[index + 1].sortPosition + after) / 2))
            })
            DropdownMenuItem(text = { Text(if (card.bool("completed")) "Mark not done" else "Mark done") }, onClick = {
                menu = false
                engine.update(card.id, mapOf("completed" to !card.bool("completed")))
            })
            DropdownMenuItem(text = { Text("Delete", color = SentinelRed) }, onClick = { menu = false; engine.deleteTree(card.id) })
        }
    }
    if (pickingBoard) {
        AlertDialog(
            onDismissRequest = { pickingBoard = false },
            title = { Text("Move \"${card.title.ifEmpty { "Card" }}\" to") },
            text = {
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    boards.filter { it.id != column.parentId }.forEach { other ->
                        Text(other.title.ifEmpty { "Board" }, color = SentinelCyan, style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 8.dp))
                        engine.children(other.id, OrganizerKinds.COLUMN).sortedBy { it.sortPosition }.forEach { target ->
                            TextButton(onClick = { pickingBoard = false; moveCardToEnd(engine, card.id, target) }) { Text(target.title) }
                        }
                    }
                }
            },
            confirmButton = {},
            dismissButton = { TextButton(onClick = { pickingBoard = false }) { Text("Cancel") } },
        )
    }
}

// A card tile's comment count, which opens the comments-only sheet. With no comments yet it's a
// faint bubble, so there's somewhere to tap to write the first one. It stays tile-sized: Compose
// counts a touch within 48dp of a small clickable as a hit on it, so the finger target is full
// size without making the tile taller. Its own click consumes the tap, so the tile underneath
// neither opens the editor nor starts a drag.
@Composable
private fun CommentBadge(count: Int, onClick: () -> Unit) {
    val tint = if (count > 0) SentinelTextDim else SentinelTextDim.copy(alpha = 0.45f)
    Row(
        Modifier.clip(RoundedCornerShape(4.dp))
            .clickable(role = Role.Button, onClickLabel = "Show comments", onClick = onClick)
            .semantics { contentDescription = when (count) { 0 -> "Add a comment"; 1 -> "1 comment"; else -> "$count comments" } }
            .padding(horizontal = 4.dp, vertical = 2.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        Icon(Icons.Outlined.ChatBubbleOutline, contentDescription = null, tint = tint, modifier = Modifier.size(12.dp))
        if (count > 0) Text("$count", fontSize = 11.sp, color = tint)
    }
}

// What a dragged card carries: its id, under a type of its own so a text field ("Add a card")
// can't take the drop and type the id into itself.
private const val CARD_MIME = "application/x-sentinelcore-card"

private fun isCardDrag(event: DragAndDropEvent): Boolean = CARD_MIME in event.mimeTypes()

private fun DragAndDropEvent.position(): Offset = toAndroidDragEvent().let { Offset(it.x, it.y) }

private sealed interface DropHit {
    data class Chip(val boardId: String) : DropHit
    data class Column(val columnId: String) : DropHit
    data class Card(val cardId: String, val columnId: String) : DropHit
}

// Where each board chip, column and card is on screen, kept up to date as they're laid out and
// forgotten when they leave the screen - what the board screen's one drop target hit-tests.
private class DropZones {
    val chips = mutableMapOf<String, Pair<Rect, String?>>()
    val columns = mutableMapOf<String, Pair<Rect, String?>>()
    val cards = mutableMapOf<String, Pair<Rect, String?>>()
    var hovered by mutableStateOf<DropHit?>(null)

    fun hit(point: Offset): DropHit? {
        chips.entries.firstOrNull { point in it.value.first }?.let { return DropHit.Chip(it.key) }
        cards.entries.firstOrNull { point in it.value.first }?.let { return DropHit.Card(it.key, it.value.second!!) }
        columns.entries.firstOrNull { point in it.value.first }?.let { return DropHit.Column(it.key) }
        return null
    }
}

@Composable
private fun Modifier.dropZone(zones: MutableMap<String, Pair<Rect, String?>>, id: String, parent: String? = null): Modifier {
    DisposableEffect(zones, id) { onDispose { zones.remove(id) } }
    return onGloballyPositioned { zones[id] = it.boundsInRoot() to parent }
}

private fun draggedCardId(event: DragAndDropEvent): String? {
    val clip = event.toAndroidDragEvent().clipData ?: return null
    if (clip.itemCount == 0 || clip.description?.hasMimeType(CARD_MIME) != true) return null
    return clip.getItemAt(0).text?.toString()
}

// Puts a card into a column, which may be on another board or in another space. The server would
// move the card's comments and attachments into the new space anyway; sending the space too lets
// this phone do the same straight away.
private fun moveCard(engine: OrganizerEngine, cardId: String, column: OrganizerItem, position: Double) {
    val card = engine.item(cardId)?.takeIf { it.kind == OrganizerKinds.CARD } ?: return
    val changes = mutableMapOf<String, Any?>("position" to position)
    if (card.parentId != column.id) changes["parentId"] = column.id
    if (card.spaceId != column.spaceId) changes["spaceId"] = column.spaceId
    engine.update(cardId, changes)
}

private fun moveCardToEnd(engine: OrganizerEngine, cardId: String, column: OrganizerItem) {
    val last = engine.children(column.id, OrganizerKinds.CARD).filter { it.id != cardId }.map { it.sortPosition }.filter { it < 1e9 }.maxOrNull() ?: 0.0
    moveCard(engine, cardId, column, last + 1)
}

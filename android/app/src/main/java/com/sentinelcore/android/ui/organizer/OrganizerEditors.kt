package com.sentinelcore.android.ui.organizer

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import com.sentinelcore.android.organizer.OrganizerDates
import com.sentinelcore.android.organizer.OrganizerEngine
import com.sentinelcore.android.organizer.OrganizerItem
import com.sentinelcore.android.organizer.OrganizerKinds
import com.sentinelcore.android.organizer.OrganizerPerson
import com.sentinelcore.android.organizer.RecurrenceRule
import com.sentinelcore.android.ui.screens.DetailHeader
import com.sentinelcore.android.ui.theme.SentinelCyan
import com.sentinelcore.android.ui.theme.SentinelRed
import com.sentinelcore.android.ui.theme.SentinelTextDim
import java.time.Duration
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.UUID
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.delay

// Editors for events, reminders, notes and board cards. Each writes only the fields that changed,
// so saving doesn't stamp a fresh clock on every field and quietly win against another device's
// edit to something this person never touched.

private fun changed(before: Map<String, Any?>, after: Map<String, Any?>): Map<String, Any?> =
    after.filter { (name, value) -> before[name] != value }

@Composable
private fun EditorFrame(title: String, onBack: () -> Unit, onSave: (() -> Unit)?, content: @Composable () -> Unit) {
    Column(Modifier.fillMaxSize()) {
        DetailHeader(title, onBack) { onSave?.let { TextButton(onClick = it) { Text("Save") } } }
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            content()
            Spacer(Modifier.height(40.dp))
        }
    }
}

@Composable
private fun DateTimeRow(label: String, date: LocalDate, time: LocalTime?, onDate: (LocalDate) -> Unit, onTime: ((LocalTime) -> Unit)?) {
    val context = LocalContext.current
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(label, modifier = Modifier.weight(1f))
        OutlinedButton(onClick = { pickDate(context, date, onDate) }) { Text(date.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM))) }
        if (time != null && onTime != null) OutlinedButton(onClick = { pickTime(context, time, onTime) }) {
            Text(time.format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT)))
        }
    }
}

@Composable
private fun SwitchRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(label, modifier = Modifier.weight(1f))
        Switch(checked = checked, onCheckedChange = onChange)
    }
}

@Composable
private fun DeleteButton(label: String, onDelete: () -> Unit) {
    var confirm by remember { mutableStateOf(false) }
    TextButton(onClick = { confirm = true }) { Text(label, color = SentinelRed) }
    if (confirm) AlertDialog(
        onDismissRequest = { confirm = false },
        title = { Text("$label?") },
        confirmButton = { TextButton(onClick = { confirm = false; onDelete() }) { Text("Delete", color = SentinelRed) } },
        dismissButton = { TextButton(onClick = { confirm = false }) { Text("Cancel") } },
    )
}

// ---- events -----------------------------------------------------------------------------------

@Composable
fun EventEditorScreen(engine: OrganizerEngine, itemId: String?, day: LocalDate?, originalStart: String?, onDone: () -> Unit) {
    val zone = ZoneId.systemDefault()
    val existing = remember(itemId) { itemId?.let(engine::item) }
    var saved by remember { mutableStateOf(existing) }
    // Opened from one occurrence of a repeating event: edits can go to that one or the series.
    val occurrence = originalStart?.takeIf { existing?.string("recurrence") != null }
    val initial = remember(itemId) {
        val span = existing?.let { com.sentinelcore.android.organizer.Recurrence.span(it, zone) }
        val start = occurrence?.let { OrganizerDates.parse(it, existing!!.bool("allDay"), zone) } ?: span?.first
            ?: (day ?: LocalDate.now()).atTime(LocalTime.now().plusHours(1).withMinute(0).withSecond(0).withNano(0)).atZone(zone)
        val length = span?.let { Duration.between(it.first, it.second) } ?: Duration.ofHours(1)
        start to start.plus(length)
    }
    var title by remember { mutableStateOf(existing?.title.orEmpty()) }
    var location by remember { mutableStateOf(existing?.string("location").orEmpty()) }
    var notes by remember { mutableStateOf(existing?.string("notes").orEmpty()) }
    var allDay by remember { mutableStateOf(existing?.bool("allDay") ?: false) }
    var start by remember { mutableStateOf(initial.first) }
    // All-day ends are exclusive on the wire; the form shows the last day.
    var end by remember { mutableStateOf(if (existing?.bool("allDay") == true) initial.second.minusDays(1) else initial.second) }
    var repeat by remember { mutableStateOf(RepeatChoice.from(existing?.string("recurrence"))) }
    var until by remember { mutableStateOf(RecurrenceRule.parse(existing?.string("recurrence"))?.until?.toLocalDate()) }
    var alert by remember { mutableStateOf((existing?.list("alerts")?.firstOrNull() as? Number)?.toInt()) }
    var spaceId by remember { mutableStateOf(existing?.spaceId ?: engine.defaultSpaceId) }
    var links by remember { mutableStateOf(OrganizerLink.from(existing)) }
    var askScope by remember { mutableStateOf<String?>(null) }

    fun fields(): Map<String, Any?> {
        val endValue = if (allDay) end.toLocalDate().coerceAtLeast(start.toLocalDate()).plusDays(1).atStartOfDay(zone)
        else if (end.isAfter(start)) end else start.plusHours(1)
        return mapOf(
            "title" to title.trim().ifEmpty { "Untitled" },
            "allDay" to allDay,
            "start" to OrganizerDates.fieldValue(if (allDay) start.toLocalDate().atStartOfDay(zone) else start, allDay),
            "end" to OrganizerDates.fieldValue(endValue, allDay),
            "timeZone" to zone.id,
            "recurrence" to repeat.rule(until?.atTime(23, 59, 59)?.atZone(zone), saved?.string("recurrence")),
            "alerts" to alert?.let { listOf(it.toDouble()) },
            "location" to location.ifBlank { null },
            "notes" to notes.ifBlank { null },
            "links" to OrganizerLink.json(links),
        )
    }

    fun ensureSaved(): String = saved?.id ?: engine.create(OrganizerKinds.EVENT, fields(), spaceId = spaceId).also { saved = it }.id

    fun excludeFromSeries(item: OrganizerItem, original: String) {
        val existingDates = engine.item(item.id)?.list("exdates").orEmpty()
        engine.update(item.id, mapOf("exdates" to existingDates + original))
    }

    fun deleteSeries(item: OrganizerItem) {
        engine.deleteTree(item.id)
        engine.items(OrganizerKinds.EVENT).filter { it.string("seriesId") == item.id }.forEach { engine.deleteTree(it.id) }
    }

    fun save(onlyThis: Boolean?) {
        val item = saved?.let { engine.item(it.id) }
        if (item == null) {
            ensureSaved()
        } else if (onlyThis == true && occurrence != null) {
            excludeFromSeries(item, occurrence)
            engine.create(OrganizerKinds.EVENT, fields() + mapOf("recurrence" to null, "seriesId" to item.id, "originalStart" to occurrence), spaceId = item.spaceId)
        } else {
            var updates = changed(item.fields, fields())
            if (onlyThis == false && occurrence != null) {
                // Every occurrence: keep the series' own first date, move only time and length.
                val seriesStart = OrganizerDates.parse(item.string("start"), item.bool("allDay"), zone)
                val occurrenceStart = OrganizerDates.parse(occurrence, item.bool("allDay"), zone)
                if (seriesStart != null && occurrenceStart != null) {
                    val newStart = seriesStart.plus(Duration.between(occurrenceStart, start))
                    val length = Duration.between(start, if (allDay) end.plusDays(1) else end)
                    updates = changed(item.fields, item.fields + updates + mapOf(
                        "start" to OrganizerDates.fieldValue(newStart, allDay),
                        "end" to OrganizerDates.fieldValue(newStart.plus(length), allDay),
                    ))
                }
            }
            if (spaceId != item.spaceId && occurrence == null) updates = updates + ("spaceId" to spaceId)
            if (updates.isNotEmpty()) engine.update(item.id, updates)
        }
        onDone()
    }

    EditorFrame(if (saved == null) "New event" else "Event", onDone, { if (occurrence != null) askScope = "save" else save(null) }) {
        OutlinedTextField(title, { title = it }, label = { Text("Title") }, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(location, { location = it }, label = { Text("Location") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        SwitchRow("All day", allDay) { allDay = it }
        DateTimeRow("Starts", start.toLocalDate(), if (allDay) null else start.toLocalTime(),
            { date -> val length = Duration.between(start, end); start = date.atTime(start.toLocalTime()).atZone(zone); end = start.plus(length) },
            { time -> val length = Duration.between(start, end); start = start.toLocalDate().atTime(time).atZone(zone); end = start.plus(length) })
        DateTimeRow("Ends", end.toLocalDate(), if (allDay) null else end.toLocalTime(),
            { date -> end = date.atTime(end.toLocalTime()).atZone(zone) },
            { time -> end = end.toLocalDate().atTime(time).atZone(zone) })
        ChoiceMenu("Repeat", repeat.label, RepeatChoice.entries.filter { it != RepeatChoice.CUSTOM || repeat == RepeatChoice.CUSTOM }.map { it to it.label }) { repeat = it }
        if (repeat != RepeatChoice.NEVER && repeat != RepeatChoice.CUSTOM) {
            val context = LocalContext.current
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("End repeat", modifier = Modifier.weight(1f))
                TextButton(onClick = { pickDate(context, until ?: start.toLocalDate().plusMonths(3)) { until = it } }) {
                    Text(until?.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM)) ?: "Never")
                }
                if (until != null) IconButton(onClick = { until = null }) { Icon(Icons.Default.Close, contentDescription = "Clear end date") }
            }
        }
        ChoiceMenu("Alert", alertChoices.firstOrNull { it.first == alert }?.second ?: "None", alertChoices) { alert = it }
        if (occurrence == null) SpaceSelector(engine, spaceId) { spaceId = it }
        OutlinedTextField(notes, { notes = it }, label = { Text("Notes") }, modifier = Modifier.fillMaxWidth().heightIn(min = 100.dp))
        LinksEditor(links) { links = it }
        AttachmentsEditor(engine, saved?.id) { ensureSaved() }
        saved?.let { item -> DeleteButton("Delete event") {
            if (occurrence != null) askScope = "delete" else { deleteSeries(item); onDone() }
        } }
    }

    askScope?.let { action ->
        AlertDialog(
            onDismissRequest = { askScope = null },
            title = { Text(if (action == "delete") "Delete which events?" else "Change which events?") },
            confirmButton = {
                TextButton(onClick = {
                    askScope = null
                    if (action == "save") save(onlyThis = false) else { saved?.let(::deleteSeries); onDone() }
                }) { Text("All events") }
            },
            dismissButton = {
                TextButton(onClick = {
                    askScope = null
                    if (action == "save") save(onlyThis = true) else { saved?.let { excludeFromSeries(it, occurrence!!) }; onDone() }
                }) { Text("Only this one") }
            },
        )
    }
}

// ---- reminders --------------------------------------------------------------------------------

@Composable
fun ReminderEditorScreen(engine: OrganizerEngine, itemId: String?, onDone: () -> Unit) {
    val zone = ZoneId.systemDefault()
    val existing = remember(itemId) { itemId?.let(engine::item) }
    var saved by remember { mutableStateOf(existing) }
    val dueAt = remember(itemId) { OrganizerDates.parse(existing?.string("due"), existing?.bool("allDay") ?: false, zone) }
    var title by remember { mutableStateOf(existing?.title.orEmpty()) }
    var notes by remember { mutableStateOf(existing?.string("notes").orEmpty()) }
    var hasDue by remember { mutableStateOf(dueAt != null) }
    var hasTime by remember { mutableStateOf(dueAt != null && existing?.bool("allDay") == false) }
    var due by remember { mutableStateOf(dueAt ?: LocalDate.now().atTime(9, 0).atZone(zone)) }
    var repeat by remember { mutableStateOf(RepeatChoice.from(existing?.string("recurrence"))) }
    var priority by remember { mutableStateOf(existing?.number("priority")?.toInt() ?: 0) }
    var alert by remember { mutableStateOf((existing?.list("alerts")?.firstOrNull() as? Number)?.toInt()) }
    var flagged by remember { mutableStateOf(existing?.bool("flagged") ?: false) }
    var spaceId by remember { mutableStateOf(existing?.spaceId ?: engine.defaultSpaceId) }
    var links by remember { mutableStateOf(OrganizerLink.from(existing)) }
    var newStep by remember { mutableStateOf("") }
    rememberOrganizerRevision(engine)

    fun fields(): Map<String, Any?> = mapOf(
        "title" to title.trim().ifEmpty { "Reminder" },
        "due" to if (hasDue) OrganizerDates.fieldValue(due, allDay = !hasTime) else null,
        "allDay" to if (hasDue) !hasTime else null,
        "recurrence" to if (hasDue) repeat.rule(null, saved?.string("recurrence")) else null,
        "priority" to priority.toDouble(),
        "alerts" to if (hasDue) alert?.let { listOf(it.toDouble()) } else null,
        "flagged" to flagged,
        "notes" to notes.ifBlank { null },
        "links" to OrganizerLink.json(links),
    )

    fun ensureSaved(): String = saved?.id ?: engine.create(OrganizerKinds.REMINDER, fields() + ("completed" to false), spaceId = spaceId).also { saved = it }.id

    EditorFrame(if (saved == null) "New reminder" else "Reminder", onDone, {
        val item = saved?.let { engine.item(it.id) }
        if (item == null) ensureSaved() else {
            var updates = changed(item.fields, fields())
            if (item.parentId == null && spaceId != item.spaceId) updates = updates + ("spaceId" to spaceId)
            if (updates.isNotEmpty()) engine.update(item.id, updates)
        }
        onDone()
    }) {
        OutlinedTextField(title, { title = it }, label = { Text("Title") }, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(notes, { notes = it }, label = { Text("Notes") }, modifier = Modifier.fillMaxWidth())
        SwitchRow("Date", hasDue) { hasDue = it }
        if (hasDue) {
            DateTimeRow("Due", due.toLocalDate(), if (hasTime) due.toLocalTime() else null,
                { date -> due = date.atTime(due.toLocalTime()).atZone(zone) }, { time -> due = due.toLocalDate().atTime(time).atZone(zone) })
            SwitchRow("Time", hasTime) { hasTime = it }
            ChoiceMenu("Repeat", repeat.label, RepeatChoice.entries.filter { it != RepeatChoice.CUSTOM || repeat == RepeatChoice.CUSTOM }.map { it to it.label }) { repeat = it }
            ChoiceMenu("Alert", if (alert == null) "At due time" else alertChoices.firstOrNull { it.first == alert }?.second.orEmpty(),
                alertChoices.map { if (it.first == null) null to "At due time" else it }) { alert = it }
        }
        ChoiceMenu("Priority", listOf("None", "Low", "Medium", "High")[priority], listOf(0 to "None", 1 to "Low", 2 to "Medium", 3 to "High")) { priority = it }
        SwitchRow("Flagged", flagged) { flagged = it }
        if (existing?.parentId == null) SpaceSelector(engine, spaceId) { spaceId = it }

        saved?.takeIf { it.parentId == null }?.let { item ->
            Text("Steps", color = SentinelCyan, style = MaterialTheme.typography.labelLarge)
            engine.children(item.id, OrganizerKinds.REMINDER).sortedBy { it.sortPosition }.forEach { step ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(checked = step.bool("completed"), onCheckedChange = { engine.update(step.id, mapOf("completed" to it)) })
                    Text(step.title, modifier = Modifier.weight(1f), textDecoration = if (step.bool("completed")) TextDecoration.LineThrough else null)
                    IconButton(onClick = { engine.delete(step.id) }) { Icon(Icons.Default.Close, contentDescription = "Remove step") }
                }
            }
            OutlinedTextField(newStep, { newStep = it }, label = { Text("Add a step") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                trailingIcon = {
                    TextButton(enabled = newStep.isNotBlank(), onClick = {
                        engine.create(OrganizerKinds.REMINDER, mapOf("title" to newStep.trim(), "completed" to false, "position" to System.currentTimeMillis().toDouble()), parentId = item.id)
                        newStep = ""
                    }) { Text("Add") }
                })
        }
        LinksEditor(links) { links = it }
        AttachmentsEditor(engine, saved?.id) { ensureSaved() }
        saved?.let { item -> DeleteButton("Delete reminder") { engine.deleteTree(item.id); onDone() } }
    }
}

// ---- notes ------------------------------------------------------------------------------------

// Saves as you type (a moment after you stop), and takes another device's edits when you're not
// in the middle of your own - the server keeps both if two people rewrite the same note.
@Composable
fun NoteEditorScreen(engine: OrganizerEngine, itemId: String?, onDone: () -> Unit) {
    val revision = rememberOrganizerRevision(engine)
    val noteId = remember(itemId) { itemId ?: engine.create(OrganizerKinds.NOTE, mapOf("title" to "", "body" to ""), spaceId = engine.defaultSpaceId).id }
    val note = engine.item(noteId)
    var title by remember { mutableStateOf(note?.title.orEmpty()) }
    var body by remember { mutableStateOf(note?.string("body").orEmpty()) }
    var dirty by remember { mutableStateOf(false) }
    var preview by remember { mutableStateOf(false) }
    val keptOffline = engine.keptOffline.collectAsStateValue()

    fun saveNow() {
        val current = engine.item(noteId) ?: return
        val updates = buildMap {
            if (current.title != title) put("title", title)
            if (current.string("body").orEmpty() != body) put("body", body)
        }
        if (updates.isNotEmpty()) engine.update(noteId, updates)
        dirty = false
    }

    LaunchedEffect(title, body) {
        if (!dirty) return@LaunchedEffect
        delay(600)
        saveNow()
    }
    LaunchedEffect(revision) {
        val current = engine.item(noteId) ?: return@LaunchedEffect
        if (!dirty) {
            if (current.title != title) title = current.title
            if (current.string("body").orEmpty() != body) body = current.string("body").orEmpty()
        }
    }
    DisposableEffect(noteId) { onDispose { if (dirty) saveNow() } }

    if (note == null) {
        EditorFrame("Note", onDone, null) { Text("This note was deleted.", color = SentinelTextDim) }
        return
    }
    EditorFrame(title.ifBlank { "Note" }, { if (dirty) saveNow(); onDone() }, null) {
        OutlinedTextField(title, { title = it; dirty = true }, label = { Text("Title") }, modifier = Modifier.fillMaxWidth())
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Markdown: # heading, **bold**, - [ ] checklist", style = MaterialTheme.typography.labelSmall, color = SentinelTextDim, modifier = Modifier.weight(1f))
            TextButton(onClick = { if (dirty) saveNow(); preview = !preview }) { Text(if (preview) "Edit" else "Preview") }
        }
        if (preview) {
            MarkdownNote(body) { line ->
                body = toggleChecklistLine(body, line)
                dirty = true
                saveNow()
            }
        } else {
            OutlinedTextField(body, { body = it; dirty = true }, modifier = Modifier.fillMaxWidth().heightIn(min = 260.dp))
        }
        HorizontalDivider()
        SpaceSelector(engine, note.spaceId) { engine.update(noteId, mapOf("spaceId" to it)) }
        SwitchRow("Pinned", note.bool("pinned")) { engine.update(noteId, mapOf("pinned" to it)) }
        SwitchRow("Keep on this phone", noteId in keptOffline) { engine.setKeptOffline(noteId, it) }
        note.updatedBy?.let { Text("Last edited by $it", style = MaterialTheme.typography.labelSmall, color = SentinelTextDim) }
        LinksEditor(OrganizerLink.from(note)) { engine.update(noteId, mapOf("links" to OrganizerLink.json(it))) }
        AttachmentsEditor(engine, noteId) { noteId }
        DeleteButton("Delete note") { dirty = false; engine.deleteTree(noteId); onDone() }
    }
}

@Composable
private fun <T> kotlinx.coroutines.flow.StateFlow<T>.collectAsStateValue(): T = collectAsStateWithLifecycle().value

// ---- cards ------------------------------------------------------------------------------------

@Composable
fun CardEditorScreen(engine: OrganizerEngine, itemId: String, onDone: () -> Unit) {
    val card = remember(itemId) { engine.item(itemId) }
    if (card == null) {
        EditorFrame("Card", onDone, null) { Text("This card was deleted.", color = SentinelTextDim) }
        return
    }
    var title by remember { mutableStateOf(card.title) }
    var done by remember { mutableStateOf(card.bool("completed")) }
    var due by remember { mutableStateOf(card.string("due")?.let { OrganizerDates.parse(it, it.length == 10)?.toLocalDate() }) }
    var labels by remember { mutableStateOf(card.list("labels").mapNotNull { (it as? Map<*, *>)?.get("name") as? String }.joinToString(", ")) }
    var description by remember { mutableStateOf(card.string("description").orEmpty()) }
    val checklist = remember { mutableStateListOf<Triple<String, String, Boolean>>().apply {
        card.list("checklist").forEach { entry ->
            val map = entry as? Map<*, *> ?: return@forEach
            add(Triple(map["id"] as? String ?: UUID.randomUUID().toString().take(8), map["text"] as? String ?: "", map["done"] as? Boolean ?: false))
        }
    } }
    var newCheck by remember { mutableStateOf("") }
    var assignees by remember { mutableStateOf(card.list("assignees").mapNotNull { it as? String }.toSet()) }
    var people by remember { mutableStateOf<List<OrganizerPerson>>(emptyList()) }
    var links by remember { mutableStateOf(OrganizerLink.from(card)) }
    val context = LocalContext.current
    LaunchedEffect(Unit) {
        if (engine.status.value?.personalSpaces == true) people = runCatching { engine.people() }.getOrDefault(emptyList())
    }

    EditorFrame(engine.item(card.parentId ?: "")?.title ?: "Card", onDone, {
        val current = engine.item(itemId)
        if (current != null) {
            val existingLabels = current.list("labels").mapNotNull { it as? Map<*, *> }.associateBy { it["name"] as? String }
            val fields = mutableMapOf<String, Any?>(
                "title" to title.trim().ifEmpty { "Card" },
                "completed" to done,
                "due" to due?.toString(),
                "labels" to labels.split(",").map { it.trim() }.filter { it.isNotEmpty() }
                    .map { name -> existingLabels[name] ?: mapOf("name" to name) }.takeIf { it.isNotEmpty() },
                "description" to description.ifBlank { null },
                "checklist" to checklist.map { mapOf("id" to it.first, "text" to it.second, "done" to it.third) }.takeIf { it.isNotEmpty() },
                "links" to OrganizerLink.json(links),
            )
            if (people.isNotEmpty()) fields["assignees"] = assignees.sorted().takeIf { it.isNotEmpty() }
            val updates = changed(current.fields, fields)
            if (updates.isNotEmpty()) engine.update(itemId, updates)
        }
        onDone()
    }) {
        OutlinedTextField(title, { title = it }, label = { Text("Title") }, modifier = Modifier.fillMaxWidth())
        SwitchRow("Done", done) { done = it }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Due", modifier = Modifier.weight(1f))
            TextButton(onClick = { pickDate(context, due ?: LocalDate.now()) { due = it } }) {
                Text(due?.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM)) ?: "None")
            }
            if (due != null) IconButton(onClick = { due = null }) { Icon(Icons.Default.Close, contentDescription = "Clear due date") }
        }
        OutlinedTextField(labels, { labels = it }, label = { Text("Labels, comma separated") }, singleLine = true, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(description, { description = it }, label = { Text("Description") }, modifier = Modifier.fillMaxWidth().heightIn(min = 100.dp))
        Text("Checklist", color = SentinelCyan, style = MaterialTheme.typography.labelLarge)
        checklist.forEachIndexed { index, entry ->
            Row(verticalAlignment = Alignment.CenterVertically) {
                Checkbox(checked = entry.third, onCheckedChange = { checklist[index] = entry.copy(third = it) })
                Text(entry.second, modifier = Modifier.weight(1f), textDecoration = if (entry.third) TextDecoration.LineThrough else null)
                IconButton(onClick = { checklist.removeAt(index) }) { Icon(Icons.Default.Close, contentDescription = "Remove") }
            }
        }
        OutlinedTextField(newCheck, { newCheck = it }, label = { Text("Add an item") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
            trailingIcon = {
                TextButton(enabled = newCheck.isNotBlank(), onClick = {
                    checklist.add(Triple(UUID.randomUUID().toString().take(8), newCheck.trim(), false))
                    newCheck = ""
                }) { Text("Add") }
            })
        if (people.isNotEmpty()) {
            Text("People", color = SentinelCyan, style = MaterialTheme.typography.labelLarge)
            people.forEach { person ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(checked = person.username in assignees, onCheckedChange = { assignees = if (it) assignees + person.username else assignees - person.username })
                    Text(person.label)
                }
            }
        }
        LinksEditor(links) { links = it }
        AttachmentsEditor(engine, itemId) { itemId }
        DeleteButton("Delete card") { engine.deleteTree(itemId); onDone() }
    }
}

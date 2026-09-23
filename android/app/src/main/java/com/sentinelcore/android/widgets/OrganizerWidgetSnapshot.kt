package com.sentinelcore.android.widgets

import com.sentinelcore.android.organizer.HolidayColors
import com.sentinelcore.android.organizer.OrganizerDates
import com.sentinelcore.android.organizer.OrganizerEngine
import com.sentinelcore.android.organizer.OrganizerKinds
import com.sentinelcore.android.organizer.Recurrence
import java.io.File
import java.time.Instant
import java.time.ZoneId
import java.time.ZonedDateTime
import org.json.JSONArray
import org.json.JSONObject

// What the home-screen widgets show: the next two weeks of events, the open reminders and the
// latest notes, written by the app every time its Organizer copy changes. A widget never touches
// the Organizer database or the server - it reads this file, so it works with no signal, and it
// is as fresh as the app's last sync. The Apple apps do the same (OrganizerKit's WidgetSnapshot).
//
// Times are kept as moments rather than "overdue" flags: a widget is drawn again later from the
// same file, and works out what's past at the time it's drawn.
data class OrganizerWidgetSnapshot(
    val generatedAt: Long,
    // False when the Organizer is off on the server or the app hasn't been set up; the widgets
    // say so rather than look empty.
    val enabled: Boolean,
    val events: List<Event> = emptyList(),
    val reminders: List<Reminder> = emptyList(),
    val notes: List<Note> = emptyList(),
) {
    data class Event(
        // One occurrence of a repeating event is its own row, so the id carries the start.
        val id: String,
        val itemId: String,
        val title: String,
        val start: Long,
        val end: Long,
        val allDay: Boolean,
        val location: String? = null,
        // "#rrggbb" from the event or its space; otherwise a slot in the app's palette.
        val color: String? = null,
        val paletteIndex: Int = 0,
        val holiday: Boolean = false,
    )

    data class Reminder(
        val id: String,
        val title: String,
        val due: Long? = null,
        // The due field exactly as it was, so a check-off from a widget can tell whether the
        // reminder has moved on since (see OrganizerWidgetAction).
        val dueField: String? = null,
        val allDay: Boolean = false,
        val flagged: Boolean = false,
        val priority: Int = 0,
        val recurring: Boolean = false,
    ) {
        fun isOverdue(now: Long, zone: ZoneId = ZoneId.systemDefault()): Boolean {
            val due = due ?: return false
            return if (allDay) day(due, zone) < day(now, zone) else due < now
        }

        fun isDueToday(now: Long, zone: ZoneId = ZoneId.systemDefault()): Boolean =
            due?.let { day(it, zone) == day(now, zone) } ?: false
    }

    data class Note(val id: String, val title: String, val preview: String, val pinned: Boolean, val updatedAt: Long? = null)

    // Events still running or yet to start at `now`, in order.
    fun upcomingEvents(now: Long): List<Event> = events.filter { it.end > now }

    fun toJson(): String = JSONObject()
        .put("generatedAt", generatedAt)
        .put("enabled", enabled)
        .put("events", JSONArray(events.map { e ->
            JSONObject().put("id", e.id).put("itemId", e.itemId).put("title", e.title).put("start", e.start).put("end", e.end)
                .put("allDay", e.allDay).putOpt("location", e.location).putOpt("color", e.color)
                .put("paletteIndex", e.paletteIndex).put("holiday", e.holiday)
        }))
        .put("reminders", JSONArray(reminders.map { r ->
            JSONObject().put("id", r.id).put("title", r.title).putOpt("due", r.due).putOpt("dueField", r.dueField)
                .put("allDay", r.allDay).put("flagged", r.flagged).put("priority", r.priority).put("recurring", r.recurring)
        }))
        .put("notes", JSONArray(notes.map { n ->
            JSONObject().put("id", n.id).put("title", n.title).put("preview", n.preview).put("pinned", n.pinned).putOpt("updatedAt", n.updatedAt)
        }))
        .toString()

    companion object {
        val EMPTY = OrganizerWidgetSnapshot(generatedAt = 0, enabled = false)

        private fun day(millis: Long, zone: ZoneId) = Instant.ofEpochMilli(millis).atZone(zone).toLocalDate()

        fun fromJson(text: String?): OrganizerWidgetSnapshot? = runCatching {
            val json = JSONObject(text ?: return null)
            fun JSONObject.optStringOrNull(name: String) = if (has(name) && !isNull(name)) getString(name) else null
            fun JSONObject.optLongOrNull(name: String) = if (has(name) && !isNull(name)) getLong(name) else null
            fun <T> JSONArray?.list(read: (JSONObject) -> T): List<T> =
                if (this == null) emptyList() else (0 until length()).map { read(getJSONObject(it)) }
            OrganizerWidgetSnapshot(
                generatedAt = json.optLong("generatedAt"),
                enabled = json.optBoolean("enabled"),
                events = json.optJSONArray("events").list { e ->
                    Event(e.getString("id"), e.getString("itemId"), e.getString("title"), e.getLong("start"), e.getLong("end"),
                        e.optBoolean("allDay"), e.optStringOrNull("location"), e.optStringOrNull("color"),
                        e.optInt("paletteIndex"), e.optBoolean("holiday"))
                },
                reminders = json.optJSONArray("reminders").list { r ->
                    Reminder(r.getString("id"), r.getString("title"), r.optLongOrNull("due"), r.optStringOrNull("dueField"),
                        r.optBoolean("allDay"), r.optBoolean("flagged"), r.optInt("priority"), r.optBoolean("recurring"))
                },
                notes = json.optJSONArray("notes").list { n ->
                    Note(n.getString("id"), n.getString("title"), n.getString("preview"), n.optBoolean("pinned"), n.optLongOrNull("updatedAt"))
                },
            )
        }.getOrNull()
    }
}

// A reminder checked off on a widget, waiting for the app to make the real edit. `dueField` is
// the due date the widget showed: a repeating reminder moves on to its next date when done, and
// if it already has (done on another device, or this applied twice) the action is dropped rather
// than skipping a second occurrence.
data class OrganizerWidgetAction(val reminderId: String, val dueField: String?, val at: Long)

// The snapshot file and the queue of check-offs. Widgets on Android run in the app's own process,
// so one lock is enough to keep the two in step.
class OrganizerWidgetFiles(private val directory: File) {
    private val snapshotFile get() = File(directory, "organizer-widgets.json")
    private val actionsFile get() = File(directory, "organizer-widget-actions.json")

    fun readSnapshot(): OrganizerWidgetSnapshot = synchronized(LOCK) {
        OrganizerWidgetSnapshot.fromJson(snapshotFile.takeIf { it.exists() }?.readText()) ?: OrganizerWidgetSnapshot.EMPTY
    }

    fun writeSnapshot(snapshot: OrganizerWidgetSnapshot) = synchronized(LOCK) { writeAtomically(snapshotFile, snapshot.toJson()) }

    fun pendingActions(): List<OrganizerWidgetAction> = synchronized(LOCK) { readActions() }

    // Records the check-off and takes the reminder out of the snapshot, so the widgets drop it at
    // once rather than waiting for the app.
    fun complete(reminderId: String, now: Long = System.currentTimeMillis()) = synchronized(LOCK) {
        val snapshot = readSnapshot()
        val reminder = snapshot.reminders.firstOrNull { it.id == reminderId } ?: return@synchronized
        writeActions(readActions().filter { it.reminderId != reminderId } + OrganizerWidgetAction(reminderId, reminder.dueField, now))
        writeSnapshot(snapshot.copy(reminders = snapshot.reminders.filter { it.id != reminderId }))
    }

    fun takeActions(): List<OrganizerWidgetAction> = synchronized(LOCK) {
        readActions().also { if (it.isNotEmpty()) actionsFile.delete() }
    }

    private fun readActions(): List<OrganizerWidgetAction> = runCatching {
        val array = JSONArray(actionsFile.takeIf { it.exists() }?.readText() ?: return emptyList())
        (0 until array.length()).map { i ->
            val json = array.getJSONObject(i)
            OrganizerWidgetAction(json.getString("reminderId"), if (json.isNull("dueField")) null else json.optString("dueField"), json.optLong("at"))
        }
    }.getOrDefault(emptyList())

    private fun writeActions(actions: List<OrganizerWidgetAction>) = writeAtomically(actionsFile, JSONArray(actions.map {
        JSONObject().put("reminderId", it.reminderId).put("dueField", it.dueField ?: JSONObject.NULL).put("at", it.at)
    }).toString())

    private fun writeAtomically(file: File, text: String) {
        directory.mkdirs()
        val temporary = File(directory, "${file.name}.tmp")
        temporary.writeText(text)
        temporary.renameTo(file)
    }

    private companion object {
        val LOCK = Any()
    }
}

// Everything the widgets could want, from this device's copy. Kept small, since it's read on
// every widget redraw: two weeks of events, the first 40 reminders, 12 notes.
fun OrganizerEngine.widgetSnapshot(
    enabled: Boolean,
    now: ZonedDateTime = ZonedDateTime.now(),
    days: Long = 14,
    skippingReminders: Set<String> = emptySet(),
): OrganizerWidgetSnapshot {
    val zone = now.zone
    val from = now.toLocalDate().atStartOfDay(zone)
    val to = from.plusDays(days)
    val nowMillis = now.toInstant().toEpochMilli()
    val spaces = visibleSpaces

    val events = mutableListOf<OrganizerWidgetSnapshot.Event>()
    for ((holiday, start, end) in holidaysBetween(from, to)) {
        events += OrganizerWidgetSnapshot.Event(
            "holiday-${holiday.id}", holiday.id, holiday.name, start.toInstant().toEpochMilli(), end.toInstant().toEpochMilli(),
            allDay = true, color = "#%06x".format(HolidayColors.argb(holiday.setId) and 0xFFFFFF), holiday = true,
        )
    }
    for (event in items(OrganizerKinds.EVENT)) {
        val spaceIndex = spaces.indexOfFirst { it.id == event.spaceId }.coerceAtLeast(0)
        val color = event.string("color") ?: spaces.firstOrNull { it.id == event.spaceId }?.color
        for (occurrence in Recurrence.occurrences(event, from, to, zone)) {
            val start = occurrence.start.toInstant().toEpochMilli()
            val end = occurrence.end.toInstant().toEpochMilli()
            if (end <= nowMillis) continue
            events += OrganizerWidgetSnapshot.Event(
                "${event.id}-${start / 1000}", event.id, event.title.ifEmpty { "Event" }, start, end, event.bool("allDay"),
                event.string("location")?.takeIf { it.isNotEmpty() }, color, spaceIndex,
            )
        }
    }
    val sortedEvents = events.sortedWith(compareBy({ it.start }, { if (it.holiday) 0 else 1 }, { it.title }))

    val reminders = items(OrganizerKinds.REMINDER)
        .filter { it.parentId == null && !it.bool("completed") && it.id !in skippingReminders }
        .map { reminder ->
            val allDay = reminder.bool("allDay")
            val dueField = reminder.string("due")?.takeIf { it.isNotEmpty() }
            OrganizerWidgetSnapshot.Reminder(
                reminder.id, reminder.title.ifEmpty { "Reminder" },
                OrganizerDates.parse(dueField, allDay, zone)?.toInstant()?.toEpochMilli(), dueField, allDay,
                reminder.bool("flagged"), reminder.number("priority")?.toInt() ?: 0, reminder.string("recurrence") != null,
            )
        }
        .sortedWith(
            compareBy<OrganizerWidgetSnapshot.Reminder> { it.due == null }
                .thenBy { it.due ?: 0 }
                .thenByDescending { if (it.due == null && it.flagged) 1 else 0 }
                .thenByDescending { it.priority }
                .thenBy { it.title },
        )

    val notes = items(OrganizerKinds.NOTE)
        .filter { !it.bool("archived") }
        .sortedWith(compareByDescending<com.sentinelcore.android.organizer.OrganizerItem> { it.bool("pinned") }.thenByDescending { it.updatedAt })
        .take(12)
        .map { note ->
            OrganizerWidgetSnapshot.Note(note.id, note.title, widgetPreview(note.string("body").orEmpty()), note.bool("pinned"), note.updatedAt?.toEpochMilli())
        }

    return OrganizerWidgetSnapshot(nowMillis, enabled, sortedEvents.take(60), reminders.take(40), notes)
}

// A note's body as plain lines: the Markdown marks a widget would otherwise show literally.
internal fun widgetPreview(body: String): String = body.lines()
    .map { line ->
        var text = line.trim()
        listOf("- [ ] ", "- [x] ", "- [X] ", "* ", "- ", "> ").firstOrNull { text.startsWith(it) }?.let { text = text.removePrefix(it) }
        text.trimStart('#').replace("**", "").replace("`", "").trim()
    }
    .filter { it.isNotEmpty() }
    .joinToString("\n")
    .take(300)

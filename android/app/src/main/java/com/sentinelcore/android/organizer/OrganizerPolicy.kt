package com.sentinelcore.android.organizer

import java.time.Instant
import java.time.ZoneId
import java.time.ZonedDateTime
import org.json.JSONObject

// How much of the Organizer this device keeps for use with no connection, chosen in Settings.
// Phones default light; see OrganizerKit's OfflinePolicy, which this matches rule for rule.
data class OfflinePolicy(
    val mode: Mode,
    // Light mode only: how far back to keep. Null keeps everything.
    val windowDays: Int?,
    val attachments: Attachments,
    // Attachment files only - items are tiny. Null is no limit.
    val storageCapBytes: Long?,
) {
    enum class Mode { OFF, LIGHT, FULL }
    enum class Attachments { NONE, THUMBNAILS, FULL }

    fun widens(old: OfflinePolicy): Boolean {
        fun rank(policy: OfflinePolicy): Long = when (policy.mode) {
            Mode.OFF -> 0
            Mode.LIGHT -> policy.windowDays?.let { 1L + it } ?: (Long.MAX_VALUE - 1)
            Mode.FULL -> Long.MAX_VALUE
        }
        return rank(this) > rank(old)
    }

    // Item ids a light copy doesn't keep. Never an item with edits waiting to go up, never
    // anything pinned to stay offline, never the parents of something kept, and children always
    // go with their parent.
    fun itemsToPrune(
        items: Map<String, OrganizerItem>,
        protected: Set<String>,
        now: Instant = Instant.now(),
        zone: ZoneId = ZoneId.systemDefault(),
    ): Set<String> {
        if (mode != Mode.LIGHT || windowDays == null) return emptySet()
        val cutoff = ZonedDateTime.ofInstant(now, zone).minusDays(windowDays.toLong())

        fun root(item: OrganizerItem): OrganizerItem {
            var current = item
            val seen = mutableSetOf(item.id)
            while (true) {
                val parent = current.parentId?.let { items[it] } ?: return current
                if (!seen.add(parent.id)) return current
                current = parent
            }
        }

        fun keepsOnItsOwn(item: OrganizerItem): Boolean {
            if (item.deleted) return true
            val updated = ZonedDateTime.ofInstant(item.updatedAt ?: now, zone)
            return when (item.kind) {
                OrganizerKinds.EVENT -> when (val last = Recurrence.lastEnd(item, zone)) {
                    null, Recurrence.LastEnd.Forever -> true
                    is Recurrence.LastEnd.At -> !last.end.isBefore(cutoff)
                }
                OrganizerKinds.REMINDER -> {
                    if (!item.bool("completed")) true
                    else {
                        val completed = OrganizerJson.instant(item.string("completedAt"))?.let { ZonedDateTime.ofInstant(it, zone) } ?: updated
                        !completed.isBefore(cutoff)
                    }
                }
                OrganizerKinds.NOTE -> item.bool("pinned") || !updated.isBefore(cutoff)
                // A board is a working surface; only finished cards age out.
                OrganizerKinds.CARD -> !item.bool("completed") || !updated.isBefore(cutoff)
                else -> true
            }
        }

        val keep = mutableSetOf<String>()
        for (item in items.values) {
            val top = root(item)
            val kept = if (top.id == item.id) keepsOnItsOwn(item) else keepsOnItsOwn(top) && keepsOnItsOwn(item)
            if (kept || item.id in protected || top.id in protected) keep += item.id
        }
        for (id in keep.toList()) {
            var parentId = items[id]?.parentId
            var guard = 0
            while (parentId != null && guard++ < 64) {
                keep += parentId
                parentId = items[parentId]?.parentId
            }
        }
        return items.keys - keep
    }

    fun toJson(): String = JSONObject().apply {
        put("mode", mode.name)
        put("windowDays", windowDays ?: JSONObject.NULL)
        put("attachments", attachments.name)
        put("storageCapBytes", storageCapBytes ?: JSONObject.NULL)
    }.toString()

    companion object {
        val PHONE_DEFAULT = OfflinePolicy(Mode.LIGHT, 90, Attachments.THUMBNAILS, 500L * 1024 * 1024)
        val FULL_DEFAULT = OfflinePolicy(Mode.FULL, null, Attachments.FULL, null)

        fun fromJson(text: String?): OfflinePolicy? = runCatching {
            val json = JSONObject(text ?: return null)
            OfflinePolicy(
                mode = Mode.valueOf(json.getString("mode")),
                windowDays = if (json.isNull("windowDays")) null else json.getInt("windowDays"),
                attachments = Attachments.valueOf(json.getString("attachments")),
                storageCapBytes = if (json.isNull("storageCapBytes")) null else json.getLong("storageCapBytes"),
            )
        }.getOrNull()
    }
}

// A reminder or event alert this device should raise itself - local, so it fires with no signal
// and with the server down.
data class PlannedAlert(
    val id: String,
    val itemId: String,
    val fireAtMillis: Long,
    val title: String,
    val body: String,
)

object AlertPlanner {
    // All-day things alert relative to this hour on their day, as Apple Calendar and Reminders do.
    const val ALL_DAY_HOUR = 9

    fun plan(
        items: Collection<OrganizerItem>,
        now: Instant = Instant.now(),
        horizonDays: Long = 14,
        limit: Int = 48,
        zone: ZoneId = ZoneId.systemDefault(),
        formatTime: (ZonedDateTime) -> String = { it.toLocalTime().toString().take(5) },
    ): List<PlannedAlert> {
        val nowZoned = ZonedDateTime.ofInstant(now, zone)
        val end = nowZoned.plusDays(horizonDays)
        val alerts = mutableListOf<PlannedAlert>()

        fun base(date: ZonedDateTime, allDay: Boolean) = if (allDay) date.withHour(ALL_DAY_HOUR).withMinute(0).withSecond(0) else date
        fun minutes(item: OrganizerItem) = item.list("alerts").mapNotNull { (it as? Number)?.toInt() }

        for (item in items) {
            if (item.deleted) continue
            when (item.kind) {
                OrganizerKinds.EVENT -> {
                    val offsets = minutes(item).takeIf { it.isNotEmpty() } ?: continue
                    val latest = end.plusMinutes((offsets.maxOrNull() ?: 0).toLong())
                    val allDay = item.bool("allDay")
                    for (occurrence in Recurrence.occurrences(item, nowZoned, latest, zone)) {
                        for (offset in offsets) {
                            val fire = base(occurrence.start, allDay).minusMinutes(offset.toLong())
                            if (!fire.isAfter(nowZoned) || fire.isAfter(end)) continue
                            var body = if (allDay) "All day" else formatTime(occurrence.start)
                            item.string("location")?.takeIf { it.isNotBlank() }?.let { body += " · $it" }
                            alerts += PlannedAlert("organizer.${item.id}.${fire.toEpochSecond()}.$offset", item.id,
                                fire.toInstant().toEpochMilli(), item.title.ifEmpty { "Event" }, body)
                        }
                    }
                }
                OrganizerKinds.REMINDER -> {
                    if (item.bool("completed")) continue
                    val allDay = item.bool("allDay")
                    val due = OrganizerDates.parse(item.string("due"), allDay, zone) ?: continue
                    // A due date on a reminder is a request to be reminded then.
                    val offsets = minutes(item).ifEmpty { listOf(0) }
                    for (offset in offsets) {
                        val fire = base(due, allDay).minusMinutes(offset.toLong())
                        if (!fire.isAfter(nowZoned) || fire.isAfter(end)) continue
                        alerts += PlannedAlert("organizer.${item.id}.${fire.toEpochSecond()}.$offset", item.id,
                            fire.toInstant().toEpochMilli(), item.title.ifEmpty { "Reminder" },
                            item.string("notes")?.lineSequence()?.firstOrNull()?.takeIf { it.isNotBlank() } ?: "Reminder")
                    }
                }
            }
        }
        return alerts.sortedBy { it.fireAtMillis }.take(limit)
    }
}

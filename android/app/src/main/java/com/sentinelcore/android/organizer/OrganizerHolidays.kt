package com.sentinelcore.android.organizer

import java.time.LocalDate
import java.time.ZoneId
import java.time.ZonedDateTime
import org.json.JSONArray
import org.json.JSONObject

// Holiday calendars - US federal holidays, Christian, Jewish, Islamic and other religious
// holidays - that people can add to the calendar. The server works the dates out; this is what the
// phone keeps of them. See ORGANIZER_SYNC.md, "Holiday calendars", and OrganizerKit's
// Holidays.swift. None is shown unless the person turns it on.

data class HolidaySet(val id: String, val name: String, val description: String)

data class Holiday(val id: String, val setId: String, val name: String, val date: String, val days: Int = 1, val note: String? = null) {
    val dayCount: Int get() = days.coerceAtLeast(1)

    // Local midnight on its first day, and on the day after its last.
    fun span(zone: ZoneId): Pair<ZonedDateTime, ZonedDateTime>? {
        val start = runCatching { LocalDate.parse(date.take(10)) }.getOrNull() ?: return null
        return start.atStartOfDay(zone) to start.plusDays(dayCount.toLong()).atStartOfDay(zone)
    }
}

// The chosen sets (and whether that choice has reached the server), the list of sets there are,
// and the days themselves for last year through two years ahead.
data class HolidayState(
    val sets: List<String> = emptyList(),
    val dirty: Boolean = false,
    val catalog: List<HolidaySet>? = null,
    val key: String? = null,
    val list: List<Holiday> = emptyList(),
) {
    fun toJson(): String = JSONObject().apply {
        put("sets", JSONArray(sets))
        put("dirty", dirty)
        put("catalog", catalog?.let { JSONArray(it.map(HolidayJson::setJson)) } ?: JSONObject.NULL)
        put("key", key ?: JSONObject.NULL)
        put("list", JSONArray(list.map(HolidayJson::holidayJson)))
    }.toString()

    companion object {
        fun fromJson(text: String?): HolidayState? = runCatching {
            val json = JSONObject(text ?: return null)
            HolidayState(
                sets = HolidayJson.strings(json.optJSONArray("sets")),
                dirty = json.optBoolean("dirty"),
                catalog = json.optJSONArray("catalog")?.let(HolidayJson::sets),
                key = json.optString("key").takeIf { it.isNotEmpty() && it != "null" },
                list = json.optJSONArray("list")?.let(HolidayJson::holidays) ?: emptyList(),
            )
        }.getOrNull()
    }
}

// One color per holiday calendar, the same in every app.
object HolidayColors {
    fun argb(setId: String): Long = when (setId) {
        "us-federal" -> 0xFF4A9DFF
        "christian" -> 0xFFC98BFF
        "orthodox" -> 0xFFFF9A52
        "jewish" -> 0xFF4AE8FF
        "islamic" -> 0xFF7FD67F
        "hindu" -> 0xFFFFD84A
        "chinese" -> 0xFFFF4D5E
        else -> 0xFF93A2AD
    }
}

object HolidayJson {
    fun strings(array: JSONArray?): List<String> = array?.let { (0 until it.length()).map(it::getString) } ?: emptyList()

    fun sets(array: JSONArray): List<HolidaySet> = (0 until array.length()).map { i ->
        val json = array.getJSONObject(i)
        HolidaySet(json.getString("id"), json.optString("name"), json.optString("description"))
    }

    fun holidays(array: JSONArray): List<Holiday> = (0 until array.length()).mapNotNull { i ->
        runCatching {
            val json = array.getJSONObject(i)
            Holiday(
                json.getString("id"), json.getString("setId"), json.getString("name"), json.getString("date"),
                json.optInt("days", 1), json.optString("note").takeIf { it.isNotEmpty() && it != "null" },
            )
        }.getOrNull()
    }

    fun setJson(set: HolidaySet) = JSONObject().put("id", set.id).put("name", set.name).put("description", set.description)

    fun holidayJson(holiday: Holiday) = JSONObject().put("id", holiday.id).put("setId", holiday.setId).put("name", holiday.name)
        .put("date", holiday.date).put("days", holiday.days).put("note", holiday.note ?: JSONObject.NULL)
}

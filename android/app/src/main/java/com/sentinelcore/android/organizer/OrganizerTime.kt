package com.sentinelcore.android.organizer

import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.time.temporal.ChronoUnit

// Field clocks: "<13-digit unix ms>-<4-digit counter>-<device id>", compared as plain strings.
// Hybrid logical: never runs backwards and never falls behind a clock this device has seen, so an
// edit made here always sorts after the version of the field it replaced.
class HybridClock(deviceId: String) {
    val deviceId: String = deviceId.filter { it.isLetterOrDigit() && it.code < 128 || it in "_.:-" }.take(64).ifEmpty { "android" }
    private var lastMillis = 0L
    private var lastCounter = 0

    @Synchronized
    fun next(nowMillis: Long = System.currentTimeMillis()): String {
        if (nowMillis > lastMillis) {
            lastMillis = nowMillis
            lastCounter = 0
        } else {
            lastCounter = (lastCounter + 1) % 10_000
        }
        return format(lastMillis, lastCounter, deviceId)
    }

    @Synchronized
    fun observe(clock: String) {
        val parts = clock.split("-", limit = 3)
        if (parts.size != 3 || parts[0].length != 13 || parts[1].length != 4) return
        val millis = parts[0].toLongOrNull() ?: return
        val counter = parts[1].toIntOrNull() ?: return
        if (millis > lastMillis || (millis == lastMillis && counter > lastCounter)) {
            lastMillis = millis
            lastCounter = counter
        }
    }

    companion object {
        fun format(millis: Long, counter: Int, device: String) = "%013d-%04d-%s".format(millis, counter, device)
    }
}

// The date formats inside Organizer fields: a timed moment is ISO 8601 with its offset
// ("2026-09-23T17:00:00-05:00"), an all-day one is a bare "2026-09-23".
object OrganizerDates {
    private val offsetFormat = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ssxxx")

    fun parse(value: String?, allDay: Boolean, zone: ZoneId = ZoneId.systemDefault()): ZonedDateTime? {
        if (value.isNullOrBlank()) return null
        return if (allDay || value.length == 10) {
            runCatching { LocalDate.parse(value.take(10)).atStartOfDay(zone) }.getOrNull()
        } else {
            runCatching { OffsetDateTime.parse(value).atZoneSameInstant(zone) }.getOrNull()
        }
    }

    fun ymd(date: ZonedDateTime): String = date.toLocalDate().toString()

    fun isoLocal(date: ZonedDateTime): String = date.withNano(0).toOffsetDateTime().format(offsetFormat)

    fun fieldValue(date: ZonedDateTime, allDay: Boolean): String = if (allDay) ymd(date) else isoLocal(date)

    fun timestamp(instant: Instant = Instant.now()): String = instant.toString()
}

// Repeating events and reminders: FREQ DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, BYDAY,
// BYMONTHDAY, COUNT and UNTIL - the subset every client supports (see ORGANIZER_SYNC.md). A port
// of OrganizerKit's Recurrence, walking local calendar fields so a weekly 9am meeting stays at 9am
// across a daylight-saving change.
data class RecurrenceRule(
    val frequency: String,
    val interval: Int = 1,
    val count: Int? = null,
    val until: ZonedDateTime? = null,
    val byWeekday: List<Int>? = null, // 0 = Sunday ... 6 = Saturday
    val byMonthDay: List<Int>? = null,
) {
    val rruleValue: String
        get() = buildList {
            add("FREQ=$frequency")
            if (interval > 1) add("INTERVAL=$interval")
            byWeekday?.takeIf { it.isNotEmpty() }?.let { days -> add("BYDAY=" + days.joinToString(",") { CODES[it] }) }
            byMonthDay?.takeIf { it.isNotEmpty() }?.let { add("BYMONTHDAY=" + it.joinToString(",")) }
            count?.let { add("COUNT=$it") }
            until?.let { add("UNTIL=" + it.toLocalDate().format(DateTimeFormatter.BASIC_ISO_DATE)) }
        }.joinToString(";")

    // Every start of the series from `start` on, in order.
    fun starts(start: ZonedDateTime, limit: Int = 3000): Sequence<ZonedDateTime> = sequence {
        var produced = 0
        val time = start.toLocalTime()
        for (step in 0 until limit) {
            val candidates: List<ZonedDateTime> = when (frequency) {
                "DAILY" -> listOf(start.plusDays(step.toLong() * interval))
                "WEEKLY" -> {
                    val weekday = start.dayOfWeek.value % 7
                    val weekStart = start.toLocalDate().minusDays(weekday.toLong()).plusWeeks(step.toLong() * interval)
                    (byWeekday?.takeIf { it.isNotEmpty() } ?: listOf(weekday)).sorted()
                        .map { ZonedDateTime.of(weekStart.plusDays(it.toLong()), time, start.zone) }
                }
                "MONTHLY" -> {
                    val month = start.toLocalDate().withDayOfMonth(1).plusMonths(step.toLong() * interval)
                    val last = month.lengthOfMonth()
                    (byMonthDay?.takeIf { it.isNotEmpty() } ?: listOf(start.dayOfMonth))
                        .map { if (it < 0) last + it + 1 else it }
                        .filter { it in 1..last }
                        .sorted()
                        .map { ZonedDateTime.of(month.withDayOfMonth(it), time, start.zone) }
                }
                "YEARLY" -> {
                    val year = start.year + step * interval
                    // 29 February only happens in leap years; other years skip it.
                    if (start.monthValue == 2 && start.dayOfMonth == 29 && !java.time.Year.isLeap(year.toLong())) emptyList()
                    else listOf(ZonedDateTime.of(LocalDate.of(year, start.monthValue, start.dayOfMonth), time, start.zone))
                }
                else -> return@sequence
            }
            for (date in candidates) {
                if (date.isBefore(start)) continue
                if (until != null && date.isAfter(until)) return@sequence
                produced++
                if (count != null && produced > count) return@sequence
                yield(date)
            }
        }
    }

    companion object {
        private val CODES = listOf("SU", "MO", "TU", "WE", "TH", "FR", "SA")

        fun parse(text: String?, zone: ZoneId = ZoneId.systemDefault()): RecurrenceRule? {
            if (text.isNullOrBlank()) return null
            val parts = text.split(";").mapNotNull { part ->
                val pair = part.split("=", limit = 2)
                if (pair.size == 2) pair[0].uppercase() to pair[1] else null
            }.toMap()
            val frequency = parts["FREQ"]?.takeIf { it in setOf("DAILY", "WEEKLY", "MONTHLY", "YEARLY") } ?: return null
            return RecurrenceRule(
                frequency = frequency,
                interval = (parts["INTERVAL"]?.toIntOrNull() ?: 1).coerceAtLeast(1),
                count = parts["COUNT"]?.toIntOrNull(),
                until = parts["UNTIL"]?.let { parseUntil(it, zone) },
                byWeekday = parts["BYDAY"]?.split(",")?.mapNotNull { code -> CODES.indexOf(code.takeLast(2)).takeIf { it >= 0 } },
                byMonthDay = parts["BYMONTHDAY"]?.split(",")?.mapNotNull { it.toIntOrNull() },
            )
        }

        // "20261231" means the end of that local day; "20261231T000000Z" is an exact UTC instant.
        private fun parseUntil(text: String, zone: ZoneId): ZonedDateTime? {
            val digits = text.filter { it.isDigit() }
            if (digits.length < 8) return null
            val date = runCatching { LocalDate.parse(digits.take(8), DateTimeFormatter.BASIC_ISO_DATE) }.getOrNull() ?: return null
            if (digits.length >= 14) {
                val time = LocalTime.of(digits.substring(8, 10).toInt(), digits.substring(10, 12).toInt(), digits.substring(12, 14).toInt())
                return if (text.endsWith("Z")) ZonedDateTime.of(date, time, ZoneOffset.UTC).withZoneSameInstant(zone)
                else ZonedDateTime.of(date, time, zone)
            }
            return ZonedDateTime.of(date, LocalTime.of(23, 59, 59), zone)
        }
    }
}

data class Occurrence(
    val item: OrganizerItem,
    val start: ZonedDateTime,
    val end: ZonedDateTime,
    // The start this occurrence has in its series, in field format - what goes in exdates or a
    // detached copy's originalStart. Null for an event that doesn't repeat.
    val originalStart: String?,
)

object Recurrence {
    fun span(event: OrganizerItem, zone: ZoneId = ZoneId.systemDefault()): Pair<ZonedDateTime, ZonedDateTime>? {
        val allDay = event.bool("allDay")
        val start = OrganizerDates.parse(event.string("start"), allDay, zone) ?: return null
        var end = OrganizerDates.parse(event.string("end"), allDay, zone)
        if (end == null || !end.isAfter(start)) end = if (allDay) start.plusDays(1) else start.plusHours(1)
        return start to end
    }

    fun occurrences(event: OrganizerItem, from: ZonedDateTime, to: ZonedDateTime, zone: ZoneId = ZoneId.systemDefault()): List<Occurrence> {
        val (start, end) = span(event, zone) ?: return emptyList()
        val duration = ChronoUnit.SECONDS.between(start, end)
        val allDay = event.bool("allDay")
        val rule = RecurrenceRule.parse(event.string("recurrence"), zone)
            ?: return if (start.isBefore(to) && end.isAfter(from)) listOf(Occurrence(event, start, end, null)) else emptyList()

        val skipped = event.list("exdates").mapNotNull { value ->
            OrganizerDates.parse(value as? String, allDay, zone)?.let { key(it, allDay) }
        }.toSet()
        val out = mutableListOf<Occurrence>()
        for (occurrenceStart in rule.starts(start)) {
            if (!occurrenceStart.isBefore(to)) break
            val occurrenceEnd = occurrenceStart.plusSeconds(duration)
            if (!occurrenceEnd.isAfter(from) || key(occurrenceStart, allDay) in skipped) continue
            out += Occurrence(event, occurrenceStart, occurrenceEnd, OrganizerDates.fieldValue(occurrenceStart, allDay))
        }
        return out
    }

    // When the last occurrence ends: null when it repeats forever or has no usable start.
    sealed interface LastEnd {
        data object Forever : LastEnd
        data class At(val end: ZonedDateTime) : LastEnd
    }

    fun lastEnd(event: OrganizerItem, zone: ZoneId = ZoneId.systemDefault()): LastEnd? {
        val (start, end) = span(event, zone) ?: return null
        val rule = RecurrenceRule.parse(event.string("recurrence"), zone) ?: return LastEnd.At(end)
        if (rule.until == null && rule.count == null) return LastEnd.Forever
        val last = rule.starts(start).lastOrNull() ?: start
        return LastEnd.At(last.plusSeconds(ChronoUnit.SECONDS.between(start, end)))
    }

    // The occurrence after the one just completed - the same rule as the dashboard and the Apple
    // apps, so a weekly chore ticked off late doesn't silently skip a week.
    fun nextDue(reminder: OrganizerItem, zone: ZoneId = ZoneId.systemDefault()): String? {
        val allDay = reminder.bool("allDay")
        val due = OrganizerDates.parse(reminder.string("due"), allDay, zone) ?: return null
        val rule = RecurrenceRule.parse(reminder.string("recurrence"), zone) ?: return null
        return rule.starts(due).firstOrNull { it.isAfter(due) }?.let { OrganizerDates.fieldValue(it, allDay) }
    }

    private fun key(date: ZonedDateTime, allDay: Boolean) = if (allDay) OrganizerDates.ymd(date) else date.toEpochSecond().toString()
}

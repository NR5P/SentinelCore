package com.sentinelcore.android.organizer

import java.time.LocalDate
import java.time.ZoneId
import java.time.LocalTime
import java.time.ZonedDateTime
import java.time.temporal.ChronoUnit

// A spoken reminder split into what to do and when: "call mom tomorrow at 5pm" is "Call mom", due
// tomorrow at 5 PM. The voice shortcuts and Google Assistant hand the app the words as said, so the
// date has to come out of them here.
//
// The Apple apps have the same rules in OrganizerKit's VoicePhrase.swift, and both test suites
// check the examples in core/docs/voice-phrase-cases.json - a phrase means the same thing whichever
// phone heard it. Change them together.
data class VoicePhrase(
    val title: String,
    val due: ZonedDateTime? = null,
    // False for a date with no time ("dentist Friday"), which is stored as an all-day due date.
    val hasTime: Boolean = false,
) {
    // The value for a reminder's `due` field (ORGANIZER_SYNC.md), or null for none.
    fun dueField(): String? = due?.let { if (hasTime) OrganizerDates.isoLocal(it) else it.toLocalDate().toString() }

    companion object {
        fun parse(text: String, now: ZonedDateTime = ZonedDateTime.now()): VoicePhrase = Parser(text, now).run()

        // Morning, afternoon, evening and night as clock hours - Apple Reminders' choices.
        internal val periodHours = mapOf("morning" to 9, "afternoon" to 14, "evening" to 18, "night" to 20, "tonight" to 20)

        internal val numberWords = mapOf(
            "a" to 1, "an" to 1, "one" to 1, "two" to 2, "three" to 3, "four" to 4, "five" to 5, "six" to 6, "seven" to 7,
            "eight" to 8, "nine" to 9, "ten" to 10, "eleven" to 11, "twelve" to 12, "fifteen" to 15, "twenty" to 20,
            "thirty" to 30, "forty-five" to 45, "forty five" to 45, "ninety" to 90,
        )

        internal val weekdays = listOf("monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday")

        internal val months = listOf("jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec")
    }
}

private class Parser(private val text: String, private val now: ZonedDateTime) {
    private val removed = mutableListOf<IntRange>()
    private val zone: ZoneId = now.zone
    private val today: LocalDate = now.toLocalDate()

    private data class Time(val hour: Int, val minute: Int, val certain: Boolean)

    companion object {
        private fun regex(pattern: String) = Regex(pattern, RegexOption.IGNORE_CASE)

        private const val NUMBER = """(half an|an?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty|forty[- ]five|ninety|\d+)"""
        private val relative = regex("""\b(?:in|within)\s+$NUMBER\s+(minutes?|mins?|hours?|hrs?|days?|weeks?)\b""")
        private val dayWord = regex("""\b(?:(?:on|by|this|next)\s+)?(today|tonight|tomorrow|sunday|monday|tuesday|wednesday|thursday|friday|saturday)(?:\s+(morning|afternoon|evening|night))?\b""")
        private val thisPeriod = regex("""\bthis\s+(morning|afternoon|evening)\b""")
        private val monthDate = regex("""\b(?:(?:on|by)\s+)?(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec)\.?\s+(\d{1,2})(?:st|nd|rd|th)?\b""")
        private val ordinalDay = regex("""\b(?:(?:on|by)\s+)?the\s+(\d{1,2})(?:st|nd|rd|th)\b""")
        private val meridiemTime = regex("""\b(?:(?:at|by|around)\s+)?(\d{1,2})(?::(\d{2}))?\s*([ap])\.?\s?m\b\.?""")
        private val atTime = regex("""\b(?:at|by|around)\s+(\d{1,2})(?::(\d{2}))?(?:\s*o'?clock)?\b""")
        private val namedTime = regex("""\b(?:(?:at|by|around)\s+)?(noon|midnight)\b""")
        private val periodTime = regex("""\b(?:in\s+the\s+(morning|afternoon|evening)|at\s+(night))\b""")
        private val clockTime = regex("""\b(\d{1,2}):(\d{2})\b""")
        private val lead = regex("""^\s*(?:remind\s+me\s+(?:to\s+|that\s+)?|reminder\s+(?:to\s+|that\s+)?)""")
        private val dangling = regex("""\s+(?:at|on|by|in|around|due)\s*$""")
    }

    private fun first(expression: Regex, avoiding: IntRange? = null): MatchResult? =
        expression.findAll(text).firstOrNull { match -> avoiding == null || match.range.last < avoiding.first || match.range.first > avoiding.last }

    private fun group(match: MatchResult, index: Int): String? = match.groups[index]?.value?.lowercase()

    private fun at(day: LocalDate, hour: Int, minute: Int): ZonedDateTime = ZonedDateTime.of(day, LocalTime.of(hour, minute), zone)

    private fun startOf(day: LocalDate): ZonedDateTime = day.atStartOfDay(zone)

    fun run(): VoicePhrase {
        first(relative)?.let { match ->
            val unit = group(match, 2) ?: return@let
            val amount = amount(group(match, 1), unit) ?: return@let
            removed += match.range
            if (group(match, 1) == "half an") return finish(roundedToMinute(now.plusMinutes(amount.toLong())), true)
            return when {
                unit.startsWith("m") -> finish(roundedToMinute(now.plusMinutes(amount.toLong())), true)
                unit.startsWith("h") -> finish(roundedToMinute(now.plusHours(amount.toLong())), true)
                unit.startsWith("w") -> finish(startOf(today.plusWeeks(amount.toLong())), false)
                else -> finish(startOf(today.plusDays(amount.toLong())), false)
            }
        }

        // Which day, and a time of day the words carried with it ("tomorrow evening").
        var day: LocalDate? = null
        var dayPeriod: Int? = null
        var dayRange: IntRange? = null
        val dayMatch = first(dayWord)
        val periodMatch = if (dayMatch == null) first(thisPeriod) else null
        val monthMatch = if (dayMatch == null && periodMatch == null) first(monthDate) else null
        if (dayMatch != null) {
            dayRange = dayMatch.range
            dayPeriod = group(dayMatch, 2)?.let { VoicePhrase.periodHours[it] }
            when (val word = group(dayMatch, 1)) {
                "today" -> day = today
                "tonight" -> { day = today; dayPeriod = dayPeriod ?: VoicePhrase.periodHours["tonight"] }
                "tomorrow" -> day = today.plusDays(1)
                else -> {
                    val index = VoicePhrase.weekdays.indexOf(word)
                    if (index >= 0) {
                        var ahead = (index + 1 - today.dayOfWeek.value + 7) % 7
                        if (ahead == 0) ahead = 7
                        day = today.plusDays(ahead.toLong())
                    }
                }
            }
        } else if (periodMatch != null) {
            dayRange = periodMatch.range
            day = today
            dayPeriod = VoicePhrase.periodHours[group(periodMatch, 1)]
        } else if (monthMatch != null) {
            val name = group(monthMatch, 1).orEmpty()
            val number = group(monthMatch, 2)?.toIntOrNull()
            val month = VoicePhrase.months.indexOfFirst { name.startsWith(it) }
            val date = if (number != null && month >= 0) validDate(today.year, month + 1, number) else null
            if (date != null) {
                dayRange = monthMatch.range
                day = if (date < today) validDate(today.year + 1, month + 1, number!!) else date
            }
        } else {
            first(ordinalDay)?.let { match ->
                val number = group(match, 1)?.toIntOrNull() ?: return@let
                val date = (if (number >= today.dayOfMonth) validDate(today.year, today.monthValue, number) else null)
                    ?: today.plusMonths(1).let { validDate(it.year, it.monthValue, number) }
                if (date != null) {
                    dayRange = match.range
                    day = date
                }
            }
        }
        dayRange?.let { removed += it }

        // The time of day: hour and minute, and whether AM/PM was said.
        var time: Time? = null
        val meridiem = first(meridiemTime, dayRange)?.takeIf { (group(it, 1)?.toIntOrNull() ?: 0) in 1..12 }
        val named = if (meridiem == null) first(namedTime, dayRange) else null
        val atMatch = if (meridiem == null && named == null) first(atTime, dayRange)?.takeIf { (group(it, 1)?.toIntOrNull() ?: 99) <= 23 } else null
        val clock = if (meridiem == null && named == null && atMatch == null) first(clockTime, dayRange)?.takeIf { (group(it, 1)?.toIntOrNull() ?: 99) <= 23 } else null
        val period = if (meridiem == null && named == null && atMatch == null && clock == null) first(periodTime, dayRange) else null
        when {
            meridiem != null -> {
                val hour = group(meridiem, 1)!!.toInt()
                val pm = group(meridiem, 3) == "p"
                time = Time(hour % 12 + if (pm) 12 else 0, group(meridiem, 2)?.toIntOrNull() ?: 0, true)
                removed += meridiem.range
            }
            named != null -> {
                time = Time(if (group(named, 1) == "noon") 12 else 0, 0, true)
                removed += named.range
            }
            atMatch != null || clock != null -> {
                val match = atMatch ?: clock!!
                val hour = group(match, 1)!!.toInt()
                time = Time(hour, group(match, 2)?.toIntOrNull() ?: 0, hour == 0 || hour > 12)
                removed += match.range
            }
            period != null -> {
                val word = group(period, 1) ?: group(period, 2)
                time = Time(VoicePhrase.periodHours[word] ?: 9, 0, true)
                removed += period.range
            }
        }
        time = time?.let { if (it.minute > 59) it.copy(minute = 0) else it }

        val resolvedDay = day
        val resolvedTime = time
        return when {
            resolvedDay == null && resolvedTime == null -> finish(null, false)
            resolvedDay != null && resolvedTime == null -> {
                val hour = dayPeriod ?: return finish(startOf(resolvedDay), false)
                val date = at(resolvedDay, hour, 0)
                // "Tonight" said after 8 PM means soon, not a time already gone.
                if (!date.isAfter(now) && resolvedDay == today) finish(roundedToMinute(now.plusHours(1)), true) else finish(date, true)
            }
            resolvedDay != null -> {
                val hour = if (resolvedTime!!.certain) resolvedTime.hour else guessHour(resolvedTime.hour, dayPeriod)
                finish(at(resolvedDay, hour, resolvedTime.minute), true)
            }
            else -> {
                // No day: the next time the clock reads that, today or tomorrow.
                val hours = if (resolvedTime!!.certain) listOf(resolvedTime.hour) else candidateHours(resolvedTime.hour)
                val upcoming = hours.map { at(today, it, resolvedTime.minute) }.firstOrNull { it.isAfter(now) }
                finish(upcoming ?: at(today.plusDays(1), hours[0], resolvedTime.minute), true)
            }
        }
    }

    // "At 5" is 5 PM - nobody sets a 5 AM reminder without saying so. 7 to 11 could be either.
    private fun candidateHours(hour: Int): List<Int> = when (hour) {
        12 -> listOf(12)
        in 1..6 -> listOf(hour + 12)
        else -> listOf(hour, hour + 12)
    }

    private fun guessHour(hour: Int, period: Int?): Int =
        if (period != null && period >= 12 && hour < 12) hour + 12 else candidateHours(hour)[0]

    // "Half an hour" is the one fraction people say; it comes back as minutes.
    private fun amount(word: String?, unit: String): Int? = when {
        word == null -> null
        word == "half an" -> if (unit.startsWith("h")) 30 else null
        else -> word.toIntOrNull() ?: VoicePhrase.numberWords[word]
    }

    private fun validDate(year: Int, month: Int, day: Int): LocalDate? = runCatching { LocalDate.of(year, month, day) }.getOrNull()

    private fun roundedToMinute(date: ZonedDateTime): ZonedDateTime = date.truncatedTo(ChronoUnit.MINUTES)

    private fun finish(due: ZonedDateTime?, hasTime: Boolean): VoicePhrase {
        var title = text
        for (range in removed.sortedByDescending { it.first }) {
            title = title.replaceRange(range, " ")
        }
        title = lead.replaceFirst(title, "")
        title = title.split(Regex("\\s+")).filter { it.isNotEmpty() }.joinToString(" ")
        while (true) {
            val trimmed = dangling.replace(title, "").trim(' ', ',', '.', ';', ':', '-')
            if (trimmed == title) break
            title = trimmed
        }
        if (title.isEmpty()) title = text.trim()
        title = title.replaceFirstChar { it.uppercaseChar() }
        return VoicePhrase(title, due, due != null && hasTime)
    }
}

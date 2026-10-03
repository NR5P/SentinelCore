package com.sentinelcore.android.organizer

import java.time.ZonedDateTime
import java.util.Locale

// One line said or typed into Quick Add (the widget's microphone and keyboard buttons): "add a
// reminder to take out the trash at 7am tomorrow", "note the garage code is 4412", "dentist Friday
// from 2 to 3pm on my calendar". The words that say which of the three it is come off the front
// (or the end), and the rest goes to VoicePhrase for the date. Nothing said about which is a
// reminder; the dialog lets the person switch before adding.
//
// No model or server: plain rules, instant and offline. The Apple apps have the same rules in
// OrganizerKit's QuickCapture.swift, and both test suites read core/docs/quick-capture-cases.json.
// Change them together.
data class QuickCapture(
    val kind: Kind,
    // True when the words named the kind ("add a note…"); false for the reminder default.
    val named: Boolean,
    // What's left once the words naming the kind are gone.
    val text: String,
) {
    enum class Kind(val organizerKind: String, val label: String) {
        REMINDER(OrganizerKinds.REMINDER, "Reminder"),
        NOTE(OrganizerKinds.NOTE, "Note"),
        EVENT(OrganizerKinds.EVENT, "Event"),
    }

    data class Draft(
        val kind: Kind,
        val title: String,
        // A note's text when it's longer than its title.
        val body: String = "",
        // A reminder's due date, or an event's start. An event always has one: today, all day,
        // when no day was said.
        val start: ZonedDateTime? = null,
        val end: ZonedDateTime? = null,
        // False for a date with no time, which is all-day.
        val hasTime: Boolean = false,
    ) {
        val isEmpty: Boolean get() = title.isBlank()

        // The fields for OrganizerEngine.create (ORGANIZER_SYNC.md), the same ones the editors write.
        fun fields(): Map<String, Any?> = when (kind) {
            Kind.NOTE -> mapOf("title" to title, "body" to body)
            Kind.REMINDER -> buildMap {
                put("title", title)
                put("completed", false)
                start?.let {
                    put("due", OrganizerDates.fieldValue(it, !hasTime))
                    put("allDay", !hasTime)
                }
            }
            Kind.EVENT -> {
                val begins = start ?: ZonedDateTime.now().toLocalDate().atStartOfDay(ZonedDateTime.now().zone)
                val ends = end ?: begins.plusHours(1)
                mapOf(
                    "title" to title,
                    "allDay" to !hasTime,
                    "start" to OrganizerDates.fieldValue(begins, !hasTime),
                    "end" to OrganizerDates.fieldValue(ends, !hasTime),
                    "timeZone" to begins.zone.id,
                )
            }
        }
    }

    // What would be added, as `chosen` or as whatever the words said.
    fun draft(chosen: Kind? = null, now: ZonedDateTime = ZonedDateTime.now()): Draft = when (chosen ?: kind) {
        Kind.NOTE -> {
            val words = (Rules.strip(Rules.reminderLead, text) ?: text).trim()
            val spoken = words.replaceFirstChar { it.uppercaseChar() }
            val title = VoiceCapture.noteTitle(spoken)
            Draft(Kind.NOTE, title, body = if (title == spoken) "" else spoken)
        }
        Kind.REMINDER -> {
            val phrase = VoicePhrase.parse(text, now)
            Draft(Kind.REMINDER, phrase.title, start = phrase.due, hasTime = phrase.hasTime)
        }
        Kind.EVENT -> EventWords(text, now).draft()
    }

    companion object {
        fun parse(text: String): QuickCapture {
            val said = text.trim()
            Rules.strip(Rules.noteLead, said)?.let { return QuickCapture(Kind.NOTE, true, it) }
            // "Schedule dentist on my calendar Friday": the calendar words go too.
            Rules.strip(Rules.eventLead, said)?.let { return QuickCapture(Kind.EVENT, true, Rules.strip(Rules.calendarWords, it) ?: it) }
            Rules.strip(Rules.todoLead, said)?.let { return QuickCapture(Kind.REMINDER, true, it) }
            // "Remind me…" and "add a reminder…" stay on: VoicePhrase takes them off around the date.
            if (Rules.reminderLead.containsMatchIn(said)) return QuickCapture(Kind.REMINDER, true, said)
            // "Put dentist on my calendar Friday from 2 to 3pm": the calendar words in the middle.
            if (Rules.calendarAsk.containsMatchIn(said)) {
                Rules.strip(Rules.calendarWords, said)?.let { return QuickCapture(Kind.EVENT, true, Rules.strip(Rules.addVerb, it) ?: it) }
            }
            Rules.strip(Rules.eventTail, said)?.let { return QuickCapture(Kind.EVENT, true, Rules.strip(Rules.addVerb, it) ?: it) }
            Rules.strip(Rules.reminderTail, said)?.let { return QuickCapture(Kind.REMINDER, true, Rules.strip(Rules.addVerb, it) ?: it) }
            return QuickCapture(Kind.REMINDER, false, said)
        }
    }
}

private object Rules {
    fun regex(pattern: String) = Regex(pattern, RegexOption.IGNORE_CASE)

    // The text without the match, or null when there is none.
    fun strip(expression: Regex, text: String): String? {
        val match = expression.find(text) ?: return null
        return text.replaceRange(match.range, " ").trim()
    }

    // "Add a note that…", "take a note:", "note to self…", "jot down…".
    val noteLead = regex("""^\s*(?:please\s+)?(?:(?:(?:add|take|make|write|create|save|new)\s+(?:a\s+|an?\s+new\s+|new\s+)?)?note\b(?:\s+to\s+self\b)?(?:\s*[:,\-]\s*|\s+(?:that\s+says|saying|that|about|to|of)\b)?|(?:jot|write)\s+down\b)""")
    // "Add an event…", "new calendar event:", "add to my calendar…", "schedule…", and "add a
    // meeting…" - where "meeting" stays, since it's what the event is.
    val eventLead = regex("""^\s*(?:please\s+)?(?:(?:(?:add|create|make|new|put|schedule|set\s+up)\s+(?:an?\s+new\s+|an?\s+|new\s+)?)?(?:calendar\s+)?event\b(?:\s*[:,\-]\s*|\s+(?:for|called|named|to)\b)?|(?:add|put)\s+(?:to|on|in)\s+(?:my\s+|the\s+|our\s+)?calendar\b\s*[:,\-]?|schedule\s+(?:an?\s+)?|(?:add|create|make|book|set\s+up)\s+(?:an?\s+|new\s+)?(?=(?:appointment|meeting)\b))""")
    // "To-do: …", "task: …". "Remind me…" is reminderLead, which VoicePhrase strips itself.
    val todoLead = regex("""^\s*(?:please\s+)?(?:(?:add|new|create)\s+(?:an?\s+)?)?(?:to-?do|task)\b(?:\s*[:,\-]\s*|\s+to\b)?""")
    val reminderLead = regex("""^\s*(?:please\s+)?(?:remind\s+me\b(?:\s+(?:to|that)\b)?|(?:(?:add|set|create|make|new|put)\s+(?:a\s+|an?\s+new\s+|new\s+)?)?reminder\b(?:\s+(?:to|that|for(?:\s+me\s+to)?)\b)?)""")
    // "…to my calendar", "…on the calendar".
    val eventTail = regex("""\s+(?:to|on|in|onto)\s+(?:my\s+|the\s+|our\s+)?(?:family\s+)?calendar\s*[.!]?\s*$""")
    // "Add/put/schedule <something> on my calendar <when>": a verb, then the calendar words anywhere.
    val calendarAsk = regex("""^\s*(?:please\s+)?(?:add|put|schedule)\s+\S.*?\s(?:to|on|in|onto)\s+(?:my\s+|the\s+|our\s+)?(?:family\s+)?calendar\b""")
    // The first "on my calendar", "to the calendar:", wherever it is.
    val calendarWords = regex("""(?:^\s*|\s+)(?:to|on|in|onto)\s+(?:my\s+|the\s+|our\s+)?(?:family\s+)?calendar\b\s*[:,.!\-]?""")
    // "…to my reminders", "…on the to-do list".
    val reminderTail = regex("""\s+(?:to|on|in|onto)\s+(?:my\s+|the\s+|our\s+)?(?:reminders?|to-?dos?|to\s+do|tasks?)(?:\s+list)?\s*[.!]?\s*$""")
    // The verb left at the front once a tail named the kind: "add … to my calendar".
    val addVerb = regex("""^\s*(?:please\s+)?(?:add|put|schedule)\b(?:\s+an?\s+event\b)?""")
}

// An event's day and time, and how long it runs: "from 2 to 3pm", "11-1pm", "for 90 minutes".
// The start goes back through VoicePhrase, so an event and a reminder read a day the same way.
private class EventWords(private val text: String, private val now: ZonedDateTime) {
    private data class Clock(val hour: Int, val minute: Int, val meridiem: String?)

    companion object {
        private const val NUMBER = """(an?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty|forty[- ]five|ninety|\d+)"""
        private val duration = Rules.regex("""\bfor\s+(?:(half\s+an\s+hour)|(an?\s+hour\s+and\s+a\s+half)|$NUMBER\s+(minutes?|mins?|hours?|hrs?))\b""")
        // Counts only with "from" or an AM/PM, so "call 2 to 3 people" isn't a time.
        private val range = Rules.regex("""\b(from\s+)?(?:at\s+)?(\d{1,2})(?::(\d{2}))?(?:\s*([ap])\.?\s?m\b\.?)?\s*(?:to|until|till|through|-|–)\s*(\d{1,2})(?::(\d{2}))?(?![\d:])(?:\s*([ap])\.?\s?m\b\.?)?""")
    }

    fun draft(): QuickCapture.Draft {
        var words = text
        var minutes = 60L
        duration.find(words)?.let { match ->
            val groups = match.groups
            when {
                groups[1] != null -> minutes = 30
                groups[2] != null -> minutes = 90
                else -> {
                    val word = groups[3]?.value?.lowercase()
                    val amount = word?.let { it.toIntOrNull() ?: VoicePhrase.numberWords[it] }
                    val unit = groups[4]?.value?.lowercase()
                    if (amount != null && unit != null) minutes = if (unit.startsWith("h")) amount * 60L else amount.toLong()
                }
            }
            words = words.replaceRange(match.range, " ")
        }

        var endClock: Clock? = null
        range.find(words)?.let { match ->
            val groups = match.groups
            val startHour = groups[2]?.value?.toIntOrNull() ?: return@let
            val endHour = groups[5]?.value?.toIntOrNull() ?: return@let
            var start = Clock(startHour, groups[3]?.value?.toIntOrNull() ?: 0, groups[4]?.value?.lowercase())
            val end = Clock(endHour, groups[6]?.value?.toIntOrNull() ?: 0, groups[7]?.value?.lowercase())
            val counts = groups[1] != null || start.meridiem != null || end.meridiem != null
            if (!counts || !valid(start) || !valid(end)) return@let
            // "2 to 3pm" is 2 PM; "11 to 1pm" is 11 AM.
            val said = end.meridiem
            if (start.meridiem == null && said != null && start.hour <= 12) {
                start = start.copy(meridiem = if (said == "p" && start.hour != 12 && start.hour > end.hour) "a" else said)
            }
            val at = String.format(Locale.ROOT, " at %d:%02d", start.hour, start.minute) + (start.meridiem?.let { "${it}m " } ?: " ")
            words = words.replaceRange(match.range, at)
            endClock = end
        }

        val phrase = VoicePhrase.parse(words, now)
        val begins = phrase.due
        if (begins == null) {
            val today = now.toLocalDate().atStartOfDay(now.zone)
            return QuickCapture.Draft(QuickCapture.Kind.EVENT, phrase.title, start = today, end = today.plusDays(1), hasTime = false)
        }
        if (!phrase.hasTime) {
            return QuickCapture.Draft(QuickCapture.Kind.EVENT, phrase.title, start = begins, end = begins.plusDays(1), hasTime = false)
        }
        val ends = endClock?.let { end(it, begins) } ?: begins.plusMinutes(minutes)
        return QuickCapture.Draft(QuickCapture.Kind.EVENT, phrase.title, start = begins, end = ends, hasTime = true)
    }

    private fun valid(clock: Clock): Boolean =
        clock.minute <= 59 && (if (clock.meridiem == null) clock.hour <= 23 else clock.hour in 1..12)

    // The first time the clock reads `clock` after the start, on the start's day or the next.
    private fun end(clock: Clock, start: ZonedDateTime): ZonedDateTime {
        val hours = when {
            clock.meridiem != null -> listOf(clock.hour % 12 + if (clock.meridiem == "p") 12 else 0)
            clock.hour < 12 -> listOf(clock.hour, clock.hour + 12)
            else -> listOf(clock.hour)
        }
        val day = start.toLocalDate()
        for (hour in hours) {
            val date = day.atTime(hour, clock.minute).atZone(start.zone)
            if (date.isAfter(start)) return date
        }
        return day.plusDays(1).atTime(hours[0], clock.minute).atZone(start.zone)
    }
}

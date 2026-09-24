package com.sentinelcore.android.organizer

import android.content.Intent
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.time.format.TextStyle
import java.time.temporal.ChronoUnit
import java.util.Locale
import kotlinx.coroutines.flow.MutableStateFlow

// A reminder or note said to the phone. Three ways in, all landing on MainActivity (see
// res/xml/shortcuts.xml): the "Voice reminder" and "Voice note" launcher shortcuts, which open the
// phone's speech recognizer, and Google Assistant's App Action, which arrives with the words
// already in `text`. Google doesn't let an app take over plain "remind me to…", so this is as
// close as Android gets.
object VoiceCapture {
    const val ACTION_REMINDER = "com.sentinelcore.android.VOICE_REMINDER"
    const val ACTION_NOTE = "com.sentinelcore.android.VOICE_NOTE"
    const val EXTRA_TEXT = "text"

    // `text` null means nothing has been said yet: listen first.
    data class Request(val kind: String, val text: String?)

    // Set by MainActivity, taken by the app shell (SentinelApp), which listens and then adds.
    val pending = MutableStateFlow<Request?>(null)

    fun fromIntent(intent: Intent?): Request? {
        val kind = when (intent?.action) {
            ACTION_REMINDER -> OrganizerKinds.REMINDER
            ACTION_NOTE -> OrganizerKinds.NOTE
            else -> return null
        }
        // Reopening the app from Recents replays the intent that first started it.
        if (intent.flags and Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY != 0) return null
        return Request(kind, intent.getStringExtra(EXTRA_TEXT)?.takeIf { it.isNotBlank() })
    }

    // The fields for a reminder said as "call mom tomorrow at 5", and what to tell the person.
    fun reminder(text: String, now: ZonedDateTime = ZonedDateTime.now()): Pair<Map<String, Any?>, String> {
        val phrase = VoicePhrase.parse(text, now)
        val fields = buildMap<String, Any?> {
            put("title", phrase.title)
            put("completed", false)
            phrase.dueField()?.let {
                put("due", it)
                put("allDay", !phrase.hasTime)
            }
        }
        val whenSaid = phrase.due?.let { " for " + spoken(it, phrase.hasTime, now) }.orEmpty()
        return fields to "Added “${phrase.title}”$whenSaid."
    }

    // A short note is its own title. A longer one is titled with its opening words, and the whole
    // of it goes in the body.
    fun note(text: String): Map<String, Any?> {
        // The phone's recognizer hands back lower case; Siri doesn't.
        val spoken = text.trim().replaceFirstChar { it.uppercaseChar() }
        val title = noteTitle(spoken)
        return mapOf("title" to title, "body" to if (title == spoken) "" else spoken)
    }

    fun noteTitle(text: String): String {
        val firstLine = text.lineSequence().firstOrNull { it.isNotBlank() } ?: text
        if (firstLine.length <= 60) return firstLine
        val clipped = firstLine.take(50)
        val cut = clipped.lastIndexOf(' ').takeIf { it > 0 }?.let { clipped.take(it) } ?: clipped
        return cut.trim(' ', ',', '.', ';', ':', '-') + "…"
    }

    // "today at 5:00 PM", "tomorrow", "Friday at 9:00 AM", "October 3".
    fun spoken(date: ZonedDateTime, hasTime: Boolean, now: ZonedDateTime = ZonedDateTime.now()): String {
        val days = ChronoUnit.DAYS.between(now.toLocalDate(), date.toLocalDate())
        val day = when {
            days == 0L -> "today"
            days == 1L -> "tomorrow"
            days in 2..6 -> date.dayOfWeek.getDisplayName(TextStyle.FULL, Locale.getDefault())
            else -> date.month.getDisplayName(TextStyle.FULL, Locale.getDefault()) + " " + date.dayOfMonth
        }
        return if (hasTime) "$day at ${date.format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT))}" else day
    }
}

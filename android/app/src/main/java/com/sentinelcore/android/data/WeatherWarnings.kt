package com.sentinelcore.android.data

import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.TextStyle
import java.util.Locale

// The app-wide weather warning banner's data and rules, kept pure (no Android, no Compose) for the
// unit tests. A port of the wall kiosk's core/SentinelCore.Web/wwwroot/kiosk-weather-alert.js.

// One NWS alert from GET /api/weather/warnings, already filtered by the person's settings and
// sorted worst-first by the server. `level` is "warning", "watch" or "advisory".
data class WeatherWarning(
    val id: String,
    val event: String,
    val level: String,
    val category: String?,
    val severity: String?,
    val headline: String?,
    val instruction: String?,
    val ends: Instant?,
    val references: List<String>,
)

// "Seen" is remembered per device: id -> when it can be forgotten. The NWS reissues a warning every
// so often as it tracks the storm, each reissue a new id that references the old one - a reissue
// of something already seen is seen too, so one storm is one banner. An upgrade (watch to warning)
// is a new alert and flashes again.
class WeatherWarningSeen(initial: Map<String, Instant> = emptyMap()) {
    private val seen = initial.toMutableMap()

    val entries: Map<String, Instant> get() = seen.toMap()

    fun prune(now: Instant) {
        seen.entries.removeAll { it.value.isBefore(now) }
    }

    // True when the alert or anything it references has been seen. A reissue of something seen is
    // remembered in its own right, so the next reissue (which references this one) is seen too
    // after the original is forgotten. Returns whether that changed the set, for saving.
    fun isSeen(alert: WeatherWarning, now: Instant): Boolean {
        if (alert.id in seen) return true
        if (alert.references.any { it in seen }) {
            seen[alert.id] = expiry(alert, now)
            return true
        }
        return false
    }

    fun markSeen(alerts: List<WeatherWarning>, now: Instant) {
        for (alert in alerts) {
            val until = expiry(alert, now)
            seen[alert.id] = until
            for (ref in alert.references) seen[ref] = until
        }
    }

    // One "id<TAB>epochMillis" per line: ids are URNs, never containing a tab or a newline.
    fun encode(): String = seen.entries.joinToString("\n") { "${it.key}\t${it.value.toEpochMilli()}" }

    companion object {
        val FALLBACK: Duration = Duration.ofHours(24)

        fun expiry(alert: WeatherWarning, now: Instant): Instant = alert.ends ?: now.plus(FALLBACK)

        fun decode(text: String?): WeatherWarningSeen {
            val map = mutableMapOf<String, Instant>()
            text.orEmpty().lineSequence().forEach { line ->
                val tab = line.lastIndexOf('\t')
                if (tab <= 0) return@forEach
                val millis = line.substring(tab + 1).toLongOrNull() ?: return@forEach
                map[line.substring(0, tab)] = Instant.ofEpochMilli(millis)
            }
            return WeatherWarningSeen(map)
        }
    }
}

object WeatherWarningLogic {
    // Alerts still in force: one whose `ends` has passed is dropped even if the server still lists it.
    fun active(alerts: List<WeatherWarning>, now: Instant): List<WeatherWarning> =
        alerts.filter { it.ends == null || it.ends.isAfter(now) }

    // "until 5:30 PM" today, "until Tue 5:30 PM" on another day, "" with no end time.
    fun untilText(ends: Instant?, now: Instant, zone: ZoneId, is24Hour: Boolean, locale: Locale = Locale.getDefault()): String {
        ends ?: return ""
        val at = ends.atZone(zone)
        val time = at.format(DateTimeFormatter.ofPattern(if (is24Hour) "HH:mm" else "h:mm a", locale))
        val sameDay = at.toLocalDate() == LocalDate.ofInstant(now, zone)
        return if (sameDay) "until $time" else "until ${at.dayOfWeek.getDisplayName(TextStyle.SHORT, locale)} $time"
    }
}

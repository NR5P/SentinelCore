package com.sentinelcore.android.data

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.util.Locale

// The app-wide weather warning banner's rules - a port of kiosk-weather-alert.js's seen/reference
// handling - and the parser for GET /api/weather/warnings.
class WeatherWarningsTest {

    private val now = Instant.parse("2026-10-12T21:00:00Z")

    private fun alert(id: String, ends: Instant? = now.plusSeconds(3600), refs: List<String> = emptyList(), level: String = "warning") =
        WeatherWarning(id, "Tornado Warning", level, "tornado", "Extreme", "headline", null, ends, refs)

    @Test
    fun parsesTheServerShape() {
        val json = JSONObject(
            """
            { "enabled": true, "wakeMinutes": 10, "openRadar": true,
              "alerts": [
                { "id": "urn:oid:2.49.0.1.840.0.b", "event": "Tornado Warning", "level": "warning", "category": "tornado",
                  "severity": "Extreme", "headline": "Tornado Warning issued October 12", "instruction": "Take cover now.",
                  "ends": "2026-10-12T23:30:00+00:00", "references": ["urn:oid:2.49.0.1.840.0.a"] },
                { "id": "urn:oid:2.49.0.1.840.0.c", "event": "Wind Advisory", "level": "advisory", "category": "wind",
                  "severity": "Minor", "headline": null, "instruction": null, "ends": null, "references": [] },
                { "id": "", "event": "No id", "level": "watch" }
              ] }
            """,
        )
        val alerts = WeatherJson.warnings(json)
        assertEquals(2, alerts.size)
        assertEquals("urn:oid:2.49.0.1.840.0.b", alerts[0].id)
        assertEquals("warning", alerts[0].level)
        assertEquals(Instant.parse("2026-10-12T23:30:00Z"), alerts[0].ends)
        assertEquals(listOf("urn:oid:2.49.0.1.840.0.a"), alerts[0].references)
        assertNull(alerts[1].ends)
        assertNull(alerts[1].headline)
        assertTrue(WeatherJson.warnings(JSONObject("{}")).isEmpty())
    }

    @Test
    fun anUnseenAlertIsUnseenUntilMarked() {
        val seen = WeatherWarningSeen()
        val a = alert("a")
        assertFalse(seen.isSeen(a, now))
        seen.markSeen(listOf(a), now)
        assertTrue(seen.isSeen(a, now))
    }

    @Test
    fun aReissueOfSomethingSeenIsSeenAndRememberedInItsOwnRight() {
        val seen = WeatherWarningSeen()
        seen.markSeen(listOf(alert("a")), now)
        val reissue = alert("b", ends = now.plusSeconds(7200), refs = listOf("a"))
        assertTrue(seen.isSeen(reissue, now))
        assertEquals(now.plusSeconds(7200), seen.entries["b"])

        // Once the original is forgotten, a further reissue (referencing b) is still seen.
        seen.prune(now.plusSeconds(3601))
        assertNull(seen.entries["a"])
        assertTrue(seen.isSeen(alert("c", ends = now.plusSeconds(9000), refs = listOf("b")), now.plusSeconds(3601)))
    }

    @Test
    fun markingSeenAlsoCoversItsReferences() {
        val seen = WeatherWarningSeen()
        seen.markSeen(listOf(alert("b", refs = listOf("a"))), now)
        // Another reissue chain off the same original is seen too.
        assertTrue(seen.isSeen(alert("x", refs = listOf("a")), now))
    }

    @Test
    fun anUpgradeIsANewAlert() {
        val seen = WeatherWarningSeen()
        seen.markSeen(listOf(alert("watch-1", level = "watch")), now)
        assertFalse(seen.isSeen(alert("warning-1"), now))
    }

    @Test
    fun noEndTimeIsRememberedForADay() {
        val seen = WeatherWarningSeen()
        seen.markSeen(listOf(alert("a", ends = null)), now)
        assertEquals(now.plus(Duration.ofHours(24)), seen.entries["a"])
        seen.prune(now.plus(Duration.ofHours(25)))
        assertTrue(seen.entries.isEmpty())
    }

    @Test
    fun encodeDecodeRoundTrips() {
        val seen = WeatherWarningSeen()
        seen.markSeen(listOf(alert("urn:oid:1", refs = listOf("urn:oid:0"))), now)
        val back = WeatherWarningSeen.decode(seen.encode())
        assertEquals(seen.entries, back.entries)
        assertTrue(WeatherWarningSeen.decode(null).entries.isEmpty())
        assertTrue(WeatherWarningSeen.decode("garbage\nno-tab\n\tx").entries.isEmpty())
    }

    @Test
    fun endedAlertsAreDropped() {
        val list = listOf(alert("past", ends = now.minusSeconds(1)), alert("open", ends = null), alert("future"))
        assertEquals(listOf("open", "future"), WeatherWarningLogic.active(list, now).map { it.id })
    }

    @Test
    fun untilShowsTheTimeTodayAndTheWeekdayOtherwise() {
        val chicago = ZoneId.of("America/Chicago")
        // 21:00Z is 4:00 PM in Chicago; 23:30Z is 6:30 PM the same day.
        assertEquals("until 6:30 PM", WeatherWarningLogic.untilText(Instant.parse("2026-10-12T23:30:00Z"), now, chicago, false, Locale.US))
        assertEquals("until 18:30", WeatherWarningLogic.untilText(Instant.parse("2026-10-12T23:30:00Z"), now, chicago, true, Locale.US))
        assertEquals("until Tue 9:00 AM", WeatherWarningLogic.untilText(Instant.parse("2026-10-13T14:00:00Z"), now, chicago, false, Locale.US))
        assertEquals("", WeatherWarningLogic.untilText(null, now, chicago, false, Locale.US))
    }
}

package com.sentinelcore.android.organizer

import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.ZonedDateTime
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

// The building blocks every sync decision rests on - the same cases OrganizerKit pins on Apple,
// so a repeating event lands on the same days on an Android phone as on an iPhone.
class OrganizerFoundationTest {
    private val chicago: ZoneId = ZoneId.of("America/Chicago")

    private fun event(start: String, end: String? = null, rule: String? = null, allDay: Boolean = false, exdates: List<String> = emptyList()) =
        OrganizerItem("e", OrganizerKinds.EVENT, "family", fields = buildMap {
            put("title", "x")
            put("start", start)
            put("allDay", allDay)
            end?.let { put("end", it) }
            rule?.let { put("recurrence", it) }
            if (exdates.isNotEmpty()) put("exdates", exdates)
        })

    private fun day(text: String) = LocalDate.parse(text).atStartOfDay(chicago)
    private fun days(list: List<Occurrence>) = list.map { OrganizerDates.ymd(it.start) }

    @Test
    fun `json round trips every shape and keeps unknown fields`() {
        val text = """{"title":"x","n":2,"f":1.5,"flag":true,"none":null,"list":[1,"a",{"k":false}],"future":{"deep":[null]}}"""
        val map = OrganizerJson.map(JSONObject(text))
        val again = OrganizerJson.map(OrganizerJson.toJson(map) as JSONObject)
        assertEquals(map, again)
        assertEquals(mapOf("deep" to listOf(null)), map["future"])
        // Whole numbers go out without a fraction.
        assertTrue((OrganizerJson.toJson(map) as JSONObject).toString().contains("\"n\":2"))
    }

    @Test
    fun `clocks are fixed width, ordered, and never fall behind one already seen`() {
        val clock = HybridClock("phone")
        val a = clock.next(1_790_000_000_000)
        val b = clock.next(1_790_000_000_000)
        assertEquals("1790000000000-0000-phone", a)
        assertEquals("1790000000000-0001-phone", b)
        clock.observe("1790000060000-0003-laptop")
        assertTrue(clock.next(1_790_000_000_000) > "1790000060000-0003-laptop")
        assertEquals("MorgansPhone2", HybridClock("Morgan’s Phone (2)").deviceId)
    }

    @Test
    fun `field values match the dashboard format`() {
        val date = ZonedDateTime.of(2026, 9, 23, 17, 0, 0, 0, chicago)
        assertEquals("2026-09-23T17:00:00-05:00", OrganizerDates.fieldValue(date, allDay = false))
        assertEquals("2026-09-23", OrganizerDates.fieldValue(date, allDay = true))
        assertEquals(date.toInstant(), OrganizerDates.parse("2026-09-23T17:00:00-05:00", false, chicago)!!.toInstant())
        assertNotNull(OrganizerJson.instant("2026-09-23T19:08:19.8631234+00:00"))
    }

    @Test
    fun `weekly keeps its wall clock time across daylight saving`() {
        val hours = Recurrence.occurrences(event("2026-10-21T09:00:00-05:00", rule = "FREQ=WEEKLY"), day("2026-10-20"), day("2026-11-12"), chicago)
            .map { it.start.hour }
        assertEquals(listOf(9, 9, 9, 9), hours)
    }

    @Test
    fun `byday, until and exdates`() {
        val series = event("2026-09-21T18:00:00-05:00", rule = "FREQ=WEEKLY;BYDAY=MO,WE;UNTIL=20261007", exdates = listOf("2026-09-30T18:00:00-05:00"))
        assertEquals(listOf("2026-09-21", "2026-09-23", "2026-09-28", "2026-10-05", "2026-10-07"),
            days(Recurrence.occurrences(series, day("2026-09-01"), day("2026-12-01"), chicago)))
    }

    @Test
    fun `monthly on the 31st skips short months, and leap day only happens in leap years`() {
        assertEquals(listOf("2026-01-31", "2026-03-31", "2026-05-31"),
            days(Recurrence.occurrences(event("2026-01-31", rule = "FREQ=MONTHLY", allDay = true), day("2026-01-01"), day("2026-06-01"), chicago)))
        assertEquals(listOf("2028-02-29", "2032-02-29"),
            days(Recurrence.occurrences(event("2028-02-29", rule = "FREQ=YEARLY", allDay = true), day("2028-01-01"), day("2033-01-01"), chicago)))
    }

    @Test
    fun `count limits the series and interval skips`() {
        assertEquals(listOf("2026-09-01", "2026-09-03", "2026-09-05"),
            days(Recurrence.occurrences(event("2026-09-01", rule = "FREQ=DAILY;INTERVAL=2;COUNT=3", allDay = true), day("2026-08-01"), day("2026-12-01"), chicago)))
    }

    @Test
    fun `occurrence original start is what exdates use`() {
        val only = Recurrence.occurrences(event("2026-09-23T15:00:00-05:00", rule = "FREQ=WEEKLY"), day("2026-10-13"), day("2026-10-15"), chicago)
        assertEquals(listOf("2026-10-14T15:00:00-05:00"), only.map { it.originalStart })
    }

    @Test
    fun `next due is the occurrence after the one just done`() {
        val reminder = OrganizerItem("r", OrganizerKinds.REMINDER, "family", fields = mapOf("due" to "2026-09-22", "allDay" to true, "recurrence" to "FREQ=WEEKLY"))
        assertEquals("2026-09-29", Recurrence.nextDue(reminder, chicago))
        val timed = reminder.copy(fields = mapOf("due" to "2026-09-22T19:30:00-05:00", "allDay" to false, "recurrence" to "FREQ=WEEKLY"))
        assertEquals("2026-09-29T19:30:00-05:00", Recurrence.nextDue(timed, chicago))
    }

    @Test
    fun `rule round trips its own output`() {
        val rule = RecurrenceRule("WEEKLY", interval = 2, byWeekday = listOf(1, 3))
        assertEquals("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE", rule.rruleValue)
        assertEquals(rule, RecurrenceRule.parse(rule.rruleValue))
    }

    private val now: Instant = Instant.parse("2026-09-23T12:00:00Z")
    private fun item(id: String, kind: String, parent: String? = null, updatedDaysAgo: Long = 0, fields: Map<String, Any?> = emptyMap()) =
        OrganizerItem(id, kind, "family", parent, fields = fields, updatedAt = now.minusSeconds(updatedDaysAgo * 86_400))

    @Test
    fun `a light copy drops old things but never upcoming or open-ended ones`() {
        val items = listOf(
            item("old-note", OrganizerKinds.NOTE, updatedDaysAgo = 200),
            item("old-note-photo", OrganizerKinds.ATTACHMENT, parent = "old-note", fields = mapOf("hash" to "a")),
            item("pinned-note", OrganizerKinds.NOTE, updatedDaysAgo = 400, fields = mapOf("pinned" to true)),
            item("recent-note", OrganizerKinds.NOTE, updatedDaysAgo = 3),
            item("past-event", OrganizerKinds.EVENT, fields = mapOf("start" to "2026-01-10", "allDay" to true)),
            item("weekly-since-2020", OrganizerKinds.EVENT, fields = mapOf("start" to "2020-01-06", "allDay" to true, "recurrence" to "FREQ=WEEKLY")),
            item("next-month", OrganizerKinds.EVENT, fields = mapOf("start" to "2026-10-10T10:00:00Z")),
            item("open-reminder", OrganizerKinds.REMINDER, updatedDaysAgo = 900),
            item("done-long-ago", OrganizerKinds.REMINDER, fields = mapOf("completed" to true, "completedAt" to "2025-01-01T00:00:00Z")),
            item("board", OrganizerKinds.BOARD, updatedDaysAgo = 900),
            item("column", OrganizerKinds.COLUMN, parent = "board", updatedDaysAgo = 900),
            item("stale-open-card", OrganizerKinds.CARD, parent = "column", updatedDaysAgo = 900),
            item("finished-card", OrganizerKinds.CARD, parent = "column", updatedDaysAgo = 300, fields = mapOf("completed" to true)),
        ).associateBy { it.id }
        assertEquals(setOf("old-note", "old-note-photo", "past-event", "done-long-ago", "finished-card"),
            OfflinePolicy.PHONE_DEFAULT.itemsToPrune(items, emptySet(), now, chicago))
    }

    @Test
    fun `pending edits and pins are never dropped and keep their parents`() {
        val items = listOf(
            item("old-note", OrganizerKinds.NOTE, updatedDaysAgo = 500),
            item("photo", OrganizerKinds.ATTACHMENT, parent = "old-note", fields = mapOf("hash" to "a")),
            item("other", OrganizerKinds.NOTE, updatedDaysAgo = 500),
        ).associateBy { it.id }
        assertEquals(setOf("other"), OfflinePolicy.PHONE_DEFAULT.itemsToPrune(items, setOf("photo"), now, chicago))
        assertTrue(OfflinePolicy.FULL_DEFAULT.itemsToPrune(items, emptySet(), now, chicago).isEmpty())
        assertTrue(OfflinePolicy.FULL_DEFAULT.widens(OfflinePolicy.PHONE_DEFAULT))
        assertFalse(OfflinePolicy.PHONE_DEFAULT.widens(OfflinePolicy.FULL_DEFAULT))
        assertEquals(OfflinePolicy.PHONE_DEFAULT, OfflinePolicy.fromJson(OfflinePolicy.PHONE_DEFAULT.toJson()))
    }

    @Test
    fun `plans event alerts, reminders at their due time, and skips finished ones`() {
        val now = OrganizerDates.parse("2026-09-23T08:00:00-05:00", false, chicago)!!.toInstant()
        val items = listOf(
            OrganizerItem("practice", OrganizerKinds.EVENT, "family", fields = mapOf("title" to "Practice", "start" to "2026-09-23T15:00:00-05:00",
                "recurrence" to "FREQ=WEEKLY", "alerts" to listOf(30.0), "location" to "Field 3")),
            OrganizerItem("dentist", OrganizerKinds.REMINDER, "family", fields = mapOf("title" to "Call dentist", "due" to "2026-09-23T17:30:00-05:00")),
            OrganizerItem("trash", OrganizerKinds.REMINDER, "family", fields = mapOf("title" to "Trash", "due" to "2026-09-24", "allDay" to true)),
            OrganizerItem("done", OrganizerKinds.REMINDER, "family", fields = mapOf("title" to "Done", "due" to "2026-09-23T18:00:00-05:00", "completed" to true)),
            OrganizerItem("silent", OrganizerKinds.EVENT, "family", fields = mapOf("title" to "No alert", "start" to "2026-09-23T12:00:00-05:00")),
        )
        val plan = AlertPlanner.plan(items, now, horizonDays = 8, zone = chicago)
        assertEquals(
            listOf("Practice 2026-09-23T14:30:00-05:00", "Call dentist 2026-09-23T17:30:00-05:00", "Trash 2026-09-24T09:00:00-05:00", "Practice 2026-09-30T14:30:00-05:00"),
            plan.map { "${it.title} ${OrganizerDates.isoLocal(ZonedDateTime.ofInstant(Instant.ofEpochMilli(it.fireAtMillis), chicago))}" },
        )
        assertEquals("15:00 · Field 3", plan.first().body)
        assertEquals(plan.size, plan.map { it.id }.toSet().size)
    }
}

package com.sentinelcore.android.widgets

import com.sentinelcore.android.organizer.InMemoryOrganizerStore
import com.sentinelcore.android.organizer.OfflinePolicy
import com.sentinelcore.android.organizer.OrganizerEngine
import com.sentinelcore.android.organizer.OrganizerKinds
import java.io.File
import java.nio.file.Files
import java.time.OffsetDateTime
import java.time.ZoneId
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class OrganizerWidgetSnapshotTest {
    private val zone = ZoneId.of("America/Chicago")
    private val directories = mutableListOf<File>()
    private val scopes = mutableListOf<CoroutineScope>()

    @After
    fun tearDown() {
        scopes.forEach { it.cancel() }
        directories.forEach { it.deleteRecursively() }
    }

    private fun directory() = Files.createTempDirectory("organizer-widgets").toFile().also { directories += it }

    private fun engine(): OrganizerEngine {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO).also { scopes += it }
        return OrganizerEngine(InMemoryOrganizerStore(directory()), "phone", OfflinePolicy.FULL_DEFAULT, scope) { zone }
    }

    private fun at(text: String) = OffsetDateTime.parse(text).atZoneSameInstant(zone)

    @Test
    fun eventsAreExpandedAndWhatHasEndedIsLeftOut() {
        val engine = engine()
        val now = at("2026-09-23T12:00:00-05:00")
        engine.create(OrganizerKinds.EVENT, mapOf("title" to "Standup", "start" to "2026-09-21T09:00:00-05:00", "end" to "2026-09-21T09:15:00-05:00", "recurrence" to "FREQ=DAILY"))
        engine.create(OrganizerKinds.EVENT, mapOf("title" to "Lunch", "start" to "2026-09-23T11:30:00-05:00", "end" to "2026-09-23T12:30:00-05:00"))
        engine.create(OrganizerKinds.EVENT, mapOf("title" to "Trip", "start" to "2026-09-25", "end" to "2026-09-27", "allDay" to true))
        engine.create(OrganizerKinds.EVENT, mapOf("title" to "Next year", "start" to "2027-09-25T10:00:00-05:00"))

        val snapshot = engine.widgetSnapshot(true, now)

        assertEquals("Lunch", snapshot.events.first().title)
        assertEquals(13, snapshot.events.count { it.title == "Standup" })
        assertTrue(snapshot.events.any { it.title == "Trip" && it.allDay })
        assertFalse(snapshot.events.any { it.title == "Next year" })
        assertEquals(snapshot.events.size, snapshot.events.map { it.id }.toSet().size)
        assertEquals(snapshot.events.map { it.start }.sorted(), snapshot.events.map { it.start })
        val later = at("2026-09-23T13:00:00-05:00").toInstant().toEpochMilli()
        assertFalse(snapshot.upcomingEvents(later).any { it.title == "Lunch" })
    }

    @Test
    fun remindersAreOpenTopLevelOnesInDueOrder() {
        val engine = engine()
        val now = at("2026-09-23T12:00:00-05:00")
        val later = engine.create(OrganizerKinds.REMINDER, mapOf("title" to "Later", "due" to "2026-09-30T09:00:00-05:00", "completed" to false))
        engine.create(OrganizerKinds.REMINDER, mapOf("title" to "Overdue", "due" to "2026-09-20", "allDay" to true, "completed" to false))
        engine.create(OrganizerKinds.REMINDER, mapOf("title" to "Someday", "completed" to false))
        engine.create(OrganizerKinds.REMINDER, mapOf("title" to "Flagged someday", "flagged" to true, "completed" to false))
        engine.create(OrganizerKinds.REMINDER, mapOf("title" to "Done", "due" to "2026-09-22", "allDay" to true, "completed" to true))
        engine.create(OrganizerKinds.REMINDER, mapOf("title" to "Step", "completed" to false), parentId = later.id)

        val snapshot = engine.widgetSnapshot(true, now)
        val nowMillis = now.toInstant().toEpochMilli()

        assertEquals(listOf("Overdue", "Later", "Flagged someday", "Someday"), snapshot.reminders.map { it.title })
        assertTrue(snapshot.reminders[0].isOverdue(nowMillis, zone))
        assertFalse(snapshot.reminders[1].isOverdue(nowMillis, zone))
        assertEquals("2026-09-20", snapshot.reminders[0].dueField)
        assertFalse(engine.widgetSnapshot(true, now, skippingReminders = setOf(later.id)).reminders.any { it.id == later.id })
    }

    @Test
    fun notesArePinnedFirstWithAPlainPreview() {
        val engine = engine()
        engine.create(OrganizerKinds.NOTE, mapOf("title" to "Groceries", "body" to "# List\n- [ ] **milk**\n- eggs"))
        engine.create(OrganizerKinds.NOTE, mapOf("title" to "Wifi", "body" to "`hunter2`", "pinned" to true))
        engine.create(OrganizerKinds.NOTE, mapOf("title" to "Old", "archived" to true))

        val snapshot = engine.widgetSnapshot(true, at("2026-09-23T12:00:00-05:00"))

        assertEquals(listOf("Wifi", "Groceries"), snapshot.notes.map { it.title })
        assertEquals("List\nmilk\neggs", snapshot.notes[1].preview)
        assertEquals("hunter2", snapshot.notes[0].preview)
    }

    @Test
    fun theSnapshotSurvivesTheFileAndACheckOffIsQueued() {
        val engine = engine()
        engine.create(OrganizerKinds.EVENT, mapOf("title" to "Dentist", "start" to "2026-09-23T15:00:00-05:00", "location" to "Main St"))
        engine.create(OrganizerKinds.REMINDER, mapOf("title" to "Bins", "due" to "2026-09-23", "allDay" to true, "recurrence" to "FREQ=WEEKLY", "completed" to false))
        engine.create(OrganizerKinds.REMINDER, mapOf("title" to "Call", "completed" to false))
        engine.create(OrganizerKinds.NOTE, mapOf("title" to "Wifi", "body" to "x"))
        val snapshot = engine.widgetSnapshot(true, at("2026-09-23T12:00:00-05:00"))
        val files = OrganizerWidgetFiles(directory())
        assertEquals(OrganizerWidgetSnapshot.EMPTY, files.readSnapshot())

        files.writeSnapshot(snapshot)
        assertEquals(snapshot, files.readSnapshot())

        val bins = snapshot.reminders.first { it.title == "Bins" }
        files.complete(bins.id)
        files.complete(bins.id) // a double tap records it once
        assertEquals(listOf("Call"), files.readSnapshot().reminders.map { it.title })
        assertEquals(listOf(bins.id), files.pendingActions().map { it.reminderId })
        assertEquals("2026-09-23", files.pendingActions().single().dueField)
        assertEquals(listOf(bins.id), files.takeActions().map { it.reminderId })
        assertTrue(files.pendingActions().isEmpty())
    }
}

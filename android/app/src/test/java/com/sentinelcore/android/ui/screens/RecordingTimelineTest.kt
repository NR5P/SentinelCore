package com.sentinelcore.android.ui.screens

import com.sentinelcore.android.data.RecordingTimelineDay
import com.sentinelcore.android.data.TimelineRecording
import com.sentinelcore.android.data.TimelineRecordingSegment
import com.sentinelcore.android.data.TimelineSecurityEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.time.Instant

class RecordingTimelineTest {

    private val dayStart: Instant = Instant.parse("2026-08-10T00:00:00Z")

    private fun at(hour: Long, minute: Long = 0): Instant =
        dayStart.plusSeconds(hour * 3600 + minute * 60)

    private fun event(id: String, occurredAt: Instant) = TimelineSecurityEvent(
        id = id,
        camera = "Front Door",
        occurredAt = occurredAt,
        eventType = "motion",
        category = "person",
        severity = "high",
        summary = "",
        confidence = 0.9,
        notified = false,
        reviewed = false,
        hasImage = true,
        hasClip = true,
    )

    private fun segment(eventId: String, start: Instant, end: Instant) = TimelineRecordingSegment(
        eventId = eventId,
        triggerEventId = "trigger-$eventId",
        start = start,
        end = end,
    )

    private fun day(
        segments: List<TimelineRecordingSegment> = emptyList(),
        events: List<TimelineSecurityEvent> = emptyList(),
        available: Boolean = true,
        error: String? = null,
    ) = RecordingTimelineDay(
        camera = "Front Door",
        date = "2026-08-10",
        start = dayStart,
        end = dayStart.plusSeconds(24 * 3600),
        recording = TimelineRecording(
            configured = available,
            available = available,
            sourceCamera = "Front Door",
            error = error,
            mode = "localTriggerClips",
            preSeconds = 5,
            postSeconds = 10,
            segments = segments,
        ),
        events = events,
    )

    // A day that is merely still loading must not claim the day is empty - the screen used to say
    // "No saved trigger clip is selected" here, which reads as a verdict on the day rather than on
    // the request.
    @Test
    fun `day placeholder reports loading while a camera is chosen and nothing has failed`() {
        assertEquals("Loading day…", dayUnavailableMessage("Front Door", null))
    }

    @Test
    fun `day placeholder surfaces the request failure over the loading wording`() {
        assertEquals(
            "Could not connect to Sentinel Core.",
            dayUnavailableMessage("Front Door", "Could not connect to Sentinel Core."),
        )
    }

    @Test
    fun `day placeholder explains the empty camera list before anything is chosen`() {
        assertEquals(
            "AI events will provide camera choices.",
            dayUnavailableMessage("", null),
        )
    }

    @Test
    fun `clip box yields to the player whenever the cursor sits inside a segment`() {
        assertNull(clipBoxMessage(null, hasSegmentAtCursor = true))
        assertNull(clipBoxMessage("No camera records clips for 'Front Door'.", true))
    }

    @Test
    fun `clip box prefers the server's recording error to the generic wording`() {
        assertEquals(
            "No camera records clips for 'Front Door'.",
            clipBoxMessage("No camera records clips for 'Front Door'.", false),
        )
    }

    @Test
    fun `clip box falls back to the gap wording when clips exist but none is under the cursor`() {
        assertEquals("No saved trigger clip at this time.", clipBoxMessage(null, false))
    }

    @Test
    fun `initial cursor prefers the last clip, then the last event, then midday`() {
        val withClips = day(
            segments = listOf(segment("a", at(8), at(8, 1)), segment("b", at(17), at(17, 1))),
            events = listOf(event("a", at(8)), event("b", at(17))),
        )
        assertEquals(at(17), initialCursor(withClips))

        val eventsOnly = day(events = listOf(event("a", at(8)), event("b", at(19))))
        assertEquals(at(19), initialCursor(eventsOnly))

        assertEquals(at(12), initialCursor(day()))
    }

    // The jump buttons compare against where the cursor will land - the clip start, which is
    // preSeconds before the event - so a clipped event is never skipped over or re-selected.
    @Test
    fun `next event lands on the clip start rather than the event time`() {
        val second = event("b", at(17))
        val today = day(
            segments = listOf(segment("b", at(16, 59), at(17, 1))),
            events = listOf(event("a", at(8)), second),
        )
        assertEquals(at(16, 59), nextEvent(today, today.events, at(12)))
    }

    @Test
    fun `next event does not re-select the event the cursor just landed on`() {
        val today = day(
            segments = listOf(segment("a", at(7, 59), at(8, 1)), segment("b", at(16, 59), at(17, 1))),
            events = listOf(event("a", at(8)), event("b", at(17))),
        )
        assertEquals(at(16, 59), nextEvent(today, today.events, at(7, 59)))
    }

    @Test
    fun `previous event walks backwards and stops at the start of the day`() {
        val today = day(events = listOf(event("a", at(8)), event("b", at(17))))
        assertEquals(at(8), previousEvent(today, today.events, at(12)))
        assertNull(previousEvent(today, today.events, at(8)))
    }

    @Test
    fun `events without a clip still navigate by their own time`() {
        val today = day(events = listOf(event("a", at(8))))
        assertEquals(at(8), nextEvent(today, today.events, at(1)))
    }
}

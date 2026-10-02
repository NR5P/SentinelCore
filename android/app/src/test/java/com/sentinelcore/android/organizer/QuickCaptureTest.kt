package com.sentinelcore.android.organizer

import java.io.File
import java.time.OffsetDateTime
import java.time.ZoneId
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

// A Quick Add line to a reminder, note or event. The examples live in
// core/docs/quick-capture-cases.json, which OrganizerKit's tests read too.
class QuickCaptureTest {
    private fun fixture(): JSONObject {
        // Gradle runs unit tests from the module directory (android/app).
        val file = generateSequence(File("").absoluteFile) { it.parentFile }
            .map { File(it, "core/docs/quick-capture-cases.json") }
            .first { it.exists() }
        return JSONObject(file.readText())
    }

    private fun JSONObject.optional(name: String): String? = if (!has(name) || isNull(name)) null else getString(name)

    @Test
    fun sharedCases() {
        val fixture = fixture()
        val zone = ZoneId.of(fixture.getString("timeZone"))
        val now = OffsetDateTime.parse(fixture.getString("now")).atZoneSameInstant(zone)
        val cases = fixture.getJSONArray("cases")
        assertTrue(cases.length() > 15)
        for (i in 0 until cases.length()) {
            val example = cases.getJSONObject(i)
            val say = example.getString("say")
            val capture = QuickCapture.parse(say)
            assertEquals(say, example.getString("kind"), capture.kind.organizerKind)
            assertEquals(say, example.getBoolean("named"), capture.named)
            val draft = capture.draft(now = now)
            assertEquals(say, example.getString("title"), draft.title)
            val fields = draft.fields()
            when (capture.kind) {
                QuickCapture.Kind.NOTE -> assertEquals(say, example.optional("body") ?: "", fields["body"])
                QuickCapture.Kind.REMINDER -> assertEquals(say, example.optional("due"), fields["due"])
                QuickCapture.Kind.EVENT -> {
                    assertEquals(say, example.optional("start"), fields["start"])
                    assertEquals(say, example.optional("end"), fields["end"])
                    assertEquals(say, fixture.getString("timeZone"), fields["timeZone"])
                }
            }
        }
    }

    // Switching kind in the dialog keeps the words, without the ones that named the old kind.
    @Test
    fun switchingKind() {
        assertEquals("Water the plants", QuickCapture.parse("remind me to water the plants").draft(QuickCapture.Kind.NOTE).title)
        assertEquals("Buy milk", QuickCapture.parse("buy milk").draft(QuickCapture.Kind.NOTE).title)
        assertEquals("The meter reading is 4410", QuickCapture.parse("note the meter reading is 4410").draft(QuickCapture.Kind.REMINDER).title)
    }

    @Test
    fun emptyLines() {
        assertTrue(QuickCapture.parse("").draft().isEmpty)
        assertTrue(QuickCapture.parse("add a note").draft().isEmpty)
    }
}

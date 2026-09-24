package com.sentinelcore.android.organizer

import java.io.File
import java.time.OffsetDateTime
import java.time.ZoneId
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

// Spoken words to a reminder. The examples live in core/docs/voice-phrase-cases.json, which
// OrganizerKit's tests read too - a phrase must mean the same thing on both phones.
class VoicePhraseTest {
    private fun fixture(): JSONObject {
        // Gradle runs unit tests from the module directory (android/app).
        val file = generateSequence(File("").absoluteFile) { it.parentFile }
            .map { File(it, "core/docs/voice-phrase-cases.json") }
            .first { it.exists() }
        return JSONObject(file.readText())
    }

    @Test
    fun sharedCases() {
        val fixture = fixture()
        val zone = ZoneId.of(fixture.getString("timeZone"))
        val now = OffsetDateTime.parse(fixture.getString("now")).atZoneSameInstant(zone)
        val cases = fixture.getJSONArray("cases")
        assertTrue(cases.length() > 20)
        for (i in 0 until cases.length()) {
            val example = cases.getJSONObject(i)
            val say = example.getString("say")
            val phrase = VoicePhrase.parse(say, now)
            assertEquals(say, example.getString("title"), phrase.title)
            assertEquals(say, if (example.isNull("due")) null else example.getString("due"), phrase.dueField())
        }
    }

    // The same cases as OrganizerKit's SharedNotesTests.
    @Test
    fun sharedNotes() {
        val page = VoiceCapture.sharedNote("https://example.com/recipe", "Best chili")
        assertEquals("Best chili", page["title"])
        assertEquals("https://example.com/recipe", page["body"])
        assertEquals(listOf(mapOf("url" to "https://example.com/recipe", "title" to "Best chili")), page["links"])

        val bare = VoiceCapture.sharedNote("https://www.example.com/a", null)
        assertEquals("example.com", bare["title"])

        val text = VoiceCapture.sharedNote("  Garage code changes Friday  ", null)
        assertEquals("Garage code changes Friday", text["title"])
        assertEquals("", text["body"])
        assertEquals(null, text["links"])

        val long = "The plumber said the water heater needs a new anode rod and the pressure valve should be checked next spring"
        val longNote = VoiceCapture.sharedNote(long, null)
        assertEquals("The plumber said the water heater needs a new…", longNote["title"])
        assertEquals(long, longNote["body"])
    }

    @Test
    fun dateOnlyPhrasesAreAllDay() {
        val phrase = VoicePhrase.parse("dentist Friday")
        assertNotNull(phrase.due)
        assertFalse(phrase.hasTime)
    }
}

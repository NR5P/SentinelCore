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

    @Test
    fun dateOnlyPhrasesAreAllDay() {
        val phrase = VoicePhrase.parse("dentist Friday")
        assertNotNull(phrase.due)
        assertFalse(phrase.hasTime)
    }
}

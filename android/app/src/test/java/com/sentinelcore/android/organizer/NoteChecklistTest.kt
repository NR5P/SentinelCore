package com.sentinelcore.android.organizer

import java.io.File
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

// A note's checklist helpers. The examples live in core/docs/note-checklist-cases.json, which the
// dashboard, OrganizerKit and the Windows client read too - a note must tick the same everywhere.
class NoteChecklistTest {
    private fun fixture(): JSONObject {
        // Gradle runs unit tests from the module directory (android/app).
        val file = generateSequence(File("").absoluteFile) { it.parentFile }
            .map { File(it, "core/docs/note-checklist-cases.json") }
            .first { it.exists() }
        return JSONObject(file.readText())
    }

    private fun cases(group: String): List<JSONObject> {
        val array: JSONArray = fixture().getJSONArray(group)
        assertTrue(group, array.length() > 0)
        return (0 until array.length()).map { array.getJSONObject(it) }
    }

    @Test
    fun progress() {
        for (example in cases("progress")) {
            val body = example.getString("body")
            assertEquals(body, NoteChecklist.Progress(example.getInt("done"), example.getInt("total")), NoteChecklist.progress(body))
        }
    }

    @Test
    fun addItem() {
        for (example in cases("addItem")) {
            val body = example.getString("body")
            assertEquals(body, example.getString("expect"), NoteChecklist.addItem(body, example.getString("text")))
        }
    }

    @Test
    fun removeLine() {
        for (example in cases("removeLine")) {
            val body = example.getString("body")
            assertEquals(body, example.getString("expect"), NoteChecklist.removeLine(body, example.getInt("line")))
        }
    }

    @Test
    fun clearChecked() {
        for (example in cases("clearChecked")) {
            val body = example.getString("body")
            assertEquals(body, example.getString("expect"), NoteChecklist.clearChecked(body))
        }
    }

    @Test
    fun makeChecklist() {
        for (example in cases("makeChecklist")) {
            val body = example.getString("body")
            assertEquals(body, example.getString("expect"), NoteChecklist.makeChecklist(body))
        }
    }

    @Test
    fun continueOnEnter() {
        for (example in cases("continueOnEnter")) {
            val line = example.getString("line")
            val expected = example.optJSONObject("expect")?.let {
                if (it.optBoolean("endList")) NoteChecklist.Continuation.EndList else NoteChecklist.Continuation.Insert(it.getString("insert"))
            }
            assertEquals(line, expected, NoteChecklist.continueOnEnter(line))
        }
    }

    // Not in the shared file: ticking only ever touches the one item line, never code.
    @Test
    fun toggleFlipsOnlyItems() {
        assertEquals("- [x] milk\n- [x] eggs", NoteChecklist.toggle("- [ ] milk\n- [x] eggs", 0))
        assertEquals("- [ ] milk\n- [ ] eggs", NoteChecklist.toggle("- [ ] milk\n- [x] eggs", 1))
        assertEquals("  * [ ] bread", NoteChecklist.toggle("  * [X] bread", 0))
        assertEquals("```\n- [ ] code\n```", NoteChecklist.toggle("```\n- [ ] code\n```", 1))
        assertEquals("plain [ ] words", NoteChecklist.toggle("plain [ ] words", 0))
        assertEquals("- [ ] milk", NoteChecklist.toggle("- [ ] milk", 3))
    }
}

package com.sentinelcore.android.organizer

import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

// Card comments: who may edit one, whose name shows, the order they list in, and what an edit sends.
class OrganizerCommentsTest {
    private fun comment(id: String = "c", by: String? = "alice", at: Instant? = Instant.parse("2026-09-23T10:00:00Z"), body: String = "hi") =
        OrganizerItem(id, OrganizerKinds.COMMENT, "family", parentId = "card", fields = mapOf("body" to body), createdAt = at, createdBy = by)

    @Test
    fun `only the author may edit, ignoring case`() {
        assertTrue(OrganizerComments.canEdit(comment(by = "alice"), "alice"))
        assertTrue(OrganizerComments.canEdit(comment(by = "Alice"), "ALICE"))
        assertFalse(OrganizerComments.canEdit(comment(by = "alice"), "bob"))
    }

    @Test
    fun `anyone may edit with login off or an author-less comment`() {
        assertTrue(OrganizerComments.canEdit(comment(by = "alice"), null))
        assertTrue(OrganizerComments.canEdit(comment(by = "alice"), ""))
        assertTrue(OrganizerComments.canEdit(comment(by = null), "bob"))
    }

    @Test
    fun `author shows the display name, then the username, then nothing`() {
        val people = listOf(OrganizerPerson("alice", "Alice Smith"), OrganizerPerson("bob", null))
        assertEquals("Alice Smith", OrganizerComments.authorName("ALICE", people))
        assertEquals("bob", OrganizerComments.authorName("bob", people))
        assertEquals("carol", OrganizerComments.authorName("carol", people))
        assertEquals(null, OrganizerComments.authorName(null, people))
    }

    @Test
    fun `comments list oldest first`() {
        val list = listOf(
            comment("b", at = Instant.parse("2026-09-23T12:00:00Z")),
            comment("undated", at = null),
            comment("a", at = Instant.parse("2026-09-23T09:00:00Z")),
        )
        assertEquals(listOf("a", "b", "undated"), OrganizerComments.sorted(list).map { it.id })
    }

    @Test
    fun `an edit sends the trimmed body and editedAt, or nothing when unchanged or blank`() {
        val now = Instant.parse("2026-09-23T15:30:00Z")
        assertEquals(mapOf("body" to "new", "editedAt" to "2026-09-23T15:30:00Z"), OrganizerComments.editFields(comment(body = "old"), "  new ", now))
        assertNull(OrganizerComments.editFields(comment(body = "same"), "same ", now))
        assertNull(OrganizerComments.editFields(comment(body = "old"), "   ", now))
    }
}

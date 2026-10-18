package com.sentinelcore.android.audio

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

// "Suggested for you" (core/docs/MUSIC_AND_PODCASTS.md "Suggestions"): the contract's sample and
// answers with nulls and missing fields.
class MusicSuggestionsParsingTest {

    private val sample = """
        { "state": "ready", "builtAt": "2026-10-18T22:00:00Z", "message": null,
          "seeds": [ { "artist": "Fleetwood Mac", "reason": "loved" }, { "artist": "Eagles", "reason": "imported" } ],
          "albums": [ { "key": "steely dan|aja", "artist": "Steely Dan", "title": "Aja", "year": 1977,
                        "mbid": "rg1", "artUrl": "https://coverartarchive.org/release-group/rg1/front-250",
                        "because": ["Fleetwood Mac", "Eagles"], "listens": 900000, "queued": false },
                      { "key": "x|y", "artist": "X", "title": "Y", "year": null, "mbid": null, "artUrl": null,
                        "because": null, "listens": null, "queued": true } ] }
    """.trimIndent()

    @Test
    fun `sample parses every field`() {
        val s = parseMusicSuggestions(JSONObject(sample))
        assertEquals("ready", s.state)
        assertFalse(s.working)
        assertEquals("2026-10-18T22:00:00Z", s.builtAt)
        assertNull(s.message)
        assertEquals(listOf("Fleetwood Mac", "Eagles"), s.seeds.map { it.artist })
        assertEquals("loved", s.seeds[0].reason)
        val aja = s.albums[0]
        assertEquals("steely dan|aja", aja.key)
        assertEquals(1977, aja.year)
        assertEquals("rg1", aja.mbid)
        assertEquals(900000L, aja.listens)
        assertFalse(aja.queued)
        assertEquals("Because you like Fleetwood Mac and Eagles", MusicSuggestionsFormat.because(aja))
        val y = s.albums[1]
        assertNull(y.year)
        assertNull(y.artUrl)
        assertTrue(y.because.isEmpty())
        assertEquals(0L, y.listens)
        assertTrue(y.queued)
        assertNull(MusicSuggestionsFormat.because(y))
    }

    @Test
    fun `nulls and missing fields are tolerated`() {
        val s = parseMusicSuggestions(JSONObject("""{ "state": "working", "builtAt": null, "seeds": null, "albums": [ { "artist": "A", "title": "B" }, { "artist": null, "title": "C" }, { "title": "D" } ] }"""))
        assertTrue(s.working)
        assertTrue(s.seeds.isEmpty())
        assertEquals(1, s.albums.size)
        assertEquals("a|b", s.albums[0].key)
        assertNull(MusicSuggestionsFormat.basedOn(s.seeds))
        val empty = parseMusicSuggestions(JSONObject("{}"))
        assertEquals("ready", empty.state)
        assertTrue(empty.albums.isEmpty())
        val failed = parseMusicSuggestions(JSONObject("""{ "state": "error", "message": "Couldn't reach ListenBrainz", "albums": [ { "artist": "A", "title": "B", "year": 0 } ] }"""))
        assertTrue(failed.failed)
        assertEquals("Couldn't reach ListenBrainz", failed.message)
        assertNull(failed.albums[0].year)
    }

    @Test
    fun `wording and request bodies`() {
        val seeds = (1..7).map { MusicSuggestionSeed("A$it") }
        assertEquals("Based on: A1, A2, A3, A4, A5…", MusicSuggestionsFormat.basedOn(seeds))
        assertEquals("Based on: A1, A2", MusicSuggestionsFormat.basedOn(seeds.take(2)))
        val three = MusicSuggestedAlbum("k", "Z", "T", because = listOf("A", "B", "C"))
        assertEquals("Because you like A, B and C", MusicSuggestionsFormat.because(three))
        assertTrue(MusicSuggestionsFormat.isQueued(three, setOf("k")))
        assertFalse(MusicSuggestionsFormat.isQueued(three, emptySet()))
        val items = MusicSuggestionsFormat.importItems(listOf(MusicSuggestedAlbum("k", "Steely Dan", "Aja", 1977)))
        assertEquals("Steely Dan", items[0].artist)
        assertEquals(1977, items[0].year)
        val album = MusicSuggestionsFormat.dismissBody("Steely Dan", "Aja")
        assertEquals("Aja", album.getString("title"))
        assertTrue(album.getBoolean("dismissed"))
        assertFalse(MusicSuggestionsFormat.dismissBody("Steely Dan", null).has("title"))
    }
}

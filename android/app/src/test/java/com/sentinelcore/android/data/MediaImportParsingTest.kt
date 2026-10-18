package com.sentinelcore.android.data

import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

// "Import a list" (core/docs/MOVIE_DOWNLOADS.md): the contract's sample JSON, trimmed answers with
// fields missing, and the wording the web's media-import.js uses.
class MediaImportParsingTest {

    private val musicPreviewJson = """
        { "kind": "music", "sources": ["Exportify"], "tracks": 312, "noAlbum": 2, "shows": 0, "unreadable": 1,
          "problems": ["notes.csv: no artist/album or title column."],
          "albums": [
            { "artist": "Fleetwood Mac", "title": "Rumours", "year": 1977, "trackCount": 4,
              "tracks": ["Dreams", "The Chain"], "single": false, "inLibrary": false },
            { "artist": "Someone", "title": "A Song", "year": null, "trackCount": 1, "tracks": ["A Song"], "single": true, "inLibrary": false },
            { "artist": "Have", "title": "It", "year": 2001, "trackCount": 2, "single": false, "inLibrary": true }
          ],
          "movies": [] }
    """.trimIndent()

    private val moviesPreviewJson = """
        { "kind": "movies", "sources": ["Letterboxd"], "tracks": 0, "noAlbum": 0, "shows": 3, "unreadable": 0, "problems": [],
          "albums": [],
          "movies": [ { "title": "The Matrix", "year": 1999, "imdbId": "tt0133093", "inLibrary": false },
                      { "title": "Alien", "year": 1979, "imdbId": null, "inLibrary": true } ] }
    """.trimIndent()

    private val importJson = """
        { "id": "5f1c", "kind": "music", "name": "Liked Songs", "requestedBy": "pat", "createdAt": "2026-10-17T10:00:00Z",
          "paused": false, "message": "Waiting to try again: VPN down", "finished": false,
          "counts": { "total": 5, "waiting": 1, "downloading": 1, "done": 1, "inLibrary": 1, "notFound": 1, "failed": 0, "canceled": 0 },
          "items": [
            { "id": "i1", "artist": "Fleetwood Mac", "title": "Rumours", "year": 1977, "trackCount": 4,
              "state": "downloading", "message": null, "progress": 0.42, "downloadId": "d1", "releaseTitle": "Fleetwood Mac - Rumours (1977) [FLAC]" },
            { "id": "i2", "artist": "X", "title": "Y", "year": null, "trackCount": 1, "state": "notFound", "message": "Nobody is sharing it.", "progress": null, "downloadId": null, "releaseTitle": null },
            { "id": "i3", "title": "Z", "state": "waiting" }
          ] }
    """.trimIndent()

    @Test
    fun `music preview parses every contract field`() {
        val p = parseMediaImportPreview(JSONObject(musicPreviewJson))
        assertEquals("music", p.kind)
        assertEquals(listOf("Exportify"), p.sources)
        assertEquals(312, p.tracks)
        assertEquals(2, p.noAlbum)
        assertEquals(1, p.unreadable)
        assertEquals(1, p.problems.size)
        assertEquals(3, p.albums.size)
        val rumours = p.albums[0]
        assertEquals("Fleetwood Mac", rumours.artist)
        assertEquals(1977, rumours.year)
        assertEquals(4, rumours.trackCount)
        assertEquals(listOf("Dreams", "The Chain"), rumours.tracks)
        assertNull(p.albums[1].year)
        assertTrue(p.albums[1].single)
        assertTrue(p.albums[2].inLibrary)
    }

    @Test
    fun `movies preview parses and turns into candidates`() {
        val p = parseMediaImportPreview(JSONObject(moviesPreviewJson))
        assertEquals(3, p.shows)
        assertEquals("tt0133093", p.movies[0].imdbId)
        assertNull(p.movies[1].imdbId)
        val items = MediaImportFormat.candidates("movies", p)
        assertEquals(2, items.size)
        assertNull(items[0].artist)
        assertEquals(setOf(items[0].key), MediaImportFormat.initialChecked(items))
    }

    @Test
    fun `an empty or partial preview does not throw`() {
        val p = parseMediaImportPreview(JSONObject("""{ "albums": [ { "artist": "A" }, { "title": "B", "year": "1990" } ] }"""))
        assertEquals("music", p.kind)
        assertEquals(0, p.tracks)
        // A row with no title cannot be searched, so it is dropped.
        assertEquals(1, p.albums.size)
        assertEquals(1990, p.albums[0].year)
        assertTrue(parseMediaImportPreview(JSONObject("{}")).movies.isEmpty())
    }

    @Test
    fun `preview facts and notes match the web wording`() {
        val p = parseMediaImportPreview(JSONObject(musicPreviewJson))
        assertEquals(listOf("From Exportify", "312 songs → 3 albums", "1 already in the library"), MediaImportFormat.facts("music", p))
        val notes = MediaImportFormat.notes("music", p)
        assertEquals("2 songs had no album, so they were left out.", notes[0])
        assertEquals("1 row could not be read (podcast episodes, local files, blank titles).", notes[1])
        assertEquals("notes.csv: no artist/album or title column.", notes[2])
        assertTrue(notes[3].startsWith("1 single: one-song releases"))

        val movies = parseMediaImportPreview(JSONObject(moviesPreviewJson))
        assertEquals(listOf("From Letterboxd", "2 movies", "1 already in the library"), MediaImportFormat.facts("movies", movies))
        assertEquals("3 TV shows left out: add them with + Add to pick seasons.", MediaImportFormat.notes("movies", movies)[0])
    }

    @Test
    fun `checklist helpers`() {
        val items = MediaImportFormat.candidates("music", parseMediaImportPreview(JSONObject(musicPreviewJson)))
        assertEquals(2, MediaImportFormat.initialChecked(items).size)
        assertTrue(MediaImportFormat.matches(items[0], "fleetwood"))
        assertTrue(MediaImportFormat.matches(items[0], "1977"))
        assertFalse(MediaImportFormat.matches(items[0], "beatles"))
        assertEquals("Download 3 albums", MediaImportFormat.startLabel("music", 3, false))
        assertEquals("Download 1 movie", MediaImportFormat.startLabel("movies", 1, false))
        assertEquals("Liked Songs", MediaImportFormat.defaultName("music", listOf("Liked_Songs.csv")))
        assertEquals("Movies import", MediaImportFormat.defaultName("movies", listOf("a.csv", "b.csv")))
    }

    @Test
    fun `start body carries name and items`() {
        val items = MediaImportFormat.candidates("music", parseMediaImportPreview(JSONObject(musicPreviewJson)))
        val body = MediaImportApi.startBody(" Liked Songs ", items.take(2))
        assertEquals("Liked Songs", body.getString("name"))
        val first = body.getJSONArray("items").getJSONObject(0)
        assertEquals("Fleetwood Mac", first.getString("artist"))
        assertEquals("Rumours", first.getString("title"))
        assertEquals(1977, first.getInt("year"))
        assertEquals(4, first.getInt("trackCount"))
        assertTrue(body.getJSONArray("items").getJSONObject(1).isNull("year"))
        val preview = MediaImportApi.previewBody(listOf(MediaImportFile("a.csv", "Artist,Album\n")))
        assertEquals("a.csv", preview.getJSONArray("files").getJSONObject(0).getString("name"))
    }

    @Test
    fun `import parses every contract field`() {
        val job = parseMediaImportJob(JSONObject(importJson))
        assertEquals("5f1c", job.id)
        assertEquals("Liked Songs", job.name)
        assertEquals("pat", job.requestedBy)
        assertFalse(job.paused)
        assertFalse(job.finished)
        assertEquals("Waiting to try again: VPN down", job.message)
        assertEquals(5, job.counts.total)
        assertEquals(3, job.counts.settled)
        assertEquals(3, job.items.size)
        val first = job.items[0]
        assertEquals(0.42, first.progress!!, 1e-9)
        assertEquals("d1", first.downloadId)
        assertEquals("42%", MediaImportFormat.itemStateLabel(first))
        assertEquals("not found", MediaImportFormat.itemStateLabel(job.items[1]))
        assertNull(job.items[2].artist)
        assertEquals(0, job.items[2].trackCount)
        assertTrue(MediaImportFormat.canSkip("notFound"))
        assertFalse(MediaImportFormat.canSkip("downloading"))
        assertEquals(listOf("1 downloaded", "1 downloading", "1 to search", "1 already had", "1 not found"), MediaImportFormat.summaryBits(job.counts))
        assertEquals(0.6f, MediaImportFormat.settledFraction(job.counts), 1e-6f)
        assertTrue(MediaImportFormat.canPause(job))
        assertTrue(MediaImportFormat.canRetry(job))
        assertEquals("working", MediaImportFormat.status(job))
        assertTrue(MediaImportFormat.inFilter("active", "downloading"))
        assertTrue(MediaImportFormat.inFilter("missing", "failed"))
        assertFalse(MediaImportFormat.inFilter("done", "waiting"))
    }

    @Test
    fun `import list parses without items and tolerates missing counts`() {
        val jobs = parseMediaImportJobs("""{ "imports": [ { "id": "a", "name": "W", "finished": true, "paused": false, "counts": null, "items": null }, { "name": "no id" } ] }""")
        assertEquals(1, jobs.size)
        assertEquals(0, jobs[0].counts.total)
        assertTrue(jobs[0].items.isEmpty())
        assertEquals("finished", MediaImportFormat.status(jobs[0]))
        assertFalse(MediaImportFormat.canPause(jobs[0]))
        assertEquals(1f, MediaImportFormat.settledFraction(jobs[0].counts))
        assertTrue(parseMediaImportJobs("").isEmpty())
        assertEquals(1, parseMediaImportJobs("""[ { "id": "b" } ]""").size)
    }

    @Test
    fun `api uses each tab's base path and the server's error`() = runBlocking {
        val calls = mutableListOf<String>()
        val api = MediaImportApi("movies") { method, path, _, _, _, _ ->
            calls += "$method $path"
            when {
                path.endsWith("/imports") && method == "GET" -> 404 to ByteArray(0)
                method == "DELETE" -> 400 to """{ "error": "That list is gone." }""".toByteArray()
                else -> 200 to importJson.toByteArray()
            }
        }
        assertNull(api.list())
        assertEquals("i1", api.pause("5f1c", true).items[0].id)
        val failure = runCatching { api.remove("5f1c") }.exceptionOrNull()
        assertEquals("That list is gone.", failure?.message)
        assertEquals(
            listOf("GET /api/media/downloads/imports", "POST /api/media/downloads/imports/5f1c/pause", "DELETE /api/media/downloads/imports/5f1c"),
            calls,
        )
        assertEquals("/api/music/downloads/imports/a%20b", MediaImportApi("music") { _, _, _, _, _, _ -> 200 to ByteArray(0) }.jobPath("a b"))
    }
}

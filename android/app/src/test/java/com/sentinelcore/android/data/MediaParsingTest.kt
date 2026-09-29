package com.sentinelcore.android.data

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

// Fed the sample JSON from core/docs/MOVIES.md, so a drift between the contract and these parsers
// shows up here rather than as an empty screen.
class MediaParsingTest {

    private val movieSummary = """
        {
          "id": "m1", "kind": "movie", "libraryId": "lib1",
          "title": "17 Again", "sortTitle": "17 again", "year": 2009,
          "genres": ["Comedy", "Fantasy"], "rating": 6.2, "certification": "PG-13",
          "runtimeMinutes": 102, "durationSeconds": 6120.4,
          "posterUrl": "/api/media/items/m1/poster", "backdropUrl": "/api/media/items/m1/backdrop",
          "thumbUrl": null,
          "addedAt": "2026-09-25T10:00:00Z",
          "progress": { "positionSeconds": 1200, "durationSeconds": 6120, "percent": 19.6, "watched": false, "updatedAt": "2026-09-25T11:00:00Z" },
          "showId": null, "showTitle": null, "seasonNumber": null, "episodeNumber": null, "airDate": null,
          "seasonCount": null, "episodeCount": null, "watchedEpisodeCount": null, "nextEpisodeId": null
        }
    """.trimIndent()

    @Test
    fun `a movie summary parses every contract field`() {
        val item = parseMediaItem(JSONObject(movieSummary))

        assertEquals("m1", item.id)
        assertTrue(item.isMovie)
        assertEquals("lib1", item.libraryId)
        assertEquals("17 Again", item.title)
        assertEquals(2009, item.year)
        assertEquals(listOf("Comedy", "Fantasy"), item.genres)
        assertEquals(6.2, item.rating!!, 0.0001)
        assertEquals("PG-13", item.certification)
        assertEquals(102, item.runtimeMinutes)
        assertEquals(6120.4, item.durationSeconds!!, 0.0001)
        assertEquals("/api/media/items/m1/poster", item.posterUrl)
        assertNull(item.thumbUrl)
        assertEquals(1200.0, item.progress!!.positionSeconds, 0.0001)
        assertEquals(19.6, item.progress!!.percent, 0.0001)
        assertFalse(item.progress!!.watched)
        assertNull(item.showId)
        assertNull(item.seasonCount)
        assertNull(item.episodeCode)
        assertFalse(item.isWatched)
        assertEquals(0.196f, item.progressFraction!!, 0.0001f)
    }

    @Test
    fun `a summary with no progress and no artwork parses to nulls`() {
        val item = parseMediaItem(
            JSONObject("""{ "id": "m2", "kind": "movie", "title": "Plain", "posterUrl": null, "progress": null, "genres": [] }"""),
        )

        assertNull(item.progress)
        assertNull(item.posterUrl)
        assertNull(item.year)
        assertNull(item.progressFraction)
        assertTrue(item.genres.isEmpty())
    }

    @Test
    fun `a show detail carries its seasons and episodes`() {
        val json = JSONObject(
            """
            {
              "id": "s1", "kind": "show", "libraryId": "lib2", "title": "Black Mirror", "year": 2011,
              "genres": ["Drama"], "posterUrl": "/api/media/items/s1/poster", "backdropUrl": null, "thumbUrl": null,
              "progress": null,
              "seasonCount": 1, "episodeCount": 3, "watchedEpisodeCount": 1, "nextEpisodeId": "e2",
              "overview": "Anthology.", "tagline": null, "releaseDate": "2011-12-04",
              "cast": [ { "name": "Daniel Lapaine", "character": "Jonathan" } ],
              "tmdbId": 42009, "tmdbUrl": "https://www.themoviedb.org/tv/42009",
              "file": null, "audioTracks": null, "subtitles": null,
              "seasons": [ { "number": 1, "name": "Season 1", "episodeCount": 3, "watchedCount": 1, "posterUrl": null,
                             "episodes": [
                               { "id": "e1", "kind": "episode", "title": "The National Anthem", "showId": "s1", "showTitle": "Black Mirror",
                                 "seasonNumber": 1, "episodeNumber": 1, "airDate": "2011-12-04", "thumbUrl": "/api/media/items/e1/thumb",
                                 "posterUrl": "/api/media/items/s1/poster",
                                 "progress": { "positionSeconds": 0, "durationSeconds": 2646, "percent": 100, "watched": true } },
                               { "id": "e2", "kind": "episode", "title": "Fifteen Million Merits", "showId": "s1", "showTitle": "Black Mirror",
                                 "seasonNumber": 1, "episodeNumber": 2, "progress": null }
                             ] } ]
            }
            """.trimIndent(),
        )

        val detail = parseMediaItemDetail(json)

        assertTrue(detail.summary.isShow)
        assertEquals(2, detail.summary.unwatchedEpisodeCount)
        assertFalse(detail.summary.isWatched)
        assertEquals("e2", detail.summary.nextEpisodeId)
        assertEquals("Anthology.", detail.overview)
        assertNull(detail.tagline)
        assertEquals(42009L, detail.tmdbId)
        assertEquals("Jonathan", detail.cast.single().character)
        assertNull(detail.file)
        assertTrue(detail.audioTracks.isEmpty())
        assertTrue(detail.subtitles.isEmpty())

        val season = detail.seasons!!.single()
        assertEquals(1, season.number)
        assertEquals("Season 1", season.name)
        assertEquals(3, season.episodeCount)
        assertEquals(1, season.watchedCount)
        assertEquals(2, season.episodes.size)
        val first = season.episodes.first()
        assertTrue(first.isEpisode)
        assertEquals("S1 · E1", first.episodeCode)
        assertEquals("Black Mirror", first.showTitle)
        assertTrue(first.isWatched)
        assertNull(first.progressFraction)
        assertEquals("/api/media/items/e1/thumb", first.thumbUrl)
    }

    @Test
    fun `a movie detail carries file info, audio tracks and subtitles but no seasons`() {
        val json = JSONObject(movieSummary)
            .put("overview", "A man becomes 17 again.")
            .put("tagline", "Who says you're only young once?")
            .put(
                "file",
                JSONObject("""{ "container": "mp4", "videoCodec": "h264", "videoProfile": "High", "width": 1920, "height": 800,
                               "audioCodec": "aac", "audioChannels": 2, "sizeBytes": 1734000000, "bitrate": 2267000, "path": null }"""),
            )
            .put("audioTracks", org.json.JSONArray("""[ { "index": 0, "codec": "eac3", "channels": 6, "language": "eng", "label": "English 5.1", "isDefault": true } ]"""))
            .put("subtitles", org.json.JSONArray("""[ { "index": 0, "language": "eng", "label": "English", "url": "/api/media/items/m1/subtitles/0.vtt", "forced": false, "external": false } ]"""))
            .put("seasons", JSONObject.NULL)

        val detail = parseMediaItemDetail(json)

        assertNull(detail.seasons)
        val file = detail.file!!
        assertEquals("mp4", file.container)
        assertEquals(1920, file.width)
        assertEquals(1734000000L, file.sizeBytes)
        assertNull(file.path)
        val track = detail.audioTracks.single()
        assertEquals("eac3", track.codec)
        assertEquals(6, track.channels)
        assertTrue(track.isDefault)
        val subtitle = detail.subtitles.single()
        assertEquals("/api/media/items/m1/subtitles/0.vtt", subtitle.url)
        assertFalse(subtitle.forced)
    }

    @Test
    fun `an hls playback parses its start and next episode`() {
        val playback = parseMediaPlayback(
            JSONObject(
                """
                {
                  "itemId": "e1", "title": "Black Mirror", "subtitle": "S1 · E1 · The National Anthem",
                  "durationSeconds": 2646, "startSeconds": 1200, "resumeSeconds": 1200,
                  "mode": "hls",
                  "url": "/api/media/hls/abc/master.m3u8",
                  "mimeType": "application/vnd.apple.mpegurl",
                  "video": "copy", "audio": "aac",
                  "subtitles": [ { "index": 0, "language": "eng", "label": "English", "url": "/api/media/items/e1/subtitles/0.vtt", "forced": false } ],
                  "nextEpisodeId": "e2", "nextEpisodeTitle": "S1 · E2 · Fifteen Million Merits",
                  "backdropUrl": null, "posterUrl": "/api/media/items/s1/poster"
                }
                """.trimIndent(),
            ),
        )

        assertTrue(playback.isHls)
        assertEquals(1200.0, playback.startSeconds, 0.0)
        assertEquals(2646.0, playback.durationSeconds!!, 0.0)
        assertEquals("/api/media/hls/abc/master.m3u8", playback.url)
        assertEquals("e2", playback.nextEpisodeId)
        assertEquals("S1 · E2 · Fifteen Million Merits", playback.nextEpisodeTitle)
        assertEquals(1, playback.subtitles.size)
        assertNull(playback.backdropUrl)
    }

    @Test
    fun `a direct playback with nothing next`() {
        val playback = parseMediaPlayback(
            JSONObject(
                """{ "itemId": "m1", "title": "17 Again", "mode": "direct", "url": "/api/media/stream/m1/video.mp4",
                     "mimeType": "video/mp4", "startSeconds": 0, "durationSeconds": 6120.4, "subtitles": [], "nextEpisodeId": null }""",
            ),
        )

        assertFalse(playback.isHls)
        assertEquals(0.0, playback.startSeconds, 0.0)
        assertNull(playback.nextEpisodeId)
        assertEquals("video/mp4", playback.mimeType)
    }

    @Test
    fun `home, items page and status parse`() {
        val library = """{ "id": "lib1", "name": "Movies", "kind": "movies", "path": "/mnt/movies/Media/Movies",
                           "itemCount": 396, "lastScanFinishedAt": "2026-09-25T10:00:00Z", "lastScanError": null }"""
        val home = parseMediaHome(
            JSONObject(
                """{ "continueWatching": [ $movieSummary ], "recentMovies": [ $movieSummary ], "recentShows": [],
                     "recentEpisodes": [], "featured": $movieSummary, "movieCount": 396, "showCount": 45, "libraries": [ $library ] }""",
            ),
        )
        assertEquals(1, home.continueWatching.size)
        assertEquals("m1", home.featured!!.id)
        assertEquals(396, home.movieCount)
        assertEquals("movies", home.libraries.single().kind)
        assertNull(home.libraries.single().lastScanError)

        val page = parseMediaItemsPage(JSONObject("""{ "items": [ $movieSummary ], "total": 396, "genres": ["Action", "Comedy"] }"""))
        assertEquals(396, page.total)
        assertEquals(listOf("Action", "Comedy"), page.genres)

        val status = parseMediaStatus(
            JSONObject(
                """{ "enabled": true, "ffmpegAvailable": true, "tmdbConfigured": false, "scanning": true,
                     "scan": { "phase": "metadata", "libraryName": "Movies", "processed": 12, "total": 400, "startedAt": "2026-09-25T10:00:00Z" },
                     "libraries": [ $library ], "movieCount": 396, "showCount": 45, "episodeCount": 1900, "lastScanFinishedAt": null }""",
            ),
        )
        assertTrue(status.enabled)
        assertFalse(status.tmdbConfigured)
        assertEquals("metadata", status.scan!!.phase)
        assertEquals(400, status.scan!!.total)
        assertEquals(1900, status.episodeCount)

        val idle = parseMediaStatus(JSONObject("""{ "enabled": false, "scan": null }"""))
        assertNull(idle.scan)
        assertFalse(idle.enabled)
    }

    @Test
    fun `play count and my list parse, and default when an older server leaves them out`() {
        val item = parseMediaItem(JSONObject(movieSummary).put("playCount", 3).put("inList", true))
        assertEquals(3, item.playCount)
        assertTrue(item.inList)
        assertEquals("Played 3 times", item.playCountLabel)

        val old = parseMediaItem(JSONObject(movieSummary))
        assertEquals(0, old.playCount)
        assertFalse(old.inList)
        assertNull(old.playCountLabel)

        assertEquals("Played once", parseMediaItem(JSONObject(movieSummary).put("playCount", 1)).playCountLabel)
        assertNull(parseMediaItem(JSONObject(movieSummary).put("playCount", JSONObject.NULL)).playCountLabel)
    }

    @Test
    fun `home discovery rows parse`() {
        val home = parseMediaHome(
            JSONObject(
                """{ "continueWatching": [], "recentMovies": [], "recentShows": [], "recentEpisodes": [], "featured": null,
                     "myList": [ $movieSummary ], "mostPlayed": [ $movieSummary, $movieSummary ], "recentlyWatched": [ $movieSummary ],
                     "topRated": [ $movieSummary ],
                     "genreShelves": [ { "genre": "Comedy", "items": [ $movieSummary ] },
                                       { "genre": "Drama", "items": [] },
                                       { "genre": null, "items": [ $movieSummary ] },
                                       { "genre": "Horror", "items": [ $movieSummary, $movieSummary ] } ],
                     "movieCount": 396, "showCount": 45, "libraries": [] }""",
            ),
        )
        assertEquals("m1", home.myList.single().id)
        assertEquals(2, home.mostPlayed.size)
        assertEquals(1, home.recentlyWatched.size)
        assertEquals(1, home.topRated.size)
        // Shelves with no name or no titles are dropped; the rest keep the server's order.
        assertEquals(listOf("Comedy", "Horror"), home.genreShelves.map { it.genre })
        assertEquals(2, home.genreShelves.last().items.size)
    }

    @Test
    fun `home from an older server has empty discovery rows`() {
        val home = parseMediaHome(
            JSONObject("""{ "continueWatching": [ $movieSummary ], "recentMovies": [], "movieCount": 1, "showCount": 0, "libraries": [] }"""),
        )
        assertEquals(1, home.continueWatching.size)
        assertTrue(home.myList.isEmpty())
        assertTrue(home.mostPlayed.isEmpty())
        assertTrue(home.recentlyWatched.isEmpty())
        assertTrue(home.topRated.isEmpty())
        assertTrue(home.genreShelves.isEmpty())

        val nulls = parseMediaHome(JSONObject("""{ "myList": null, "genreShelves": null, "topRated": null }"""))
        assertTrue(nulls.myList.isEmpty())
        assertTrue(nulls.genreShelves.isEmpty())
        assertTrue(nulls.topRated.isEmpty())
    }

    @Test
    fun `my list result names the item actually saved`() {
        assertEquals("s1" to true, parseMediaListResult(JSONObject("""{ "itemId": "s1", "inList": true }"""), "e1"))
        assertEquals("m1" to false, parseMediaListResult(JSONObject("""{ "inList": false }"""), "m1"))
    }
}

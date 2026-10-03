package com.sentinelcore.android.audio

import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.random.Random

class AudioLogicTest {
    // ---------------- LRC ----------------

    @Test
    fun `lrc lines are timed, sorted, and repeated stamps repeat the line`() {
        val lines = TimedText.parseLrc(
            """
            [ar:Someone]
            [ti:Song]
            [00:12.30]Now here you go again
            [00:05.00][00:20.5]Chorus
            [01:02.123]Late line
            """.trimIndent(),
        )
        assertEquals(listOf(5.0, 12.3, 20.5, 62.123), lines.map { it.time })
        assertEquals(listOf("Chorus", "Now here you go again", "Chorus", "Late line"), lines.map { it.text })
    }

    @Test
    fun `lrc offset shifts every line and untimed text becomes plain lines`() {
        val shifted = TimedText.parseLrc("[offset:+500]\n[00:01.00]a\n[00:02.00]b")
        assertEquals(listOf(1.5, 2.5), shifted.map { it.time })
        val plain = TimedText.parseLrc("just words\nmore words")
        assertEquals(listOf(null, null), plain.map { it.time })
        assertEquals("more words", plain[1].text)
    }

    @Test
    fun `the active lyric is the last one whose time has come`() {
        val lines = listOf(LyricLine(5.0, "a"), LyricLine(10.0, "b"), LyricLine(15.0, "c"))
        assertEquals(-1, TimedText.activeLyric(lines, 1.0))
        assertEquals(0, TimedText.activeLyric(lines, 5.0))
        assertEquals(1, TimedText.activeLyric(lines, 14.9))
        assertEquals(2, TimedText.activeLyric(lines, 400.0))
    }

    @Test
    fun `transcript segment lookup and search`() {
        val segments = (0 until 10).map { TranscriptSegment(it * 5.0, it * 5.0 + 5, null, if (it == 7) "The Soldering iron gets hot" else "words $it") }
        assertEquals(0, TimedText.activeSegment(segments, 0.0))
        assertEquals(3, TimedText.activeSegment(segments, 17.0))
        assertEquals(-1, TimedText.activeSegment(segments, -1.0))
        assertEquals(listOf(7), TimedText.search(segments, "soldering HOT"))
        assertTrue(TimedText.search(segments, "  ").isEmpty())
    }

    @Test
    fun `chapters lookup`() {
        val chapters = listOf(Chapter(0.0, 60.0, "Intro", null, null), Chapter(60.0, null, "Main", null, null))
        assertEquals(0, TimedText.activeChapter(chapters, 30.0))
        assertEquals(1, TimedText.activeChapter(chapters, 59.9))
    }

    // ---------------- queue ----------------

    @Test
    fun `moving keeps the playing index on the same entry`() {
        val list = listOf("a", "b", "c", "d")
        assertEquals(listOf("b", "c", "a", "d") to 2, QueueOps.move(list, 0, 2, 0))
        assertEquals(listOf("b", "a", "c", "d") to 0, QueueOps.move(list, 1, 0, 1))
        // A row moved from after the playing one to before it pushes the index along.
        assertEquals(listOf("a", "d", "b", "c") to 2, QueueOps.move(list, 3, 1, 1))
    }

    @Test
    fun `removing before, at and after the playing entry`() {
        val list = listOf("a", "b", "c")
        assertEquals(listOf("b", "c") to 0, QueueOps.remove(list, 0, 1))
        assertEquals(listOf("a", "b") to 1, QueueOps.remove(list, 2, 1))
        assertEquals(listOf("a", "b") to 1, QueueOps.remove(list, 2, 2))
        assertEquals(emptyList<String>() to 0, QueueOps.remove(listOf("x"), 0, 0))
    }

    @Test
    fun `play next goes right after the playing entry`() {
        assertEquals(listOf("a", "x", "y", "b"), QueueOps.insertNext(listOf("a", "b"), listOf("x", "y"), 0))
        assertEquals(listOf("x"), QueueOps.insertNext(emptyList(), listOf("x"), -1))
    }

    @Test
    fun `shuffle keeps the playing entry first and unshuffle restores the order`() {
        val list = (1..20).toList()
        val shuffled = QueueOps.shuffled(list, 4, Random(7))
        assertEquals(5, shuffled.first())
        assertEquals(list.toSet(), shuffled.toSet())
        val (restored, index) = QueueOps.unshuffled(shuffled + 99, list, 5)
        assertEquals(list + 99, restored)
        assertEquals(4, index)
    }

    @Test
    fun `now playing window keeps 500 around the index`() {
        val list = (0 until 1200).toList()
        val (window, index) = QueueOps.window(list, 1000)
        assertEquals(500, window.size)
        assertEquals(1000, window[index])
        val (start, startIndex) = QueueOps.window(list, 3)
        assertEquals(0, start.first())
        assertEquals(3, startIndex)
        assertEquals(list.take(10) to 2, QueueOps.window(list.take(10), 2))
    }

    // ---------------- Sound Check, reporting, formatting ----------------

    @Test
    fun `sound check uses track gain, album gain in album order, and clamps`() {
        val gain = ReplayGainInfo(trackGain = -6.0, trackPeak = 0.9, albumGain = -3.0, albumPeak = 0.95)
        assertEquals(0.501f, SoundCheck.volume(gain, preferAlbum = false), 0.002f)
        assertEquals(0.708f, SoundCheck.volume(gain, preferAlbum = true), 0.002f)
        assertEquals(1f, SoundCheck.volume(ReplayGainInfo(trackGain = 4.0), false), 0.0001f)
        assertEquals(1f, SoundCheck.volume(null, false), 0.0001f)
        assertEquals(0.501f, SoundCheck.volume(ReplayGainInfo(albumGain = -6.0), false), 0.002f)
    }

    @Test
    fun `skips and plays follow the contract thresholds`() {
        assertTrue(PlayReporting.isSkip(12.0, completed = false, userSkipped = true))
        assertFalse(PlayReporting.isSkip(45.0, completed = false, userSkipped = true))
        assertFalse(PlayReporting.isSkip(12.0, completed = true, userSkipped = false))
        assertTrue(PlayReporting.countsAsPlay(130.0, 257.0))
        assertFalse(PlayReporting.countsAsPlay(100.0, 600.0))
        assertTrue(PlayReporting.countsAsPlay(240.0, 900.0))
        assertFalse(PlayReporting.worthSavingProgress(9.0))
        assertTrue(PlayReporting.episodeFinished(1760.0, 1780.0))
        assertFalse(PlayReporting.episodeFinished(600.0, 1780.0))
    }

    @Test
    fun `formatting`() {
        assertEquals("4:17", AudioFormat.clock(257.8))
        assertEquals("1:02:03", AudioFormat.clock(3723.0))
        assertEquals("1 hr 12 min", AudioFormat.duration(4320.0))
        assertEquals("43 min", AudioFormat.duration(2580.0))
        assertEquals("1.5×", AudioFormat.speed(1.5))
        assertEquals("2×", AudioFormat.speed(2.0))
        assertEquals("1.8×", AudioFormat.speed(1.8f.toDouble()))
        assertEquals("1 album", AudioFormat.count(1, "album"))
        assertEquals("3 songs", AudioFormat.count(3, "song"))
        assertEquals("1.2 GB", AudioFormat.bytes(1_288_490_189))
        assertEquals("FLAC 16/44.1", AudioFormat.trackQuality(Track("1", "t", codec = "flac", bitDepth = 16, sampleRate = 44100)))
        assertEquals("MP3 320 kbps", AudioFormat.trackQuality(Track("1", "t", codec = "mp3", bitrate = 320000)))
    }

    // ---------------- smart rules ----------------

    @Test
    fun `smart rules round trip with typed values on the wire`() {
        val rules = SmartRules(
            match = "any",
            rules = listOf(SmartRule("genre", "is", "Rock"), SmartRule("rating", "gte", "4"), SmartRule("loved", "is", "true"), SmartRule("lastPlayed", "notInLast", "90")),
            sort = "plays",
            order = "desc",
            limit = 9000,
        )
        val json = rules.toJson()
        val array = json.getJSONArray("rules")
        assertEquals("Rock", array.getJSONObject(0).get("value"))
        assertEquals(4L, (array.getJSONObject(1).get("value") as Number).toLong())
        assertEquals(true, array.getJSONObject(2).get("value"))
        assertEquals(5000, json.getInt("limit"))
        val back = SmartRules.fromJson(JSONObject(json.toString()))
        assertEquals(rules.copy(limit = 5000), back)
    }

    @Test
    fun `smart field tables and blanks`() {
        assertEquals(listOf("inLast", "notInLast"), SmartFields.ops("added"))
        assertEquals(listOf("is", "gte", "lte"), SmartFields.ops("bpm"))
        assertEquals(SmartRule("loved", "is", "true"), SmartFields.defaultRule("loved"))
        val cleaned = SmartRules(rules = listOf(SmartRule("title", "contains", ""), SmartRule("year", "gte", "1990"))).withoutBlankRules()
        assertEquals(1, cleaned.rules.size)
        assertEquals("Year at least 1990", cleaned.summary())
    }

    // ---------------- parsing ----------------

    @Test
    fun `track parsing keeps nulls as nulls`() {
        val track = parseTrack(
            JSONObject(
                """{"id":"t1","title":"Dreams","artist":"Fleetwood Mac","albumId":null,"year":1977,"durationSeconds":257.8,
                "replayGain":{"trackGain":-6.2,"trackPeak":null},"hasLyrics":true,"loved":true,"rating":4,"path":null,"composer":null}""",
            ),
        )
        assertNull(track.albumId)
        assertNull(track.path)
        assertNull(track.composer)
        assertEquals(-6.2, track.replayGain?.trackGain)
        assertNull(track.replayGain?.trackPeak)
        assertEquals(4, track.rating)
        assertTrue(track.loved)
    }

    @Test
    fun `home drops empty recent items and empty on-this-day`() {
        val home = parseMusicHome(
            JSONObject(
                """{"recentlyPlayed":[{"kind":"album","album":{"id":"a","title":"Rumours"}},{"kind":"mix"}],
                "onThisDay":{"label":"x","tracks":[]},"decades":[{"decade":1990,"label":"90s","trackCount":3}],"trackCount":5}""",
            ),
        )
        assertEquals(1, home.recentlyPlayed.size)
        assertNull(home.onThisDay)
        assertEquals(1990, home.decades.single().decade)
    }

    @Test
    fun `lyrics are synced only with timed lines`() {
        val unsynced = parseLyrics(JSONObject("""{"synced":true,"lines":[{"time":null,"text":"a"}],"plain":"a"}"""))
        assertFalse(unsynced.synced)
        val synced = parseLyrics(JSONObject("""{"synced":true,"lines":[{"time":12.3,"text":"a"}],"source":"lrclib"}"""))
        assertTrue(synced.synced)
        assertEquals("lrclib", synced.source)
    }

    @Test
    fun `episode remaining and resume`() {
        val episode = parseEpisode(
            JSONObject("""{"id":"e","podcastId":"p","title":"t","durationSeconds":1780,"progress":{"positionSeconds":300,"durationSeconds":1780,"percent":16.9,"played":false}}"""),
        )
        assertEquals(1480.0, episode.remainingSeconds, 0.01)
        assertEquals(300.0, episode.resumeSeconds, 0.01)
        assertTrue(episode.inProgress)
        val played = episode.copy(progress = episode.progress?.copy(played = true))
        assertEquals(0.0, played.resumeSeconds, 0.01)
    }

    @Test
    fun `speaker commands carry only what their action needs`() {
        val play = SpeakerCommand(action = "play", queue = listOf(QueueRef("track", "1")), index = 0, positionSeconds = 12.0, context = PlayContext("album", "a", "Rumours")).toJson()
        assertEquals(12.0, play.getDouble("positionSeconds"), 0.0)
        assertEquals("track", play.getJSONArray("queue").getJSONObject(0).getString("type"))
        val pause = SpeakerCommand(action = "pause").toJson()
        assertFalse(pause.has("queue"))
        assertFalse(pause.has("positionSeconds"))
        val batch = parseCommandBatch(JSONObject("""{"commands":[{"seq":42,"action":"announce","text":"Hi"}],"latestSeq":42}"""))
        assertEquals(42L, batch.latestSeq)
        assertEquals("Hi", batch.commands.single().text)
    }

    @Test
    fun `stats parse with hour and weekday arrays`() {
        val stats = parseStats(
            JSONObject(
                """{"period":"year","music":{"minutes":12034,"plays":3400,"topArtists":[{"artist":{"id":"a","name":"X"},"plays":300,"minutes":1100}],
                "byHour":[${(0 until 24).joinToString(",")}],"byWeekday":[1,2,3,4,5,6,7],"firstPlay":null},
                "podcasts":{"minutes":4000,"topShows":[{"podcast":{"id":"p","title":"Show"},"minutes":900,"episodes":30}]}}""",
            ),
        )
        assertEquals(24, stats.music?.byHour?.size)
        assertEquals("X", stats.music?.topArtists?.single()?.artist?.name)
        assertNull(stats.music?.firstPlay)
        assertEquals(30, stats.podcasts?.topShows?.single()?.episodes)
    }

    // ---------------- the API's edges ----------------

    @Test
    fun `an older server's 404 means both tabs are off`() = runTest {
        val api = AudioApi { _, _, _, _, _, _ -> 404 to ByteArray(0) }
        assertEquals(AudioEnabled.OFF, api.enabled())
        assertNull(api.lyrics("x"))
        assertNull(api.musicDownloadStatus())
    }

    @Test
    fun `server errors become the exception message`() = runTest {
        val api = AudioApi { _, _, _, _, _, _ -> 400 to """{"errors":["No name.","Folder missing."]}""".toByteArray() }
        val error = runCatching { api.saveMusicLibrary(null, "", "/x") }.exceptionOrNull()
        assertTrue(error is AudioApiException)
        assertEquals("No name.\nFolder missing.", error?.message)
    }

    @Test
    fun `queries leave out blank parameters`() = runTest {
        var seen: Map<String, String> = emptyMap()
        var seenPath = ""
        val api = AudioApi { _, path, query, _, _, _ ->
            seen = query
            seenPath = path
            200 to """{"items":[],"total":0}""".toByteArray()
        }
        api.albums(genre = "Rock", sort = "year")
        assertEquals("/api/music/albums", seenPath)
        assertEquals("Rock", seen["genre"])
        assertFalse(seen.containsKey("q"))
        assertFalse(seen.containsKey("decade"))
    }

    @Test
    fun `stream paths carry this phone's codecs`() {
        assertEquals("/api/music/tracks/a%20b/stream?codecs=mp3,aac,flac,alac,opus,vorbis,wav", AudioApi.trackStreamPath("a b"))
        assertTrue(AudioApi.trackDownloadPath("x", 128).endsWith("&maxBitrate=128"))
        assertEquals("/api/podcasts/episodes/e/audio?download=1", AudioApi.episodeAudioPath("e", download = true))
        assertEquals("/api/music/albums/a/art?size=300", AudioApi.sized("/api/music/albums/a/art", 300))
        assertEquals("https://x/y.jpg", AudioApi.sized("https://x/y.jpg", 300))
    }
}

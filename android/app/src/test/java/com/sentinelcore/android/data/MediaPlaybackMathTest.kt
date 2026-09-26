package com.sentinelcore.android.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class MediaPlaybackMathTest {

    @Test
    fun `an hls position is the playlist clock plus where the playlist started`() {
        assertEquals(1265.5, MediaPlaybackMath.mediaPositionSeconds("hls", 1200.0, 65_500), 0.0001)
    }

    @Test
    fun `a direct position is the player's own`() {
        assertEquals(65.5, MediaPlaybackMath.mediaPositionSeconds("direct", 1200.0, 65_500), 0.0001)
    }

    @Test
    fun `a negative player position counts as zero`() {
        assertEquals(1200.0, MediaPlaybackMath.mediaPositionSeconds("hls", 1200.0, -40), 0.0001)
    }

    @Test
    fun `direct seeks to the start, hls begins at zero`() {
        assertEquals(1_200_000L, MediaPlaybackMath.initialSeekMillis("direct", 1200.0))
        assertEquals(0L, MediaPlaybackMath.initialSeekMillis("hls", 1200.0))
        assertEquals(0L, MediaPlaybackMath.initialSeekMillis("direct", 0.0))
    }

    @Test
    fun `resume is offered only part-way through`() {
        assertNull(MediaPlaybackMath.resumeSeconds(null))
        assertNull(MediaPlaybackMath.resumeSeconds(MediaProgress(10.0, 6000.0, 0.2, watched = false)))
        assertNull(MediaPlaybackMath.resumeSeconds(MediaProgress(5900.0, 6000.0, 98.0, watched = true)))
        assertEquals(1200.0, MediaPlaybackMath.resumeSeconds(MediaProgress(1200.0, 6000.0, 20.0, watched = false))!!, 0.0)
    }

    @Test
    fun `under thirty seconds or with no duration is not worth reporting`() {
        assertFalse(MediaPlaybackMath.worthReporting(29.9, 6000.0))
        assertFalse(MediaPlaybackMath.worthReporting(600.0, null))
        assertTrue(MediaPlaybackMath.worthReporting(30.0, 6000.0))
    }

    @Test
    fun `clocks and runtimes format`() {
        assertEquals("0:05", MediaPlaybackMath.formatClock(5.9))
        assertEquals("23:45", MediaPlaybackMath.formatClock(1425.0))
        assertEquals("1:02:03", MediaPlaybackMath.formatClock(3723.0))
        assertEquals("1h 42m", MediaPlaybackMath.formatRuntime(102))
        assertEquals("2h", MediaPlaybackMath.formatRuntime(120))
        assertEquals("45m", MediaPlaybackMath.formatRuntime(45))
        assertEquals("1.7 GB", MediaPlaybackMath.formatSize(1_734_000_000))
        assertEquals("640 MB", MediaPlaybackMath.formatSize(640_000_000))
    }

    @Test
    fun `artwork downsamples by powers of two without going below the target`() {
        assertEquals(1, MediaPlaybackMath.imageSampleSize(300, 360))
        assertEquals(1, MediaPlaybackMath.imageSampleSize(700, 360))
        assertEquals(2, MediaPlaybackMath.imageSampleSize(780, 360))
        assertEquals(4, MediaPlaybackMath.imageSampleSize(2000, 360))
        assertEquals(1, MediaPlaybackMath.imageSampleSize(0, 360))
    }

    @Test
    fun `playback asks for the android codecs and omits start to resume`() {
        assertEquals(
            "/api/media/items/abc/playback?video=h264,hevc&audio=aac,ac3,eac3,mp3",
            MediaApi.playbackPath("abc", null),
        )
        assertEquals(
            "/api/media/items/abc/playback?start=0&video=h264,hevc&audio=aac,ac3,eac3,mp3",
            MediaApi.playbackPath("abc", 0.0),
        )
        assertEquals(
            "/api/media/items/a%20b/playback?start=1200.5&video=h264&audio=aac,mp3",
            MediaApi.playbackPath("a b", 1200.5, "h264", "aac,mp3"),
        )
    }

    @Test
    fun `codec lists drop what the phone cannot decode`() {
        assertEquals("h264,hevc" to "aac,ac3,eac3,mp3", MediaApi.mediaCodecs(hasHevc = true, hasAc3 = true, hasEac3 = true))
        assertEquals("h264" to "aac,mp3", MediaApi.mediaCodecs(hasHevc = false, hasAc3 = false, hasEac3 = false))
        assertEquals("h264,hevc" to "aac,eac3,mp3", MediaApi.mediaCodecs(hasHevc = true, hasAc3 = false, hasEac3 = true))
    }

    @Test
    fun `items query carries only the filters that are set`() {
        assertEquals(
            "/api/media/items?kind=movie&sort=title&order=asc&offset=0&limit=60",
            MediaApi.itemsPath("movie"),
        )
        assertEquals(
            "/api/media/items?q=alice+in&genre=Science+Fiction&sort=added&order=desc&unwatched=true&offset=60&limit=60",
            MediaApi.itemsPath(null, query = " alice in ", genre = "Science Fiction", sort = "added", unwatched = true, offset = 60),
        )
        assertEquals("asc", MediaApi.defaultOrder("title"))
        assertEquals("desc", MediaApi.defaultOrder("rating"))
    }

    @Test
    fun `season watched path`() {
        assertEquals("/api/media/items/s1/seasons/2/watched", MediaApi.seasonWatchedPath("s1", 2))
    }
}

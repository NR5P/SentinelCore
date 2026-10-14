package com.sentinelcore.android.data

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

// The radar map's lightning: the answer of core/SentinelCore.Web/LightningService.cs, merging and
// pruning across polls, and the dashboard's rules for how a flash looks and what the status says.
class WeatherLightningTest {

    private val home = GeoPoint(38.627, -90.1994)
    private val origin = 1_792_009_348_923L

    private fun flash(minutesAgo: Double, now: Long, lat: Double = 38.7, lon: Double = -90.1, energy: Double = 100.0) =
        LightningFlash(lat, lon, now - (minutesAgo * 60_000).toLong(), energy)

    @Test
    fun `an answer parses into flashes at origin plus seconds`() {
        val answer = WeatherLightning.parse(JSONObject("""
            { "state": "ready", "epoch": "66ecec5f", "sequence": 274, "incremental": false,
              "coverageStart": "2026-10-14T12:00:00+00:00", "coverageEnd": "2026-10-14T13:35:00+00:00",
              "origin": $origin, "truncated": false,
              "flashes": [[38.712, -90.301, 0.0, 42], [38.9, -89.8, 61.5, 2400]] }
        """.trimIndent()))

        assertTrue(answer.ready)
        assertEquals("66ecec5f", answer.epoch)
        assertEquals(274L, answer.sequence)
        assertFalse(answer.incremental)
        assertEquals(2, answer.flashes.size)
        assertEquals(origin, answer.flashes[0].timeMs)
        assertEquals(origin + 61_500, answer.flashes[1].timeMs)
        assertEquals(38.712, answer.flashes[0].latitude, 1e-9)
        assertEquals(-90.301, answer.flashes[0].longitude, 1e-9)
        assertEquals(2400.0, answer.flashes[1].energy, 1e-9)
    }

    @Test
    fun `a loading answer with no epoch yet parses`() {
        val answer = WeatherLightning.parse(JSONObject("""{ "state": "loading", "epoch": null, "sequence": 0, "origin": 0, "flashes": [] }"""))
        assertFalse(answer.ready)
        assertNull(answer.epoch)
        assertTrue(answer.flashes.isEmpty())
    }

    @Test
    fun `incremental answers append, full ones replace, and only 95 minutes are kept`() {
        val now = origin + 100 * 60_000L
        val held = LightningData("e1", 10, "ready", listOf(flash(120.0, now), flash(30.0, now), flash(5.0, now)))
        val more = LightningAnswer("ready", "e1", 11, incremental = true, truncated = false, flashes = listOf(flash(1.0, now)))

        val appended = WeatherLightning.merge(held, more, now)
        assertEquals(11L, appended.sequence)
        // The two-hour-old flash is gone; the rest stay, oldest first.
        assertEquals(listOf(30.0, 5.0, 1.0), appended.flashes.map { (now - it.timeMs) / 60_000.0 })

        val replaced = WeatherLightning.merge(held, more.copy(incremental = false, epoch = "e2"), now)
        assertEquals("e2", replaced.epoch)
        assertEquals(1, replaced.flashes.size)
    }

    @Test
    fun `the box is the padded view plus home's surroundings, and the path carries epoch and after`() {
        val box = WeatherLightning.box(LightningBox(-91.0, 38.0, -90.0, 39.0), home)
        assertEquals(-92.1994, box.west, 1e-9)
        assertEquals(37.127, box.south, 1e-9)
        assertEquals(-88.1994, box.east, 1e-9)
        assertEquals(40.127, box.north, 1e-9)
        assertTrue(box.contains(LightningBox(-91.0, 38.0, -90.0, 39.0)))
        assertFalse(box.contains(LightningBox(-95.0, 38.0, -90.0, 39.0)))

        assertEquals(
            "/api/weather/lightning?west=-92.199&south=37.127&east=-88.199&north=40.127",
            WeatherLightning.path(box),
        )
        assertEquals(
            "/api/weather/lightning?west=-92.199&south=37.127&east=-88.199&north=40.127&epoch=66ecec5f&after=274",
            WeatherLightning.path(box, "66ecec5f", 274),
        )
    }

    @Test
    fun `the moment is the frame's time, or now on the newest frame`() {
        val frames = listOf("2026-10-14T13:00:00Z", "2026-10-14T13:10:00Z")
        assertEquals(java.time.Instant.parse("2026-10-14T13:00:00Z").toEpochMilli(), WeatherLightning.frameMoment(frames, 0))
        assertNull(WeatherLightning.frameMoment(frames, 1))
        assertNull(WeatherLightning.frameMoment(listOf(null), 0))
        assertNull(WeatherLightning.frameMoment(emptyList(), 0))
    }

    @Test
    fun `only the 15 minutes before the moment show`() {
        val now = origin
        val flashes = listOf(flash(20.0, now), flash(14.0, now), flash(0.0, now), flash(-1.0, now))
        assertEquals(listOf(14.0, 0.0), WeatherLightning.showing(flashes, now).map { (now - it.timeMs) / 60_000.0 })
    }

    @Test
    fun `new flashes are bolts, then dots that redden and fade`() {
        assertEquals(LightningMark.Bolt, WeatherLightning.mark(90_000, 100.0))
        val fresh = WeatherLightning.mark(3 * 60_000L, 1.0) as LightningMark.Dot
        assertEquals(0xFFFFEB3B, fresh.color)
        assertEquals(2f, fresh.radiusDp, 1e-6f)
        assertEquals(1 - 0.55f * 3 / 15, fresh.alpha, 1e-6f)
        assertEquals(0xFFFF9800, (WeatherLightning.mark(7 * 60_000L, 100.0) as LightningMark.Dot).color)
        val old = WeatherLightning.mark(14 * 60_000L, 1_000_000.0) as LightningMark.Dot
        assertEquals(0xFFF44336, old.color)
        // Very bright flashes cap at 2 + 2.5.
        assertEquals(4.5f, old.radiusDp, 1e-6f)
        // 1000 fJ: 2 + (3 - 1.5) * 0.9.
        assertEquals(3.35f, (WeatherLightning.mark(3 * 60_000L, 1000.0) as LightningMark.Dot).radiusDp, 1e-5f)
    }

    @Test
    fun `compass points and bearings`() {
        assertEquals("N", WeatherLightning.compass(0.0))
        assertEquals("N", WeatherLightning.compass(359.0))
        assertEquals("NNE", WeatherLightning.compass(22.5))
        assertEquals("NE", WeatherLightning.compass(-315.0))
        assertEquals("W", WeatherLightning.compass(-90.0))
        assertEquals("E", WeatherLightning.compass(WeatherLightning.bearing(home, GeoPoint(home.latitude, home.longitude + 0.5))))
        assertEquals("S", WeatherLightning.compass(WeatherLightning.bearing(home, GeoPoint(home.latitude - 0.5, home.longitude))))
    }

    @Test
    fun `distance from home reads in tenths under ten miles`() {
        // 0.1 degree of latitude is about 6.9 miles.
        assertEquals("6.9 mi N", WeatherLightning.fromHomeText(home, home.latitude + 0.1, home.longitude))
        assertEquals("69 mi S", WeatherLightning.fromHomeText(home, home.latitude - 1.0, home.longitude))
    }

    @Test
    fun `the status line counts what's in view and names the nearest flash`() {
        val now = origin
        val view = LightningBox(-90.5, 38.5, -89.5, 39.0)
        val data = LightningData("e", 1, "ready", listOf(
            flash(3.0, now, lat = home.latitude + 0.09, lon = home.longitude + 0.09),
            flash(10.0, now, lat = 40.0, lon = -90.0),
            flash(30.0, now, lat = home.latitude, lon = home.longitude),
        ))
        val status = WeatherLightning.status(data, view, home, now)
        assertEquals("⚡ 1 flash in view, last 15 min · nearest home 7.9 mi NE, 3 min ago", status.text)
        assertEquals(LightningLevel.CLOSE, status.level)
    }

    @Test
    fun `the status line's quieter states`() {
        val now = origin
        val view = LightningBox(-91.0, 38.0, -89.0, 39.0)
        assertEquals("⚡ loading lightning…", WeatherLightning.status(LightningData(state = "loading"), view, home, now).text)
        assertEquals(
            "⚡ 0 flashes in view, last 15 min · none within 100 mi of home",
            WeatherLightning.status(LightningData(state = "ready"), view, home, now).text,
        )
        val far = LightningData("e", 1, "loading", listOf(flash(2.0, now, lat = 35.0, lon = -80.0)))
        val status = WeatherLightning.status(far, view, home, now)
        assertEquals("⚡ 0 flashes in view, last 15 min · still filling in", status.text)
        assertEquals(LightningLevel.NORMAL, status.level)
        // 20 miles out: orange.
        val near = LightningData("e", 1, "ready", listOf(flash(2.0, now, lat = home.latitude + 0.29, lon = home.longitude)))
        assertEquals(LightningLevel.NEAR, WeatherLightning.status(near, view, home, now).level)
        val big = LightningData("e", 1, "ready", List(1260) { flash(1.0, now) })
        assertTrue(WeatherLightning.status(big, view, null, now).text.startsWith("⚡ 1,260 flashes in view"))
    }

    @Test
    fun `a tapped flash's details`() {
        val now = origin
        val f = flash(3.0, now, lat = home.latitude + 0.1, lon = home.longitude, energy = 2400.4)
        val rows = WeatherLightning.details(f, now, onNewest = true, home = home) { "1:02:03 PM" }.toMap()
        assertEquals("1:02:03 PM", rows["time"])
        assertEquals("3 min ago", rows["age"])
        assertEquals("6.9 mi N", rows["from home"])
        assertEquals("38.727, -90.199 (good to a few miles)", rows["where"])
        assertEquals("2,400 fJ seen from orbit - a bright one", rows["brightness"])
        assertEquals("NOAA GOES-19 Lightning Mapper", rows["seen by"])

        val framed = WeatherLightning.details(f.copy(timeMs = now - 20_000), now, onNewest = false, home = null) { "" }.toMap()
        assertEquals("under a minute before this frame", framed["age"])
        assertNull(framed["from home"])
    }
}

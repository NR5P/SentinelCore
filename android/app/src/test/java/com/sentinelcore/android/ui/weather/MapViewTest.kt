package com.sentinelcore.android.ui.weather

import androidx.compose.ui.geometry.Offset
import com.sentinelcore.android.data.GeoPoint
import com.sentinelcore.android.data.Mercator
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

// The radar map's viewport arithmetic: which tiles cover the screen, where a place lands on it,
// and that pinching keeps the point under the fingers where it was.
class MapViewTest {

    private val home = GeoPoint(38.627, -90.1994)
    private val view = MapView(
        centerX = Mercator.x(home.longitude),
        centerY = Mercator.y(home.latitude),
        zoom = 8.0,
        width = 1080f,
        height = 1000f,
        tilePx = 256f,
    )

    @Test
    fun `the centre of the view is the centre point`() {
        val at = view.screen(home)
        assertEquals(540f, at.x, 0.01f)
        assertEquals(500f, at.y, 0.01f)
        val back = view.geo(Offset(540f, 500f))
        assertEquals(home.latitude, back.latitude, 1e-6)
        assertEquals(home.longitude, back.longitude, 1e-6)
    }

    @Test
    fun `tiles cover the whole canvas, nearest the middle first`() {
        val tiles = view.tiles(8)
        // St. Louis at zoom 8 is in tile (63, 98).
        assertEquals(63 to 98, tiles.first())
        val n = 256.0
        val xs = tiles.map { it.first }
        val ys = tiles.map { it.second }
        assertTrue(xs.min() / n <= view.centerX - 540 / view.worldPx)
        assertTrue((xs.max() + 1) / n >= view.centerX + 540 / view.worldPx)
        assertTrue(ys.min() / n <= view.centerY - 500 / view.worldPx)
        assertTrue((ys.max() + 1) / n >= view.centerY + 500 / view.worldPx)
    }

    @Test
    fun `tile zoom rounds and stops at the layer's deepest native zoom`() {
        assertEquals(8, MapView.tileZoom(8.4, 16))
        assertEquals(9, MapView.tileZoom(8.6, 16))
        assertEquals(12, MapView.tileZoom(14.0, 12))
    }

    @Test
    fun `zooming around a point keeps that point under the finger`() {
        val finger = Offset(200f, 300f)
        val before = view.geo(finger)
        val (x, y) = zoomedCenter(view, finger, 9.5)
        val after = view.copy(centerX = x, centerY = y, zoom = 9.5).geo(finger)
        assertEquals(before.latitude, after.latitude, 1e-9)
        assertEquals(before.longitude, after.longitude, 1e-9)
    }

    @Test
    fun `a pan moves the map with the finger`() {
        val finger = Offset(600f, 600f)
        val pan = Offset(100f, -50f)
        val grabbed = view.geo(finger - pan)
        val (x, y) = zoomedCenter(view, finger, view.zoom, pan)
        val after = view.copy(centerX = x, centerY = y).geo(finger)
        assertEquals(grabbed.latitude, after.latitude, 1e-9)
        assertEquals(grabbed.longitude, after.longitude, 1e-9)
    }

    @Test
    fun `bounds widen with padding`() {
        val tight = view.bounds()
        val padded = view.bounds(0.3)
        assertTrue(padded[0] < tight[0] && padded[2] > tight[2])
        assertTrue(padded[1] < tight[1] && padded[3] > tight[3])
        assertTrue(tight[0] < home.longitude && home.longitude < tight[2])
        assertTrue(tight[1] < home.latitude && home.latitude < tight[3])
    }
}

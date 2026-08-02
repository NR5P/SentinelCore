package com.sentinelcore.android.data

import org.junit.Assert.assertEquals
import org.junit.Test

// The Live grid's arrangement rules, which have to hold while the camera list moves underneath a
// saved order - a camera deleted on another device, or one added since this order was stored.
class LiveCameraOrderTest {
    private fun camera(id: String, name: String = id) = Camera(
        id = id,
        name = name,
        streamUrl = "rtsp://camera/$id",
        includeAudio = false,
        ptzEnabled = false,
        onvifPtzServiceUrl = null,
        onvifProfileToken = null,
    )

    private val gate = camera("gate")
    private val driveway = camera("driveway")
    private val frontDoor = camera("front-door")

    @Test
    fun `arranges cameras into the saved order`() {
        val arranged = LiveCameraOrder.arrange(
            order = listOf("front-door", "gate", "driveway"),
            cameras = listOf(gate, driveway, frontDoor),
        )

        assertEquals(listOf(frontDoor, gate, driveway), arranged)
    }

    // Someone who has never dragged a tile sees the camera list exactly as the server sent it.
    @Test
    fun `falls back to the camera list's own order when nothing is saved`() {
        val arranged = LiveCameraOrder.arrange(order = emptyList(), cameras = listOf(gate, driveway))

        assertEquals(listOf(gate, driveway), arranged)
    }

    @Test
    fun `drops placements for cameras that no longer exist`() {
        val arranged = LiveCameraOrder.arrange(
            order = listOf("front-door", "deleted-camera", "gate"),
            cameras = listOf(gate, frontDoor),
        )

        assertEquals(listOf(frontDoor, gate), arranged)
    }

    // Appearing at the end is predictable; slotting a new camera in by name would shove tiles
    // somebody deliberately placed sideways.
    @Test
    fun `puts cameras the saved order has never seen last`() {
        val added = camera("side-gate")
        val arranged = LiveCameraOrder.arrange(
            order = listOf("front-door", "gate"),
            cameras = listOf(gate, added, frontDoor),
        )

        assertEquals(listOf(frontDoor, gate, added), arranged)
    }

    @Test
    fun `moving a camera onto a later one closes the gap behind it`() {
        val moved = LiveCameraOrder.move(
            ids = listOf("gate", "driveway", "front-door"),
            movingId = "gate",
            targetId = "front-door",
        )

        assertEquals(listOf("driveway", "front-door", "gate"), moved)
    }

    @Test
    fun `moving a camera onto an earlier one pushes that one along`() {
        val moved = LiveCameraOrder.move(
            ids = listOf("gate", "driveway", "front-door"),
            movingId = "front-door",
            targetId = "gate",
        )

        assertEquals(listOf("front-door", "gate", "driveway"), moved)
    }

    // A drag that raced a camera being deleted elsewhere, and a tile dropped back on itself.
    @Test
    fun `leaves the order alone when either camera isn't in it`() {
        val ids = listOf("gate", "driveway")

        assertEquals(ids, LiveCameraOrder.move(ids, movingId = "gate", targetId = "gate"))
        assertEquals(ids, LiveCameraOrder.move(ids, movingId = "gone", targetId = "gate"))
        assertEquals(ids, LiveCameraOrder.move(ids, movingId = "gate", targetId = "gone"))
    }
}

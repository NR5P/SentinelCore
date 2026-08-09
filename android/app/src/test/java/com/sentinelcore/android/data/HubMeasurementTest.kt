package com.sentinelcore.android.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

// The two rules a client has to get right about numeric sensor readings, both of which are shared
// with the web dashboard and the Apple clients: how a raw number is written down, and when a
// device with no on/off counts as working rather than unknown.
class HubMeasurementTest {
    private fun device(
        commandable: Boolean = false,
        power: String = "unknown",
        measurements: List<HubMeasurement> = emptyList(),
    ) = HubDevice(
        id = "d1",
        name = "Barn",
        kind = "sensor",
        room = "",
        enabled = true,
        commandTopic = "",
        stateTopic = "sentinel-test/barn",
        availabilityTopic = "",
        payloadOn = "ON",
        payloadOff = "OFF",
        payloadAvailable = "online",
        payloadNotAvailable = "offline",
        schema = "raw",
        stateValueTemplate = "",
        optimistic = false,
        supportsBrightness = false,
        brightnessCommandTopic = "",
        brightnessStateTopic = "",
        brightnessScale = 255,
        discoverySource = "manual",
        power = power,
        brightness = null,
        available = true,
        commandable = commandable,
        measurements = measurements,
    )

    private fun formatted(value: Double) = HubMeasurement("k", value).formattedValue

    @Test
    fun `rounds to two decimals and drops the trailing zeros`() {
        // The float noise comes from scaling upstream, not from the sensor.
        assertEquals("21.5", formatted(21.500000000000004))
        assertEquals("48", formatted(48.0))
        assertEquals("1.23", formatted(1.23))
        assertEquals("120", formatted(120.0))
    }

    @Test
    fun `keeps whole numbers whole rather than eating their zeros`() {
        // "100.00" trimmed carelessly becomes "1" - the decimal point is what stops the trim.
        assertEquals("100", formatted(100.0))
        assertEquals("1000", formatted(1000.0))
        assertEquals("0", formatted(0.0))
    }

    @Test
    fun `rounds rather than truncates, and keeps negatives signed`() {
        assertEquals("1.24", formatted(1.235))
        assertEquals("-4.5", formatted(-4.5))
        // Rounding a hair below zero must not leave a bare "-0" on the tile.
        assertEquals("0", formatted(-0.001))
    }

    @Test
    fun `has nothing to show for a reading that is not a number`() {
        assertEquals("-", formatted(Double.NaN))
        assertEquals("-", formatted(Double.POSITIVE_INFINITY))
    }

    @Test
    fun `a sensor publishing numbers is reporting, not unknown`() {
        assertTrue(device(measurements = listOf(HubMeasurement("temperature", 21.5))).isReporting)
    }

    @Test
    fun `a sensor that has reported nothing yet is not reporting`() {
        assertFalse(device().isReporting)
    }

    @Test
    fun `a device that can be commanded is never reporting, whatever it publishes`() {
        // A lamp that also reports power draw still has an on/off, and that is what its tile says.
        assertFalse(
            device(
                commandable = true,
                power = "on",
                measurements = listOf(HubMeasurement("ENERGY.Power", 12.0)),
            ).isReporting,
        )
    }
}

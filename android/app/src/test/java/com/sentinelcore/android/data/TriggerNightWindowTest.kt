package com.sentinelcore.android.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

// The client half of the night window's wire contract: absent is not off. The server merges any of
// the three keys a client didn't send against the stored row, so collapsing "the server never told
// us" into "the user turned it off" here would switch someone's window off the next time they
// nudged a polygon from their phone - see core/docs/NIGHT_WINDOW_HANDOFF.md.
class TriggerNightWindowTest {
    private fun config(
        nightWindowEnabled: Boolean? = null,
        nightStartHour: Int? = null,
        nightEndHour: Int? = null,
    ) = TriggerConfig(
        enabled = true,
        zonePolygon = emptyList(),
        minTriggerArea = 0.02,
        zones = null,
        cooldownSeconds = 5,
        maxTriggersPerMinute = 6,
        shadowSensitivity = 50,
        recordClips = false,
        clipPreSeconds = 5,
        clipPostSeconds = 10,
        sendToAlerts = false,
        alertProfile = "default",
        nightWindowEnabled = nightWindowEnabled,
        nightStartHour = nightStartHour,
        nightEndHour = nightEndHour,
    )

    // A server older than the feature sends none of the three keys, and the editor still has to
    // show a window. The fallbacks are the server's own: off, and the measured 21:00-07:00 rather
    // than 0/0, which would be zero-width and appear to do nothing once switched on.
    @Test
    fun `falls back to the server's defaults when the keys are absent`() {
        assertEquals(NightWindow(false, 21, 7), config().resolvedNightWindow())
    }

    @Test
    fun `keeps what the server actually sent`() {
        assertEquals(
            NightWindow(true, 22, 6),
            config(nightWindowEnabled = true, nightStartHour = 22, nightEndHour = 6).resolvedNightWindow(),
        )
    }

    // Midnight is a legitimate hour at both ends, and the one a `?: 21` style fallback swallows if
    // anything downstream ever treats 0 as "unset".
    @Test
    fun `hour zero is a real hour, not a missing one`() {
        assertEquals(
            NightWindow(true, 0, 6),
            config(nightWindowEnabled = true, nightStartHour = 0, nightEndHour = 6).resolvedNightWindow(),
        )
        assertEquals(
            NightWindow(true, 21, 0),
            config(nightWindowEnabled = true, nightStartHour = 21, nightEndHour = 0).resolvedNightWindow(),
        )
    }

    // The distinction the whole nullable contract exists for: a config that was never told about
    // the window must not look like one whose owner turned it off.
    @Test
    fun `an untold config is distinguishable from an explicit off`() {
        assertNull(config().nightWindowEnabled)
        assertEquals(false, config(nightWindowEnabled = false, nightStartHour = 21, nightEndHour = 7).nightWindowEnabled)
    }
}

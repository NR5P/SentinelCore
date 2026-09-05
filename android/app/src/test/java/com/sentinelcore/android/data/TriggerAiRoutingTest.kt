package com.sentinelcore.android.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

// The client half of the AI routing flags' wire contract, which is the night window's contract
// again: absent is not off. The server merges either key a client didn't send against the stored
// row, so collapsing "the server never told us" into "the user turned it off" here would route the
// coop camera back through the fast detector the next time someone nudged a polygon from their
// phone - and nothing anywhere would say so. See core/docs/AI_ROUTING_HANDOFF.md.
class TriggerAiRoutingTest {
    private fun config(
        bypassFastLocal: Boolean? = null,
        keepOnlyAlertable: Boolean? = null,
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
        bypassFastLocal = bypassFastLocal,
        keepOnlyAlertable = keepOnlyAlertable,
    )

    // A server older than either column sends neither key, and the editor still has to show two
    // switches. Both fall back to the server's own default, which is off: routing every camera's
    // motion at the full model is far more analysis than the box can run, and narrowing what
    // counts as a real trigger on someone's behalf is how footage goes missing unasked.
    @Test
    fun `falls back to off when the keys are absent`() {
        assertFalse(config().resolvedBypassFastLocal())
        assertFalse(config().resolvedKeepOnlyAlertable())
    }

    @Test
    fun `keeps what the server actually sent`() {
        assertTrue(config(bypassFastLocal = true).resolvedBypassFastLocal())
        assertTrue(config(keepOnlyAlertable = true).resolvedKeepOnlyAlertable())
        assertFalse(config(bypassFastLocal = false).resolvedBypassFastLocal())
        assertFalse(config(keepOnlyAlertable = false).resolvedKeepOnlyAlertable())
    }

    // The two are independent: turning one on says nothing about the other, and the coop needs
    // both while a driveway wants neither.
    @Test
    fun `the two flags are independent`() {
        val bypassOnly = config(bypassFastLocal = true, keepOnlyAlertable = false)
        assertTrue(bypassOnly.resolvedBypassFastLocal())
        assertFalse(bypassOnly.resolvedKeepOnlyAlertable())

        val keepOnly = config(bypassFastLocal = false, keepOnlyAlertable = true)
        assertFalse(keepOnly.resolvedBypassFastLocal())
        assertTrue(keepOnly.resolvedKeepOnlyAlertable())
    }

    // The distinction the whole nullable contract exists for: a config that was never told about
    // either flag must not look like one whose owner turned them off.
    @Test
    fun `an untold config is distinguishable from an explicit off`() {
        assertNull(config().bypassFastLocal)
        assertNull(config().keepOnlyAlertable)
        assertEquals(false, config(bypassFastLocal = false).bypassFastLocal)
        assertEquals(false, config(keepOnlyAlertable = false).keepOnlyAlertable)
    }
}

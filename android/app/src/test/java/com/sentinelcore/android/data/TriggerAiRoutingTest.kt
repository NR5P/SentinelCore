package com.sentinelcore.android.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.json.JSONArray
import org.json.JSONObject
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
        fastLocalClasses: List<String>? = null,
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
        fastLocalClasses = fastLocalClasses,
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

    // Fast detector categories. The server sends null for "every category" (and an older server
    // sends no key at all); both must show every chip ticked.
    @Test
    fun `fast local classes parse null and absent as every category`() {
        assertNull(FastLocalCategories.parse(JSONObject()))
        assertNull(FastLocalCategories.parse(JSONObject().put("fastLocalClasses", JSONObject.NULL)))
        assertEquals(FastLocalCategories.keys.toSet(), config().resolvedFastLocalClasses())
    }

    @Test
    fun `fast local classes parse a stored subset`() {
        val json = JSONObject().put("fastLocalClasses", JSONArray(listOf("person", "car")))
        val parsed = FastLocalCategories.parse(json)
        assertEquals(listOf("person", "car"), parsed)
        assertEquals(setOf("person", "car"), config(fastLocalClasses = parsed).resolvedFastLocalClasses())
    }

    // A key from a newer server has no chip here, and the server refuses keys it doesn't know,
    // so it's dropped rather than shown or sent back.
    @Test
    fun `unknown fast local keys are dropped`() {
        assertEquals(setOf("dog"), config(fastLocalClasses = listOf("dog", "raccoon")).resolvedFastLocalClasses())
    }

    @Test
    fun `fast local classes round-trip in display order`() {
        val json = JSONObject()
        FastLocalCategories.write(json, listOf("Dog", "person", "dog"))
        val array = json.getJSONArray("fastLocalClasses")
        assertEquals(listOf("person", "dog"), (0 until array.length()).map { array.getString(it) })
        assertEquals(listOf("person", "dog"), FastLocalCategories.parse(JSONObject(json.toString())))
    }

    @Test
    fun `every category ticked is sent as the full list`() {
        val json = JSONObject()
        FastLocalCategories.write(json, FastLocalCategories.keys.reversed())
        assertEquals(FastLocalCategories.keys, FastLocalCategories.parse(json))
    }

    // Null leaves the key out (keep what's stored); an empty list would be a 400, so it's never sent.
    @Test
    fun `null or empty fast local classes leave the key out`() {
        val untold = JSONObject()
        FastLocalCategories.write(untold, null)
        assertFalse(untold.has("fastLocalClasses"))

        val empty = JSONObject()
        FastLocalCategories.write(empty, emptyList())
        assertFalse(empty.has("fastLocalClasses"))
    }

    @Test
    fun `fast local labels are the capitalized keys`() {
        assertEquals("Person", FastLocalCategories.label("person"))
        assertEquals("Elephant", FastLocalCategories.label("elephant"))
        assertEquals(14, FastLocalCategories.keys.size)
    }
}

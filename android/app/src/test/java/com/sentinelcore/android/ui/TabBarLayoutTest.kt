package com.sentinelcore.android.ui

import com.sentinelcore.android.ui.AppSection.DEVICES
import com.sentinelcore.android.ui.AppSection.EVENTS
import com.sentinelcore.android.ui.AppSection.FILES
import com.sentinelcore.android.ui.AppSection.LIVE
import com.sentinelcore.android.ui.AppSection.ORGANIZER
import com.sentinelcore.android.ui.AppSection.SETTINGS
import com.sentinelcore.android.ui.AppSection.TIMELINE
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TabBarLayoutTest {

    private val four = AppSection.visible(deviceHubEnabled = false, organizerEnabled = false, filesEnabled = false)
    private val five = AppSection.visible(deviceHubEnabled = true, organizerEnabled = false, filesEnabled = false)
    private val six = AppSection.visible(deviceHubEnabled = true, organizerEnabled = true, filesEnabled = false)
    private val seven = AppSection.visible(deviceHubEnabled = true, organizerEnabled = true, filesEnabled = true)

    @Test
    fun `every section gets a tab by default when four are visible`() {
        val layout = TabBarLayout(pinned = null, visible = four)

        assertEquals(listOf(EVENTS, TIMELINE, LIVE, SETTINGS), layout.bar)
        assertFalse(layout.showsMore)
        assertEquals(5, TabBarLayout.limit(four))
    }

    @Test
    fun `every section gets a tab by default when five are visible`() {
        val layout = TabBarLayout(pinned = null, visible = five)

        assertEquals(listOf(EVENTS, TIMELINE, LIVE, DEVICES, SETTINGS), layout.bar)
        assertTrue(layout.more.isEmpty())
        assertEquals(5, TabBarLayout.limit(five))
    }

    @Test
    fun `six visible default to the first four plus More`() {
        val layout = TabBarLayout(pinned = null, visible = six)

        assertEquals(listOf(EVENTS, TIMELINE, LIVE, ORGANIZER), layout.bar)
        assertEquals(listOf(DEVICES, SETTINGS), layout.more)
        assertTrue(layout.showsMore)
        assertEquals(4, TabBarLayout.limit(six))
    }

    @Test
    fun `seven visible default to the first four plus More`() {
        val layout = TabBarLayout(pinned = null, visible = seven)

        assertEquals(listOf(EVENTS, TIMELINE, LIVE, ORGANIZER), layout.bar)
        assertEquals(listOf(FILES, DEVICES, SETTINGS), layout.more)
        assertTrue(layout.showsMore)
        assertEquals(4, TabBarLayout.limit(seven))
    }

    @Test
    fun `the Files section sits after Organizer and only when the server has it on`() {
        assertEquals(listOf(EVENTS, TIMELINE, LIVE, ORGANIZER, FILES, DEVICES, SETTINGS), seven)
        assertFalse(FILES in six)
        assertEquals(
            listOf(EVENTS, TIMELINE, LIVE, FILES, SETTINGS),
            AppSection.visible(deviceHubEnabled = false, organizerEnabled = false, filesEnabled = true),
        )
    }

    @Test
    fun `a customized list keeps its order and sends the rest to More in section order`() {
        val layout = TabBarLayout(pinned = listOf(SETTINGS, LIVE, DEVICES), visible = six)

        assertEquals(listOf(SETTINGS, LIVE, DEVICES), layout.bar)
        assertEquals(listOf(EVENTS, TIMELINE, ORGANIZER), layout.more)
    }

    @Test
    fun `pinning every visible section when they fit leaves no More`() {
        val layout = TabBarLayout(pinned = listOf(SETTINGS, LIVE, TIMELINE, EVENTS), visible = four)

        assertEquals(listOf(SETTINGS, LIVE, TIMELINE, EVENTS), layout.bar)
        assertFalse(layout.showsMore)
    }

    @Test
    fun `a pinned list with a section left out adds More even when everything would fit`() {
        val layout = TabBarLayout(pinned = listOf(EVENTS, LIVE), visible = four)

        assertEquals(listOf(EVENTS, LIVE), layout.bar)
        assertEquals(listOf(TIMELINE, SETTINGS), layout.more)
    }

    @Test
    fun `a pinned list longer than four is cut to four when More is needed`() {
        val layout = TabBarLayout(pinned = listOf(SETTINGS, DEVICES, ORGANIZER, LIVE, TIMELINE), visible = six)

        assertEquals(listOf(SETTINGS, DEVICES, ORGANIZER, LIVE), layout.bar)
        assertEquals(listOf(EVENTS, TIMELINE), layout.more)
    }

    @Test
    fun `a pinned section switched off server-side drops out`() {
        val layout = TabBarLayout(pinned = listOf(ORGANIZER, LIVE, SETTINGS), visible = five)

        assertEquals(listOf(LIVE, SETTINGS), layout.bar)
        assertEquals(listOf(EVENTS, TIMELINE, DEVICES), layout.more)
    }

    @Test
    fun `a pinned list with nothing left visible falls back to the default`() {
        val layout = TabBarLayout(pinned = listOf(ORGANIZER, DEVICES), visible = four)

        assertEquals(TabBarLayout(pinned = null, visible = four).bar, layout.bar)
        assertFalse(layout.showsMore)
    }

    @Test
    fun `duplicates in the pinned list are dropped`() {
        val layout = TabBarLayout(pinned = listOf(LIVE, LIVE, EVENTS), visible = four)

        assertEquals(listOf(LIVE, EVENTS), layout.bar)
    }

    @Test
    fun `encode and decode round trip`() {
        val sections = listOf(SETTINGS, ORGANIZER, EVENTS, DEVICES)

        assertEquals("settings,organizer,events,devices", TabBarLayout.encode(sections))
        assertEquals(sections, TabBarLayout.decode(TabBarLayout.encode(sections)))
    }

    @Test
    fun `blank storage means never customized`() {
        assertNull(TabBarLayout.decode(""))
        assertNull(TabBarLayout.decode(null))
    }

    @Test
    fun `garbage in storage is skipped`() {
        assertNull(TabBarLayout.decode("nonsense,,EVENTS, ,{}"))
        assertEquals(listOf(LIVE, SETTINGS), TabBarLayout.decode("live,cameras,,settings"))
    }
}

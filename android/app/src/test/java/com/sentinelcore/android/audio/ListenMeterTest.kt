package com.sentinelcore.android.audio

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ListenMeterTest {
    private fun ListenMeter.playFrom(start: Double, until: Double, step: Double = 0.5, wallPerStep: Double = step, speed: Double = 1.0) {
        var position = start
        while (position < until - 1e-9) {
            position = minOf(until, position + step)
            sample(position, wallPerStep, speed)
        }
    }

    @Test
    fun `counts media progress, not wall-clock time`() {
        val meter = ListenMeter()
        meter.rebase(0.0)
        // The 28 s track that took 38.7 s of wall-clock time: position advances 28 s in total.
        meter.playFrom(0.0, 28.0, step = 0.5, wallPerStep = 0.69)
        assertEquals(28.0, meter.total(28.0, reachedEnd = true), 1e-6)
    }

    @Test
    fun `stalls and buffering add nothing`() {
        val meter = ListenMeter()
        meter.rebase(0.0)
        meter.playFrom(0.0, 10.0)
        repeat(20) { meter.sample(10.0, 0.5) } // buffering: the position sits still
        meter.playFrom(10.0, 15.0)
        assertEquals(15.0, meter.total(200.0, reachedEnd = false), 1e-6)
    }

    @Test
    fun `seeks forward and back are not counted`() {
        val meter = ListenMeter()
        meter.rebase(0.0)
        meter.playFrom(0.0, 10.0)
        meter.sample(70.0, 0.5) // a jump the ticker saw before the seek callback
        meter.playFrom(70.0, 75.0)
        meter.sample(20.0, 0.5) // backwards
        meter.playFrom(20.0, 25.0)
        assertEquals(20.0, meter.total(300.0, reachedEnd = false), 1e-6)
    }

    @Test
    fun `rebase after a seek starts counting from the new spot`() {
        val meter = ListenMeter()
        meter.rebase(0.0)
        meter.playFrom(0.0, 5.0)
        meter.rebase(100.0)
        meter.sample(100.5, 0.5)
        assertEquals(5.5, meter.total(300.0, reachedEnd = false), 1e-6)
    }

    @Test
    fun `small forward steps within the jump limit count`() {
        val meter = ListenMeter()
        meter.rebase(0.0)
        meter.sample(4.0, 0.5)
        assertEquals(4.0, meter.total(300.0, reachedEnd = false), 1e-6)
        meter.sample(10.0, 0.5) // 6 s in half a second is a jump
        assertEquals(4.0, meter.total(300.0, reachedEnd = false), 1e-6)
    }

    @Test
    fun `a late tick still counts when that much real time passed`() {
        val meter = ListenMeter()
        meter.rebase(0.0)
        meter.sample(20.0, 20.0)
        assertEquals(20.0, meter.total(300.0, reachedEnd = false), 1e-6)
    }

    @Test
    fun `faster playback allows bigger steps`() {
        val meter = ListenMeter()
        meter.rebase(0.0)
        meter.sample(6.0, 3.0, speed = 2.0)
        assertEquals(6.0, meter.total(300.0, reachedEnd = false), 1e-6)
    }

    @Test
    fun `the first sample without a baseline only sets one`() {
        val meter = ListenMeter()
        meter.sample(42.0, 0.5)
        meter.sample(42.5, 0.5)
        assertEquals(0.5, meter.total(300.0, reachedEnd = false), 1e-6)
    }

    @Test
    fun `reaching the end credits the short tail after the last sample`() {
        val meter = ListenMeter()
        meter.rebase(0.0)
        meter.playFrom(0.0, 179.6)
        assertEquals(179.6, meter.total(180.0, reachedEnd = false), 1e-6)
        assertEquals(180.0, meter.total(180.0, reachedEnd = true), 1e-6)
    }

    @Test
    fun `no tail credit when the last sample was far from the end`() {
        val meter = ListenMeter()
        meter.rebase(0.0)
        meter.playFrom(0.0, 30.0)
        assertEquals(30.0, meter.total(180.0, reachedEnd = true), 1e-6)
    }

    @Test
    fun `the total is capped at the duration`() {
        val meter = ListenMeter()
        meter.rebase(0.0)
        meter.playFrom(0.0, 31.0)
        assertEquals(30.0, meter.total(30.0, reachedEnd = true), 1e-6)
        assertEquals(31.0, meter.total(0.0, reachedEnd = true), 1e-6) // unknown length: no cap
    }

    @Test
    fun `server rules line up with the measured time`() {
        // Skip after 20 s of real progress even if buffering stretched it past 30 s of wall time.
        val skipped = ListenMeter().apply { rebase(0.0); playFrom(0.0, 20.0, wallPerStep = 0.9) }
        val secs = skipped.total(200.0, reachedEnd = false)
        assertTrue(PlayReporting.isSkip(secs, completed = false, userSkipped = true))
        assertFalse(PlayReporting.countsAsPlay(secs, 200.0))

        val played = ListenMeter().apply { rebase(0.0); playFrom(0.0, 100.0) }
        assertTrue(PlayReporting.countsAsPlay(played.total(200.0, reachedEnd = false), 200.0))
    }
}

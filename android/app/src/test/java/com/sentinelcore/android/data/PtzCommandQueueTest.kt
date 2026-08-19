package com.sentinelcore.android.data

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Test

// The PTZ control's press and release are two separate network calls, and the camera keeps moving
// until the second one lands. These pin the ordering that makes that safe.
class PtzCommandQueueTest {
    @Test
    fun `runs commands in the order they were enqueued`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.Default)
        val queue = PtzCommandQueue(scope)
        val log = mutableListOf<String>()

        queue.enqueue { log.add("move") }
        queue.enqueue { log.add("stop") }
        queue.awaitIdle()

        assertEquals(listOf("move", "stop"), log)
    }

    // The regression itself. A slow move followed immediately by a fast stop is exactly the quick
    // tap that used to leave the camera spinning: unqueued, the stop would finish first and the
    // move would arrive at the camera afterwards, with nothing behind it to stop it again.
    @Test
    fun `a slow move still lands before the stop that follows it`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.Default)
        val queue = PtzCommandQueue(scope)
        val log = mutableListOf<String>()

        queue.enqueue {
            delay(50)
            log.add("move")
        }
        queue.enqueue { log.add("stop") }
        queue.awaitIdle()

        assertEquals(listOf("move", "stop"), log)
    }

    // A move that fails must not take the stop down with it. If a failed command broke the chain,
    // the next command would join a cancelled job - and the command most likely to follow a failed
    // move is the stop that ends it.
    @Test
    fun `a failing command does not stop later ones from running`() = runBlocking {
        val scope = CoroutineScope(Dispatchers.Default)
        val queue = PtzCommandQueue(scope)
        val log = mutableListOf<String>()

        queue.enqueue { throw IllegalStateException("camera refused the move") }
        queue.enqueue { log.add("stop") }
        queue.awaitIdle()

        assertEquals(listOf("stop"), log)
    }
}

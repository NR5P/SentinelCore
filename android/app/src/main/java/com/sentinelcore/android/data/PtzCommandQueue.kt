package com.sentinelcore.android.data

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

// Runs PTZ commands one at a time, in the order they were issued.
//
// A press and a release used to launch independent coroutines, which is two HTTP requests with no
// ordering between them. On a quick tap both are in flight at once, and whenever the stop won that
// race the camera was sent stop-then-move and kept turning - the intermittent "sticks and spins"
// this was reported for. It hid on a long press, where the release lands long after the move.
//
// Chaining each command onto the previous one rather than holding a queue of closures keeps this
// to a few lines, and it is self-limiting: a finished job returns from join() immediately, so a
// burst of taps drains as fast as the requests complete rather than accumulating.
//
// The scope belongs to the caller on purpose. It has to outlive the control that issues the
// commands - a release launched from a composable's own scope is cancelled when that composable
// leaves, which could cancel the very stop the press depended on.
class PtzCommandQueue(private val scope: CoroutineScope) {
    private var pending: Job? = null

    fun enqueue(block: suspend () -> Unit) {
        val previous = pending
        // Failures are contained per command: one command that throws must not break the chain,
        // or the next press would find a cancelled job to join and never run.
        pending = scope.launch {
            previous?.join()
            runCatching { block() }
        }
    }

    // Waits for everything enqueued so far. Only the tests need this - the app fires and forgets.
    suspend fun awaitIdle() {
        pending?.join()
    }
}

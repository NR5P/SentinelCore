package com.sentinelcore.android.audio

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.withTimeoutOrNull
import java.io.IOException

// withTimeout can't cut short a request: the app's HTTP calls block an IO thread
// (HttpURLConnection), and a cancelled withContext still waits for its block to finish, so a
// "20 second" limit really lasts as long as the read timeout - a minute. For the car, where a
// spinner is all the driver sees, the wait itself has to stop: the call runs detached and is
// simply not waited for past the limit. It finishes (or times out) on its own in the background.
private val detached = CoroutineScope(SupervisorJob() + Dispatchers.IO)

internal suspend fun <T> withHardTimeout(limitMillis: Long, block: suspend () -> T): T {
    val call = detached.async { block() }
    // Boxed, because the call's own answer may be null ("nothing to report") and that must not
    // read as having run out of time.
    val answer = withTimeoutOrNull(limitMillis) { Result.success(call.await()) }
    if (answer == null) {
        call.cancel()
        throw IOException("No answer within ${limitMillis / 1000} seconds")
    }
    return answer.getOrThrow()
}

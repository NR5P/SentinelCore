package com.sentinelcore.android.audio

// How much of a track was actually heard, for `secondsListened` (§4). It sums forward moves of
// the media position between samples rather than wall-clock time, so a stall, buffering, or a
// slow start never inflates it. A forward move bigger than the time that passed allows (plus a
// little slack) is a seek or a jump, not listening, and is ignored; backward moves are ignored.
// The total never exceeds the track's length.
class ListenMeter(private val maxJumpSeconds: Double = DEFAULT_MAX_JUMP_SECONDS) {
    var listenedSeconds = 0.0
        private set

    // The position the last sample saw; null until the first sample (or after a rebase to unknown).
    private var lastPosition: Double? = null

    // A new position reading. [elapsedSeconds] is the wall-clock time since the previous sample
    // and [speed] the playback rate, which together bound how far playback can honestly move.
    fun sample(positionSeconds: Double, elapsedSeconds: Double, speed: Double = 1.0) {
        val last = lastPosition
        lastPosition = positionSeconds
        if (last == null) return
        val delta = positionSeconds - last
        if (delta <= 0) return
        val allowed = maxOf(maxJumpSeconds, elapsedSeconds.coerceAtLeast(0.0) * speed.coerceAtLeast(1.0) + SLACK_SECONDS)
        if (delta <= allowed) listenedSeconds += delta
    }

    // A seek: playback continues from [positionSeconds] without counting the jump.
    fun rebase(positionSeconds: Double) {
        lastPosition = positionSeconds
    }

    // The total to report. When the track played to its end, the short stretch after the last
    // sample (up to the end) counts too. Capped at [durationSeconds] when that is known.
    fun total(durationSeconds: Double, reachedEnd: Boolean): Double {
        var total = listenedSeconds
        val last = lastPosition
        if (reachedEnd && durationSeconds > 0 && last != null) {
            val tail = durationSeconds - last
            if (tail > 0 && tail <= maxJumpSeconds) total += tail
        }
        return if (durationSeconds > 0) total.coerceAtMost(durationSeconds) else total
    }

    companion object {
        const val DEFAULT_MAX_JUMP_SECONDS = 5.0
        private const val SLACK_SECONDS = 2.0
    }
}

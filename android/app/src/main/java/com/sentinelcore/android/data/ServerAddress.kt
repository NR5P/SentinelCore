package com.sentinelcore.android.data

import java.io.IOException
import java.net.URI
import java.time.Instant
import java.time.OffsetDateTime

// Pure parsing helpers pulled out of SentinelRepository so they can be unit tested without an
// Android runtime. Both of these decide whether the app talks to the server at all - a base URL
// that normalizes wrong shows "Enter a valid server address" on a perfectly good address, and a
// timestamp that fails to parse takes down a whole event list - so they're worth pinning down.
internal object ServerAddress {
    /**
     * Returns the address to use as the API base, or null if it isn't usable.
     *
     * Only http/https with a real host qualify. Trailing slashes are dropped so callers can
     * concatenate paths that start with "/" without doubling the separator.
     */
    fun normalize(raw: String): String? {
        val trimmed = raw.trim().trimEnd('/')
        if (trimmed.isBlank()) return null
        val uri = runCatching { URI(trimmed) }.getOrNull() ?: return null
        if (uri.scheme !in setOf("http", "https") || uri.host.isNullOrBlank()) return null
        return trimmed
    }
}

internal object ServerTimestamp {
    /**
     * The server sends UTC instants ("...Z") for some fields and offset date-times for others.
     * Accept both; anything else is a genuine protocol error worth surfacing.
     */
    fun parse(value: String): Instant =
        runCatching { Instant.parse(value) }
            .recoverCatching { OffsetDateTime.parse(value).toInstant() }
            .getOrElse { throw IOException("Invalid server timestamp: $value") }
}

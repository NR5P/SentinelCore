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

    /**
     * The plain-HTTP address to retry an untrusted https:// address on, or null if there isn't one.
     *
     * `https://host[:port]` -> `http://host:3320`, the plain-HTTP port the dashboard ships with.
     * A custom HTTP port can't be derived from the HTTPS one, so the shipped default is the only
     * sensible guess - callers probe the result before switching to it.
     */
    fun httpFallback(address: String): String? {
        val uri = runCatching { URI(normalize(address) ?: return null) }.getOrNull() ?: return null
        if (uri.scheme != "https") return null
        val host = uri.host?.takeUnless(String::isBlank) ?: return null
        return "http://$host:$DefaultHttpPort"
    }

    /**
     * The https:// form of a plain-http address, or null when it can't be guessed.
     *
     * Deliberately narrow: swapping the scheme is only sound advice when no port is pinned. On
     * `http://host:3320` the https counterpart is a *different* listener on a different port
     * (3443), not the same one with TLS, so that case gets no guess rather than a wrong one.
     */
    fun httpsEquivalent(address: String): String? {
        val uri = runCatching { URI(normalize(address) ?: return null) }.getOrNull() ?: return null
        if (uri.scheme != "http" || uri.port != -1) return null
        val host = uri.host?.takeUnless(String::isBlank) ?: return null
        return "https://$host"
    }

    private const val DefaultHttpPort = 3320
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

package com.sentinelcore.android.data

import java.io.IOException
import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class ServerAddressTest {
    @Test
    fun `keeps a plain lan address unchanged`() {
        assertEquals("http://192.168.1.50:3320", ServerAddress.normalize("http://192.168.1.50:3320"))
    }

    @Test
    fun `trims surrounding whitespace`() {
        assertEquals("http://192.168.1.50:3320", ServerAddress.normalize("  http://192.168.1.50:3320  "))
    }

    // Callers append paths that already start with "/", so a trailing slash here would produce
    // "http://host:3320//api/events".
    @Test
    fun `drops trailing slashes`() {
        assertEquals("http://192.168.1.50:3320", ServerAddress.normalize("http://192.168.1.50:3320/"))
        assertEquals("http://192.168.1.50:3320", ServerAddress.normalize("http://192.168.1.50:3320///"))
    }

    @Test
    fun `accepts https and hostnames`() {
        assertEquals(
            "https://sentinel.example.com",
            ServerAddress.normalize("https://sentinel.example.com")
        )
    }

    @Test
    fun `rejects blank input`() {
        assertNull(ServerAddress.normalize(""))
        assertNull(ServerAddress.normalize("   "))
    }

    // A bare host is the most likely thing someone types; it has no scheme, so it can't be used
    // as a base URL and should be reported rather than silently half-working.
    @Test
    fun `rejects an address with no scheme`() {
        assertNull(ServerAddress.normalize("192.168.1.50:3320"))
    }

    @Test
    fun `rejects non http schemes`() {
        assertNull(ServerAddress.normalize("ftp://192.168.1.50"))
        assertNull(ServerAddress.normalize("rtsp://192.168.1.50:554/stream1"))
    }

    @Test
    fun `rejects a scheme with no host`() {
        assertNull(ServerAddress.normalize("http://"))
    }
}

class ServerTimestampTest {
    @Test
    fun `parses a utc instant`() {
        assertEquals(
            Instant.parse("2026-07-30T18:55:47Z"),
            ServerTimestamp.parse("2026-07-30T18:55:47Z")
        )
    }

    // The server serializes DateTimeOffset, so several fields arrive with a numeric offset
    // rather than "Z" - both shapes have to land on the same instant.
    @Test
    fun `parses an offset date time`() {
        assertEquals(
            Instant.parse("2026-07-30T18:55:47Z"),
            ServerTimestamp.parse("2026-07-30T13:55:47-05:00")
        )
    }

    @Test
    fun `parses fractional seconds`() {
        assertEquals(
            Instant.parse("2026-07-30T18:55:47.638278Z"),
            ServerTimestamp.parse("2026-07-30T18:55:47.638278Z")
        )
    }

    @Test
    fun `throws a readable error on garbage`() {
        val error = assertThrows(IOException::class.java) { ServerTimestamp.parse("not-a-date") }
        assertEquals("Invalid server timestamp: not-a-date", error.message)
    }
}

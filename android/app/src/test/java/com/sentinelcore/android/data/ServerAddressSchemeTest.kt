package com.sentinelcore.android.data

import org.junit.Assert.assertEquals
import org.junit.Test

// A typed address with no scheme: home-network addresses keep http:// (how the dashboard ships),
// internet addresses get https:// so a password never crosses the internet in plain text. The
// App Review demo (demo.nr5p.com) answers on plain HTTP too, which is why this matters.
class ServerAddressSchemeTest {
    private fun normalize(value: String) = SentinelRepository.normalizeAddress(value)

    @Test
    fun `home network addresses default to http`() {
        assertEquals("http://192.168.1.50:3320", normalize("192.168.1.50:3320"))
        assertEquals("http://sentinel.local:3320", normalize("sentinel.local:3320"))
        assertEquals("http://mediabox:3320", normalize("mediabox:3320"))
        assertEquals("http://localhost:3320", normalize("localhost:3320"))
        assertEquals("http://[fe80::1]:3320", normalize("[fe80::1]:3320"))
        assertEquals("http://nas.home.arpa", normalize("nas.home.arpa"))
    }

    @Test
    fun `internet addresses default to https`() {
        assertEquals("https://demo.nr5p.com", normalize("demo.nr5p.com"))
        assertEquals("https://home.example.com:8443", normalize("home.example.com:8443/"))
    }

    @Test
    fun `a typed or forced scheme wins`() {
        assertEquals("http://demo.nr5p.com", normalize("http://demo.nr5p.com"))
        assertEquals("https://192.168.1.50:3443", normalize("https://192.168.1.50:3443"))
        assertEquals("https://192.168.1.50", SentinelRepository.normalizeAddress("192.168.1.50", defaultScheme = "https"))
    }
}

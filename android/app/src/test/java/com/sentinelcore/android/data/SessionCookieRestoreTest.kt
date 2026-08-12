package com.sentinelcore.android.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.CookieManager
import java.net.CookiePolicy
import java.net.URI

// These run against the real java.net.CookieManager, which is the thing whose behaviour the bug
// turned on: it is a JDK class, so a plain JVM unit test exercises the same code the phone does.
class SessionCookieRestoreTest {

    private val origin = URI("https://cam.example.com")
    private val apiCall = URI("https://cam.example.com/api/timeline/day")
    private val clip = URI("https://cam.example.com/api/triggers/abc/clip")

    private fun jar() = CookieManager(null, CookiePolicy.ACCEPT_ALL)

    private fun CookieManager.cookieFor(uri: URI): String =
        get(uri, emptyMap())["Cookie"].orEmpty().joinToString("; ")

    @Test
    fun `each stored cookie comes back as its own Set-Cookie value`() {
        assertEquals(
            listOf(".SentinelCore.Auth=abc; Path=/", "extra=1; Path=/"),
            restoredSetCookieValues(".SentinelCore.Auth=abc; extra=1"),
        )
    }

    @Test
    fun `a blank or padded header restores nothing surprising`() {
        assertEquals(emptyList<String>(), restoredSetCookieValues(""))
        assertEquals(listOf("a=1; Path=/"), restoredSetCookieValues("  a=1 ;; "))
    }

    // The regression itself: restoring during a deep API call used to scope the session to that
    // call's directory, so it matched later /api requests but vanished from a root lookup - which
    // is exactly how ExoPlayer's mediaHeaders() ended up sending no cookie and getting a 401.
    @Test
    fun `restoring without an explicit path hides the session from a root lookup`() {
        val jar = jar()
        jar.put(apiCall, mapOf("Set-Cookie" to listOf(".SentinelCore.Auth=abc")))

        assertTrue(jar.cookieFor(apiCall).contains(".SentinelCore.Auth=abc"))
        assertEquals("", jar.cookieFor(origin))
    }

    @Test
    fun `restoring through the helper keeps the session visible everywhere`() {
        val jar = jar()
        jar.put(apiCall, mapOf("Set-Cookie" to restoredSetCookieValues(".SentinelCore.Auth=abc")))

        assertTrue(jar.cookieFor(origin).contains(".SentinelCore.Auth=abc"))
        assertTrue(jar.cookieFor(apiCall).contains(".SentinelCore.Auth=abc"))
        assertTrue(jar.cookieFor(clip).contains(".SentinelCore.Auth=abc"))
    }

    @Test
    fun `every cookie in a multi-cookie session survives the round trip`() {
        val jar = jar()
        jar.put(apiCall, mapOf("Set-Cookie" to restoredSetCookieValues(".SentinelCore.Auth=abc; theme=dark")))

        val atRoot = jar.cookieFor(origin)
        assertTrue(atRoot.contains(".SentinelCore.Auth=abc"))
        assertTrue(atRoot.contains("theme=dark"))
    }

    // The behaviour the detection exists for, pinned against the real CookieManager: a Secure
    // cookie handed over an http:// URL is accepted without complaint and then never sent again.
    @Test
    fun `a secure cookie delivered over http is accepted and then withheld`() {
        val jar = jar()
        val insecureOrigin = URI("http://cam.example.com")
        jar.put(insecureOrigin, mapOf("Set-Cookie" to listOf("session=abc; path=/; secure; httponly")))

        assertEquals("", jar.cookieFor(insecureOrigin))
        assertTrue(carriesUnusableSecureCookie("http", listOf("session=abc; path=/; secure; httponly")))
    }

    @Test
    fun `a secure cookie over https is exactly what should happen`() {
        assertFalse(carriesUnusableSecureCookie("https", listOf("session=abc; path=/; secure; httponly")))
    }

    @Test
    fun `a plain cookie over http is not the problem being looked for`() {
        assertFalse(carriesUnusableSecureCookie("http", listOf("session=abc; path=/; httponly")))
        assertFalse(carriesUnusableSecureCookie("http", emptyList()))
    }

    // Logout answers with an expired, empty-valued cookie carrying the same Secure attribute.
    // Reading that as a lost session would fail a sign-out that worked perfectly.
    @Test
    fun `a deletion cookie is not a lost session`() {
        assertFalse(
            carriesUnusableSecureCookie(
                "http",
                listOf("session=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure; samesite=lax; httponly"),
            ),
        )
    }
}

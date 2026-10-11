package com.sentinelcore.android.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.security.cert.X509Certificate

// The pure half of ServerIdentity: whether a remembered authority signed a certificate. The
// fixtures are real certificates (openssl PEM, valid to 2126; .txt because the repo ignores
// *.pem): a local CA, a server certificate it issued, and a second CA with the very same name -
// a reinstalled server, or an impostor.
class ServerIdentityTest {
    private fun fixture(name: String): ByteArray =
        requireNotNull(javaClass.classLoader?.getResourceAsStream(name)) { "missing $name" }.use { it.readBytes() }

    private fun certificate(name: String): X509Certificate =
        requireNotNull(ServerIdentity.parseCertificate(fixture(name)))

    @Test
    fun `only the authority that issued a certificate signs it`() {
        val authority = certificate("server-identity-ca.cert.txt")
        val sameName = certificate("server-identity-other-ca.cert.txt")
        val leaf = certificate("server-identity-leaf.cert.txt")

        assertTrue(ServerIdentity.signs(authority, leaf))
        assertFalse(ServerIdentity.signs(sameName, leaf))
        // A CA certificate presented as the server's own is not "signed by" a different CA.
        assertFalse(ServerIdentity.signs(authority, sameName))
    }

    @Test
    fun `the authority is read as served, PEM or DER`() {
        val pem = certificate("server-identity-ca.cert.txt")
        val der = ServerIdentity.parseCertificate(pem.encoded)

        assertNotNull(der)
        assertEquals(pem, der)
        assertNull(ServerIdentity.parseCertificate("<html>not found</html>".toByteArray()))
    }

    @Test
    fun `one remembered authority per host and port`() {
        assertEquals("192.168.1.50:3443", ServerIdentity.storageKey("https://192.168.1.50:3443/"))
        assertEquals("192.168.1.50:3443", ServerIdentity.storageKey("https://192.168.1.50:3443"))
        assertEquals("media.local:443", ServerIdentity.storageKey("https://Media.local"))
        assertEquals("192.168.1.50:3320", ServerIdentity.storageKey("http://192.168.1.50:3320"))
    }
}
